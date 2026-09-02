import json
import os
import ipaddress
import importlib
import socket
import subprocess
import threading
import time
import unicodedata

import addonHandler
import core
import globalPluginHandler
import globalVars
import gui
import ui
import wx
from gui import guiHelper
from logHandler import log
from scriptHandler import script

from . import server_installer

addonHandler.initTranslation()

ADDON = addonHandler.getCodeAddon()
ADDON_DIR = ADDON.path
HELPER_PATH = os.path.join(ADDON_DIR, "bin", "NVDARemoteAudioHelper.exe")
CONFIG_PATH = os.path.join(globalVars.appArgs.configPath, "remoteAudioClient.json")

DEFAULT_CONFIG = {
	"host": "127.0.0.1",
	"port": 6838,
	"key": "",
	"bitrate": 128000,
	"captureProcess": "",
	"outputDeviceId": "",
	"receiveVolume": 100,
	"receivePan": 0,
	"bassDb": 0,
	"midDb": 0,
	"trebleDb": 0,
	"password": "",
	"qualityMode": "adaptive",
	"recordReceived": False,
	"recordingFolder": os.path.join(os.path.expanduser("~"), "Documents", "NVDA Remote Audio Recordings"),
	"startupMode": "auto",
	"latencyProfile": "auto",
	"announceStatus": True,
	"useFec": True,
	"verboseLogging": False,
	"profiles": {},
	"activeProfile": "",
}

# Mirrors NVDARemoteAudioServer's server-side rules so we surface a friendly
# message before the helper EXE even starts. Server source: validate_key() in
# https://github.com/haitun001/NVDARemoteAudioServer src/protocol.rs.
MAX_KEY_BYTES = 128


def _validateKey(key):
	"""Return a translated error string if key violates server rules, else None.

	An empty string is treated as "not set" by callers and is reported as such.
	"""
	if not isinstance(key, str) or not key:
		return _("Remote audio key is not set")
	if any(unicodedata.category(c) == "Cc" for c in key):
		return _("Remote audio key contains control characters (such as tab or newline)")
	if len(key.encode("utf-8")) > MAX_KEY_BYTES:
		return _("Remote audio key is too long; must be at most {n} UTF-8 bytes").format(n=MAX_KEY_BYTES)
	return None


STARTUP_MODES = ("auto", "disabled", "subscriber", "publisher")
LATENCY_PROFILES = ("auto", "lan", "tailscale", "internet")
QUALITY_MODES = ("adaptive", "opusLive", "opusBroadcast", "pcm")
LATENCY_SETTINGS = {
	# LAN uses 5 ms Opus frames and a small WASAPI event-sync playout target.
	"lan": {"prebufferMs": 15, "outputLatencyMs": 15, "bufferMs": 120, "opusFrameMs": 5},
	"tailscale": {"prebufferMs": 50, "outputLatencyMs": 20, "bufferMs": 250, "opusFrameMs": 10},
	"internet": {"prebufferMs": 100, "outputLatencyMs": 30, "bufferMs": 600, "opusFrameMs": 10},
}

# Tailscale hands every machine an address out of the CGNAT range, and routes
# its MagicDNS resolver over the tailnet interface. Connecting a UDP socket to
# that resolver therefore binds a local address that IS this machine's tailnet
# address, without sending a packet, shelling out to the Tailscale CLI, or
# requiring Tailscale to be installed in any particular place.
TAILSCALE_CGNAT_NETWORK = ipaddress.ip_network("100.64.0.0/10")
TAILSCALE_PROBE_ADDRESS = ("100.100.100.100", 53)
# Any routable address will do to find the interface Windows would use to leave
# this machine; nothing is sent, so the host never has to answer or even exist.
LAN_PROBE_ADDRESS = ("8.8.8.8", 53)
ADDRESS_PROBE_TIMEOUT_SEC = 1.0

RESUME_MONITOR_INTERVAL_MS = 30000
RESUME_GAP_SECONDS = 90
RESUME_SETTLE_MS = 1500


def _detachMenuAfterReload(menuOwner, toolsMenu, menuRoot, menu):
	"""Remove a stale Tools-menu item while retaining its unsafe wx wrapper."""
	try:
		if menuRoot is not None:
			toolsMenu.Remove(menuRoot)
	except Exception:
		log.debug("Failed to remove remote audio menu item", exc_info=True)
	# Calling menu.Destroy(), or allowing the detached wx.Menu wrapper to be
	# garbage-collected during a reload, terminates NVDA in native wxWidgets
	# code. Keep the wrappers alive on the process-lifetime tray object; NVDA
	# safely releases all of them when the tray icon is destroyed on exit.
	retiredMenus = getattr(menuOwner, "_remoteAudioRetiredMenus", None)
	if retiredMenus is None:
		retiredMenus = []
		setattr(menuOwner, "_remoteAudioRetiredMenus", retiredMenus)
	retiredMenus.append((menuRoot, menu))


def _loadConfig():
	config = dict(DEFAULT_CONFIG)
	try:
		with open(CONFIG_PATH, "r", encoding="utf-8-sig") as f:
			loaded = json.load(f)
		if isinstance(loaded, dict):
			config.update(loaded)
	except FileNotFoundError:
		pass
	except Exception:
		log.error("Failed to load remote audio client config", exc_info=True)
	return _normalizeConfig(config)


def _saveConfig(config):
	config = _normalizeConfig(config)
	tmpPath = CONFIG_PATH + ".tmp"
	with open(tmpPath, "w", encoding="utf-8") as f:
		json.dump(config, f, ensure_ascii=False, indent=2)
	os.replace(tmpPath, CONFIG_PATH)
	return config


def _normalizeConfig(config):
	def clampInt(value, default, minimum, maximum):
		try:
			value = int(value)
		except Exception:
			return default
		return max(minimum, min(maximum, value))

	def asBool(value, default):
		if isinstance(value, bool):
			return value
		if value is None:
			return default
		if isinstance(value, str):
			value = value.strip().lower()
			if value in ("1", "true", "yes", "on"):
				return True
			if value in ("0", "false", "no", "off"):
				return False
		return bool(value)

	normalized = {
		"host": str(config.get("host") or DEFAULT_CONFIG["host"]).strip() or DEFAULT_CONFIG["host"],
		"port": clampInt(config.get("port"), DEFAULT_CONFIG["port"], 1, 65535),
		"key": str(config.get("key") if config.get("key") is not None else DEFAULT_CONFIG["key"]),
		"bitrate": clampInt(config.get("bitrate"), DEFAULT_CONFIG["bitrate"], 16000, 510000),
		"captureProcess": str(config.get("captureProcess") or "").strip().lower(),
		"outputDeviceId": str(config.get("outputDeviceId") or "").strip(),
		"receiveVolume": clampInt(config.get("receiveVolume"), DEFAULT_CONFIG["receiveVolume"], 0, 200),
		"receivePan": clampInt(config.get("receivePan"), DEFAULT_CONFIG["receivePan"], -100, 100),
		"bassDb": clampInt(config.get("bassDb"), DEFAULT_CONFIG["bassDb"], -12, 12),
		"midDb": clampInt(config.get("midDb"), DEFAULT_CONFIG["midDb"], -12, 12),
		"trebleDb": clampInt(config.get("trebleDb"), DEFAULT_CONFIG["trebleDb"], -12, 12),
		"password": str(config.get("password") or ""),
		"qualityMode": config.get("qualityMode") if config.get("qualityMode") in QUALITY_MODES else DEFAULT_CONFIG["qualityMode"],
		"recordReceived": asBool(config.get("recordReceived"), DEFAULT_CONFIG["recordReceived"]),
		"recordingFolder": str(config.get("recordingFolder") or DEFAULT_CONFIG["recordingFolder"]).strip(),
		"startupMode": config.get("startupMode") if config.get("startupMode") in STARTUP_MODES else DEFAULT_CONFIG["startupMode"],
		"latencyProfile": config.get("latencyProfile") if config.get("latencyProfile") in LATENCY_PROFILES else DEFAULT_CONFIG["latencyProfile"],
		"announceStatus": asBool(config.get("announceStatus"), DEFAULT_CONFIG["announceStatus"]),
		"useFec": asBool(config.get("useFec"), DEFAULT_CONFIG["useFec"]),
		"verboseLogging": asBool(config.get("verboseLogging"), DEFAULT_CONFIG["verboseLogging"]),
	}
	profiles = {}
	rawProfiles = config.get("profiles") if isinstance(config.get("profiles"), dict) else {}
	for name, profile in rawProfiles.items():
		name = str(name).strip()
		if not name or not isinstance(profile, dict):
			continue
		profileData = dict(profile)
		profileData["profiles"] = {}
		normalizedProfile = _normalizeConfig(profileData)
		normalizedProfile.pop("profiles", None)
		normalizedProfile.pop("activeProfile", None)
		profiles[name] = normalizedProfile
	normalized["profiles"] = profiles
	activeProfile = str(config.get("activeProfile") or "").strip()
	normalized["activeProfile"] = activeProfile if activeProfile in profiles else ""
	return normalized


def _startupModeLabel(mode):
	return {
		"auto": _("Automatic: server sends, client receives"),
		"disabled": _("Do not connect automatically"),
		"subscriber": _("Receive remote audio"),
		"publisher": _("Send this computer's audio"),
	}.get(mode, _("Automatic: server sends, client receives"))


def _isAudioServerMachine():
	return server_installer.is_installed()


def _resolveStartupMode(config):
	mode = config.get("startupMode", "auto")
	if mode == "auto":
		return "publisher" if _isAudioServerMachine() else "subscriber"
	return mode


def _latencyProfileLabel(profile):
	return {
		"auto": _("Automatic"),
		"lan": _("LAN: lowest latency"),
		"tailscale": _("Tailscale: low latency"),
		"internet": _("Internet: stable"),
	}.get(profile, _("Automatic"))


def _resolveLatencyProfile(config):
	profile = config.get("latencyProfile", "auto")
	if profile != "auto":
		return profile

	host = str(config.get("host") or "").strip().lower()
	if host in ("localhost", "127.0.0.1", "::1"):
		return "lan"
	if host.endswith(".ts.net") or host.endswith(".beta.tailscale.net"):
		return "tailscale"
	try:
		ip = ipaddress.ip_address(host.strip("[]"))
	except Exception:
		return "internet"
	if ip.version == 4 and ip in TAILSCALE_CGNAT_NETWORK:
		return "tailscale"
	if ip.is_private or ip.is_loopback or ip.is_link_local:
		return "lan"
	return "internet"


def _probeLocalAddress(target):
	"""Return the local IPv4 address Windows would use to reach target, or None.

	Connecting a UDP socket assigns a local address without transmitting
	anything, so this is free and cannot be blocked by a firewall.
	"""
	sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
	try:
		sock.settimeout(ADDRESS_PROBE_TIMEOUT_SEC)
		sock.connect(target)
		return sock.getsockname()[0]
	except OSError:
		return None
	finally:
		try:
			sock.close()
		except OSError:
			pass


def _detectTailscaleAddress():
	"""This machine's Tailscale IPv4 address, or None when Tailscale is down."""
	address = _probeLocalAddress(TAILSCALE_PROBE_ADDRESS)
	if not address:
		return None
	try:
		if ipaddress.ip_address(address) in TAILSCALE_CGNAT_NETWORK:
			return address
	except ValueError:
		pass
	return None


def _hostnameAddress():
	"""This machine's IPv4 address by name, for a LAN with no default route.

	The probe below needs a route to somewhere to reveal an interface. A machine
	on an isolated switch has none, yet is exactly the case where the other
	computer has to be told an address by hand.
	"""
	try:
		_name, _aliases, addresses = socket.gethostbyname_ex(socket.gethostname())
	except OSError:
		return None
	for address in addresses:
		try:
			ip = ipaddress.ip_address(address)
		except ValueError:
			continue
		if ip.version == 4 and not ip.is_loopback and ip not in TAILSCALE_CGNAT_NETWORK:
			return address
	return None


def _detectLanAddress():
	"""This machine's IPv4 address on the network it would leave by, or None."""
	address = _probeLocalAddress(LAN_PROBE_ADDRESS) or _hostnameAddress()
	if not address:
		return None
	try:
		ip = ipaddress.ip_address(address)
	except ValueError:
		return None
	# A tailnet address here means Tailscale is carrying the default route. It is
	# already reported separately, and repeating it as "the local network
	# address" would tell the user to type the same thing twice.
	if ip in TAILSCALE_CGNAT_NETWORK:
		return _hostnameAddress()
	return address


def _localAddresses():
	"""The addresses another computer could use to reach this one."""
	return {
		"hostname": socket.gethostname(),
		"tailscale": _detectTailscaleAddress(),
		"lan": _detectLanAddress(),
	}


def _localAddressReport(addresses=None, port=None):
	"""A short spoken and copyable answer to "what do I type on the other computer?"."""
	addresses = _localAddresses() if addresses is None else addresses
	port = DEFAULT_CONFIG["port"] if port is None else port
	lines = []
	if addresses.get("tailscale"):
		# Translators: reported address of this computer over Tailscale.
		lines.append(_("Tailscale address: {address}, port {port}").format(
			address=addresses["tailscale"], port=port))
	if addresses.get("lan"):
		# Translators: reported address of this computer on the local network.
		lines.append(_("Local network address: {address}, port {port}").format(
			address=addresses["lan"], port=port))
	if addresses.get("hostname"):
		# Translators: reported computer name.
		lines.append(_("Computer name: {name}").format(name=addresses["hostname"]))
	if not lines:
		# Translators: reported when no usable address for this computer was found.
		return _("No network address for this computer could be found")
	return "\n".join(lines)


def _latencySettings(config):
	return LATENCY_SETTINGS[_resolveLatencyProfile(config)]


def _qualityModeLabel(mode):
	return {
		"adaptive": _("Opus: follow latency profile"),
		"opusLive": _("Opus: live, lowest codec delay"),
		"opusBroadcast": _("Opus: broadcast quality"),
		"pcm": _("PCM: uncompressed LAN quality"),
	}.get(mode, _("Opus: follow latency profile"))


def _qualitySettings(config):
	mode = config.get("qualityMode", "adaptive")
	if mode == "opusLive":
		return {"codec": "opus", "frameMs": 5}
	if mode == "opusBroadcast":
		return {"codec": "opus", "frameMs": 20}
	if mode == "pcm":
		return {"codec": "pcm", "frameMs": 5}
	return {"codec": "opus", "frameMs": _latencySettings(config)["opusFrameMs"]}


def _profileSnapshot(config):
	return {
		key: value
		for key, value in config.items()
		if key not in ("profiles", "activeProfile")
	}


def _queryHelperCatalog(flag, eventName, collectionName):
	"""Return a live helper catalog, or an empty list if discovery is unavailable."""
	if not os.path.exists(HELPER_PATH):
		return []
	try:
		startupinfo = subprocess.STARTUPINFO()
		startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
		completed = subprocess.run(
			[HELPER_PATH, flag],
			stdin=subprocess.DEVNULL,
			stdout=subprocess.PIPE,
			stderr=subprocess.STDOUT,
			text=True,
			encoding="utf-8",
			errors="replace",
			timeout=5,
			creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
			startupinfo=startupinfo,
			check=False,
		)
		for line in completed.stdout.splitlines():
			try:
				payload = json.loads(line)
			except Exception:
				continue
			if payload.get("event") == eventName and isinstance(payload.get(collectionName), list):
				return payload[collectionName]
		if completed.returncode:
			log.warning("Remote audio helper catalog %s failed: %s", flag, completed.stdout.strip())
	except Exception:
		log.debug("Remote audio helper catalog %s failed", flag, exc_info=True)
	return []


def _audioApps():
	apps = _queryHelperCatalog("--list-audio-apps", "audio_apps", "apps")
	return [app for app in apps if isinstance(app, dict) and app.get("ProcessName")]


def _outputDevices():
	devices = _queryHelperCatalog("--list-output-devices", "output_devices", "devices")
	return [device for device in devices if isinstance(device, dict) and device.get("Id")]


def _runHelperSelfTest():
	if not os.path.exists(HELPER_PATH):
		return False, _("Remote audio helper is missing")
	try:
		startupinfo = subprocess.STARTUPINFO()
		startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
		completed = subprocess.run(
			[HELPER_PATH, "--self-test"],
			stdin=subprocess.DEVNULL,
			stdout=subprocess.PIPE,
			stderr=subprocess.STDOUT,
			text=True,
			encoding="utf-8",
			errors="replace",
			timeout=30,
			creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
			startupinfo=startupinfo,
			check=False,
		)
		for line in completed.stdout.splitlines():
			try:
				payload = json.loads(line)
			except Exception:
				continue
			if payload.get("event") == "self_test":
				return True, str(payload.get("message") or _("All helper self-tests passed"))
		return False, completed.stdout.strip() or _("Helper self-test failed")
	except Exception as e:
		log.error("Remote audio helper self-test failed", exc_info=True)
		return False, str(e)


class AudioClientProcess:
	def __init__(self, exitCallback=None):
		self._process = None
		self._readerThread = None
		self._role = None
		self._lastMessage = _("Not connected")
		self._startedAt = None
		self._stopping = False
		self._exitCallback = exitCallback
		self._announceStatus = DEFAULT_CONFIG["announceStatus"]
		self._verboseLogging = DEFAULT_CONFIG["verboseLogging"]
		self._lastEvent = {}
		self._lastDiagnostics = {}

	def isRunning(self):
		return self._process is not None and self._process.poll() is None

	def currentRole(self):
		"""Return 'subscriber', 'publisher', or None if not running."""
		if not self.isRunning():
			return None
		return self._role

	def start(self, role, config):
		self.stop()
		self._announceStatus = bool(config.get("announceStatus", DEFAULT_CONFIG["announceStatus"]))
		self._verboseLogging = bool(config.get("verboseLogging", DEFAULT_CONFIG["verboseLogging"]))
		if not os.path.exists(HELPER_PATH):
			self._speak(_("Remote audio helper is missing"))
			return
		keyError = _validateKey(str(config.get("key") or "").strip())
		if keyError is not None:
			self._speak(keyError)
			return

		self._role = role
		self._lastMessage = _("Connecting")
		self._startedAt = time.time()
		self._stopping = False

		args = [
			HELPER_PATH,
			"--role", role,
			"--host", config["host"],
			"--port", str(config["port"]),
			"--key", config["key"],
		]
		latency = _latencySettings(config)
		quality = _qualitySettings(config)
		args.extend([
			"--opus-frame-ms", str(quality["frameMs"]),
			"--codec", quality["codec"],
		])
		password = str(config.get("password") or "")
		childEnv = None
		if password:
			passwordEnvName = "NVDA_REMOTE_AUDIO_PASSWORD"
			args.extend(["--password-env", passwordEnvName])
			childEnv = os.environ.copy()
			childEnv[passwordEnvName] = password
		if not config.get("useFec", DEFAULT_CONFIG["useFec"]):
			args.append("--disable-fec")
		if role == "publisher":
			captureProcess = str(config.get("captureProcess") or "").strip()
			if captureProcess:
				args.extend(["--include-process-name", captureProcess])
			else:
				args.extend(["--exclude-pid", str(os.getpid())])
			args.extend(["--bitrate", str(config["bitrate"])])
		else:
			args.extend([
				"--prebuffer-ms", str(latency["prebufferMs"]),
				"--output-latency-ms", str(latency["outputLatencyMs"]),
				"--buffer-ms", str(latency["bufferMs"]),
				"--receive-volume", str(config.get("receiveVolume", DEFAULT_CONFIG["receiveVolume"])),
				"--receive-pan", str(config.get("receivePan", DEFAULT_CONFIG["receivePan"])),
				"--bass-db", str(config.get("bassDb", DEFAULT_CONFIG["bassDb"])),
				"--mid-db", str(config.get("midDb", DEFAULT_CONFIG["midDb"])),
				"--treble-db", str(config.get("trebleDb", DEFAULT_CONFIG["trebleDb"])),
			])
			outputDeviceId = str(config.get("outputDeviceId") or "").strip()
			if outputDeviceId:
				args.extend(["--output-device-id", outputDeviceId])
			if config.get("recordReceived") and str(config.get("recordingFolder") or "").strip():
				args.extend(["--record-folder", str(config["recordingFolder"]).strip()])

		try:
			startupinfo = subprocess.STARTUPINFO()
			startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
			# stdin is a pipe so we can signal a graceful shutdown by closing it.
			# The helper watches stdin and treats EOF (or any byte) as "shut down,
			# release WASAPI handles, close sockets, exit".
			self._process = subprocess.Popen(
				args,
				stdin=subprocess.PIPE,
				stdout=subprocess.PIPE,
				stderr=subprocess.STDOUT,
				text=True,
				encoding="utf-8",
				errors="replace",
				creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
				startupinfo=startupinfo,
				env=childEnv,
			)
		except Exception as e:
			self._process = None
			log.error("Failed to start remote audio helper", exc_info=True)
			self._speak(_("Failed to start remote audio: {error}").format(error=e))
			return

		self._readerThread = threading.Thread(target=self._readOutput, name="remoteAudioClientOutput", daemon=True)
		self._readerThread.start()
		if role == "subscriber":
			self._speakStatus(_("Connecting to remote audio"))
		else:
			self._speakStatus(_("Sending this computer's audio"))

	def _speak(self, message):
		if wx.IsMainThread():
			ui.message(message)
		else:
			wx.CallAfter(ui.message, message)

	def _speakStatus(self, message):
		if self._announceStatus:
			self._speak(message)

	def _queueStatus(self, message):
		if self._announceStatus:
			wx.CallAfter(ui.message, message)

	def stop(self, wait=True):
		process = self._process
		if process is None:
			return
		self._stopping = True
		self._process = None
		self._role = None
		self._lastMessage = _("Not connected")
		self._startedAt = None
		if process.poll() is None:
			# Graceful shutdown: close stdin, helper sees EOF and tears the session down,
			# releasing WASAPI process-loopback handles and closing TCP/UDP cleanly.
			# During NVDA shutdown, don't wait on a child process from the main thread.
			try:
				if process.stdin is not None:
					try:
						process.stdin.close()
					except Exception:
						pass
				if not wait:
					try:
						process.terminate()
					except Exception:
						pass
					return
				try:
					process.wait(timeout=2)
				except subprocess.TimeoutExpired:
					try:
						process.terminate()
						process.wait(timeout=2)
					except Exception:
						try:
							process.kill()
						except Exception:
							pass
			except Exception:
				try:
					process.kill()
				except Exception:
					pass

	def disableExitCallback(self):
		self._exitCallback = None

	def statusMessage(self):
		if not self.isRunning():
			return _("Remote audio is not connected")
		role = _("receiving") if self._role == "subscriber" else _("sending")
		return _("Remote audio {role}: {message}").format(role=role, message=self._lastMessage)

	def diagnosticsSnapshot(self):
		return {
			"running": self.isRunning(),
			"role": self._role or "",
			"lastMessage": self._lastMessage,
			"startedAt": self._startedAt,
			"lastEvent": dict(self._lastEvent),
			"lastDiagnostics": dict(self._lastDiagnostics),
		}

	def _readOutput(self):
		process = self._process
		if process is None or process.stdout is None:
			return
		try:
			for line in process.stdout:
				self._handleLine(line.strip())
		except Exception:
			log.debug("Remote audio helper output reader failed", exc_info=True)
		finally:
			exitCode = process.poll()
			if exitCode is None:
				exitCode = process.wait()
			role = self._role
			stopping = self._stopping
			if not self._stopping and self._process is process:
				self._process = None
				self._role = None
				wx.CallAfter(self._speakStatus, _("Remote audio stopped"))
				if exitCode:
					log.warning("Remote audio helper exited with code %s", exitCode)
			if self._exitCallback is not None:
				wx.CallAfter(self._exitCallback, role, exitCode, stopping)

	def _handleLine(self, line):
		if not line:
			return
		try:
			event = json.loads(line)
		except Exception:
			log.debug("Remote audio helper output: %s", line)
			return
		message = str(event.get("message") or "")
		if message:
			self._lastMessage = message
		eventName = event.get("event")
		self._lastEvent = event
		if eventName == "diagnostic":
			self._lastDiagnostics = event
		if self._verboseLogging or eventName != "diagnostic":
			log.info("remoteAudio helper: %s", line)
		if eventName == "connected":
			if event.get("role") == "subscriber":
				self._queueStatus(_("Remote audio connected for receiving"))
			else:
				self._queueStatus(_("Remote audio connected for sending"))
		elif eventName == "error":
			wx.CallAfter(ui.message, _("Remote audio error: {message}").format(message=message))
			log.error("Remote audio helper error: %s", line)
		elif eventName == "status" and message in ("Capture started.", "Listening for remote audio."):
			self._queueStatus(_(message))
		elif eventName == "recording":
			self._queueStatus(_(message))


class RemoteAudioSettingsPanel(gui.settingsDialogs.SettingsPanel):
	title = _("NVDA Remote Audio")

	def makeSettings(self, settingsSizer):
		self._config = _loadConfig()
		helper = guiHelper.BoxSizerHelper(self, sizer=settingsSizer)

		self.hostCtrl = helper.addLabeledControl(_("Server host:"), wx.TextCtrl)
		self.hostCtrl.SetValue(str(self._config["host"]))

		self.portCtrl = helper.addLabeledControl(
			_("Audio port:"),
			wx.SpinCtrl,
			min=1,
			max=65535,
			initial=int(self._config["port"]),
		)

		self.keyCtrl = helper.addLabeledControl(_("Session key / room name (not a password):"), wx.TextCtrl)
		self.keyCtrl.SetValue(str(self._config["key"]))
		self.passwordCtrl = helper.addLabeledControl(
			_("End-to-end encryption password (optional):"),
			wx.TextCtrl,
			style=wx.TE_PASSWORD,
		)
		self.passwordCtrl.SetValue(str(self._config["password"]))

		self.bitrateCtrl = helper.addLabeledControl(
			_("Send bitrate:"),
			wx.SpinCtrl,
			min=16000,
			max=510000,
			initial=int(self._config["bitrate"]),
		)
		self.qualityChoice = helper.addLabeledControl(
			_("Audio quality:"),
			wx.Choice,
			choices=[_qualityModeLabel(mode) for mode in QUALITY_MODES],
		)
		self.qualityChoice.SetSelection(list(QUALITY_MODES).index(self._config["qualityMode"]))
		self._captureProcessValues = [""]
		captureChoices = [_('System audio (NVDA excluded)')]
		configuredCapture = self._config["captureProcess"]
		for app in _audioApps():
			processName = str(app.get("ProcessName") or "").strip().lower()
			if not processName or processName in self._captureProcessValues:
				continue
			displayName = str(app.get("DisplayName") or processName)
			state = _("playing") if app.get("Playing") else _("idle")
			captureChoices.append(_("{display} ({process}, {state})").format(display=displayName, process=processName, state=state))
			self._captureProcessValues.append(processName)
		if configuredCapture and configuredCapture not in self._captureProcessValues:
			captureChoices.append(_("{process} (not currently available)").format(process=configuredCapture))
			self._captureProcessValues.append(configuredCapture)
		self.captureChoice = helper.addLabeledControl(_("Audio to send:"), wx.Choice, choices=captureChoices)
		self.captureChoice.SetSelection(self._captureProcessValues.index(configuredCapture) if configuredCapture in self._captureProcessValues else 0)

		self._outputDeviceValues = [""]
		outputChoices = [_('Windows default playback device')]
		configuredOutput = self._config["outputDeviceId"]
		for device in _outputDevices():
			deviceId = str(device.get("Id") or "").strip()
			if not deviceId or deviceId in self._outputDeviceValues:
				continue
			outputChoices.append(str(device.get("Name") or deviceId))
			self._outputDeviceValues.append(deviceId)
		if configuredOutput and configuredOutput not in self._outputDeviceValues:
			outputChoices.append(_("Saved playback device (not currently available)"))
			self._outputDeviceValues.append(configuredOutput)
		self.outputDeviceChoice = helper.addLabeledControl(_("Receive through:"), wx.Choice, choices=outputChoices)
		self.outputDeviceChoice.SetSelection(self._outputDeviceValues.index(configuredOutput) if configuredOutput in self._outputDeviceValues else 0)
		self.receiveVolumeCtrl = helper.addLabeledControl(
			_("Receive volume (percent):"),
			wx.SpinCtrl,
			min=0,
			max=200,
			initial=int(self._config["receiveVolume"]),
		)
		self.receivePanCtrl = helper.addLabeledControl(
			_("Receive pan (-100 left, 0 center, 100 right):"),
			wx.SpinCtrl,
			min=-100,
			max=100,
			initial=int(self._config["receivePan"]),
		)
		self.bassCtrl = helper.addLabeledControl(
			_("Receive bass (dB):"),
			wx.SpinCtrl,
			min=-12,
			max=12,
			initial=int(self._config["bassDb"]),
		)
		self.midCtrl = helper.addLabeledControl(
			_("Receive midrange (dB):"),
			wx.SpinCtrl,
			min=-12,
			max=12,
			initial=int(self._config["midDb"]),
		)
		self.trebleCtrl = helper.addLabeledControl(
			_("Receive treble (dB):"),
			wx.SpinCtrl,
			min=-12,
			max=12,
			initial=int(self._config["trebleDb"]),
		)
		self.recordReceivedCheck = helper.addItem(wx.CheckBox(self, label=_("Record received audio to WAV")))
		self.recordReceivedCheck.SetValue(bool(self._config["recordReceived"]))
		self.recordingFolderCtrl = helper.addLabeledControl(_("Recording folder:"), wx.TextCtrl)
		self.recordingFolderCtrl.SetValue(str(self._config["recordingFolder"]))
		self.latencyChoice = helper.addLabeledControl(
			_("Latency profile:"),
			wx.Choice,
			choices=[_latencyProfileLabel(profile) for profile in LATENCY_PROFILES],
		)
		self.latencyChoice.SetSelection(list(LATENCY_PROFILES).index(self._config["latencyProfile"]))
		self.startupChoice = helper.addLabeledControl(
			_("Startup action:"),
			wx.Choice,
			choices=[
				_startupModeLabel("auto"),
				_startupModeLabel("disabled"),
				_startupModeLabel("subscriber"),
				_startupModeLabel("publisher"),
			],
		)
		self.startupChoice.SetSelection(list(STARTUP_MODES).index(self._config["startupMode"]))
		self.announceStatusCheck = helper.addItem(wx.CheckBox(self, label=_("Announce connection status messages")))
		self.announceStatusCheck.SetValue(bool(self._config["announceStatus"]))
		self.useFecCheck = helper.addItem(wx.CheckBox(self, label=_("Use Opus packet-loss recovery")))
		self.useFecCheck.SetValue(bool(self._config["useFec"]))
		self.verboseLoggingCheck = helper.addItem(wx.CheckBox(self, label=_("Verbose diagnostic logging")))
		self.verboseLoggingCheck.SetValue(bool(self._config["verboseLogging"]))

	def isValid(self):
		# Empty key is allowed at save time so the user can come back later, but
		# anything non-empty must already obey the server's key rules.
		key = self.keyCtrl.GetValue()
		if key:
			err = _validateKey(key)
			if err is not None:
				gui.messageBox(err, _("NVDA Remote Audio"), wx.OK | wx.ICON_ERROR)
				self.keyCtrl.SetFocus()
				return False
		return super().isValid()

	def onSave(self):
		config = _loadConfig()
		config.update({
			"host": self.hostCtrl.GetValue(),
			"port": self.portCtrl.GetValue(),
			"key": self.keyCtrl.GetValue(),
			"password": self.passwordCtrl.GetValue(),
			"bitrate": self.bitrateCtrl.GetValue(),
			"qualityMode": QUALITY_MODES[self.qualityChoice.GetSelection()],
			"captureProcess": self._captureProcessValues[self.captureChoice.GetSelection()],
			"outputDeviceId": self._outputDeviceValues[self.outputDeviceChoice.GetSelection()],
			"receiveVolume": self.receiveVolumeCtrl.GetValue(),
			"receivePan": self.receivePanCtrl.GetValue(),
			"bassDb": self.bassCtrl.GetValue(),
			"midDb": self.midCtrl.GetValue(),
			"trebleDb": self.trebleCtrl.GetValue(),
			"recordReceived": self.recordReceivedCheck.GetValue(),
			"recordingFolder": self.recordingFolderCtrl.GetValue(),
			"latencyProfile": LATENCY_PROFILES[self.latencyChoice.GetSelection()],
			"startupMode": STARTUP_MODES[self.startupChoice.GetSelection()],
			"announceStatus": self.announceStatusCheck.GetValue(),
			"useFec": self.useFecCheck.GetValue(),
			"verboseLogging": self.verboseLoggingCheck.GetValue(),
		})
		config["activeProfile"] = ""
		_saveConfig(config)


class GlobalPlugin(globalPluginHandler.GlobalPlugin):
	scriptCategory = _("NVDA Remote Audio")

	def __init__(self, *args, **kwargs):
		super().__init__(*args, **kwargs)
		self._config = _loadConfig()
		self._client = AudioClientProcess(self._onClientExit)
		self._menu = None
		self._menuRoot = None
		self._receiveItem = None
		self._sendItem = None
		self._recordItem = None
		self._autoRole = None
		self._autoStartCall = None
		self._autoRetryCall = None
		self._remoteScriptSyncCall = None
		self._resumeMonitorCall = None
		self._resumeReconnectCall = None
		self._lastResumeMonitorWall = time.time()
		self._manualStop = False
		self._terminating = False
		self._remoteLocalScripts = (
			self.script_receiveRemoteAudio,
			self.script_sendRemoteAudio,
			self.script_disconnectRemoteAudio,
			self.script_reconnectRemoteAudio,
			self.script_reportRemoteAudioStatus,
			self.script_copyRemoteAudioDiagnostics,
			self.script_toggleRemoteAudioRecording,
			self.script_reportLocalAddress,
		)
		if RemoteAudioSettingsPanel not in gui.settingsDialogs.NVDASettingsDialog.categoryClasses:
			gui.settingsDialogs.NVDASettingsDialog.categoryClasses.append(RemoteAudioSettingsPanel)
		self._createMenu()
		if server_installer.is_installed():
			server_installer.ensure_installed_server_ready()
		self._autoStartCall = core.callLater(1500, self._autoStartFromSettings)
		self._remoteScriptSyncCall = core.callLater(1500, self._syncRemoteLocalScripts)
		self._resumeMonitorCall = core.callLater(RESUME_MONITOR_INTERVAL_MS, self._monitorResume)

	def terminate(self):
		log.info("remoteAudioClient terminate starting")
		self._terminating = True
		# NVDA may call terminate while the wx event loop, NVDA Remote, and the
		# system tray menu are already being torn down. Best-effort cleanup keeps
		# reloads from leaving stale timers, menu items, or local-script bindings.
		for callName in ("_autoStartCall", "_autoRetryCall", "_remoteScriptSyncCall", "_resumeMonitorCall", "_resumeReconnectCall"):
			call = getattr(self, callName, None)
			if call is not None:
				try:
					call.Stop()
				except Exception:
					pass
				setattr(self, callName, None)
		try:
			self._removeRemoteLocalScripts()
		except Exception:
			log.debug("remoteAudioClient local-script cleanup failed", exc_info=True)
		# On a real NVDA exit, core._closeAllWindows() destroys the system tray
		# icon (and every menu it owns, including ours) immediately after this
		# method returns, in the same call stack. Touching toolsMenu ourselves
		# at that point is what made Remove() block forever, hanging every
		# restart (observed 2026-07-04 on NVDA 2026.2beta5). core sets
		# core._hasShutdownBeenTriggered before queuing that teardown - NVDA's
		# own bundled NVDA Remote client (_remoteClient/client.py) uses the same
		# private flag to detect a real exit, so it's a stable-enough signal.
		# Only tear the submenu down ourselves for a plugin reload (no exit
		# pending), where NVDA won't destroy the tray icon and a leftover entry
		# would otherwise persist or duplicate.
		if not getattr(core, "_hasShutdownBeenTriggered", False):
			try:
				self._destroyMenu()
			except Exception:
				log.debug("remoteAudioClient menu cleanup failed", exc_info=True)
		try:
			self._client.disableExitCallback()
			self._client.stop(wait=False)
		except Exception:
			log.debug("remoteAudioClient helper stop during terminate failed", exc_info=True)
		try:
			gui.settingsDialogs.NVDASettingsDialog.categoryClasses.remove(RemoteAudioSettingsPanel)
		except Exception:
			pass
		log.info("remoteAudioClient terminate finished")
		super().terminate()

	def _createMenu(self):
		try:
			toolsMenu = gui.mainFrame.sysTrayIcon.toolsMenu
			self._menu = wx.Menu()
			self._receiveItem = self._menu.AppendCheckItem(wx.ID_ANY, _("Receive remote audio"))
			self._sendItem = self._menu.AppendCheckItem(wx.ID_ANY, _("Send this computer's audio"))
			self._menu.AppendSeparator()
			reconnectItem = self._menu.Append(wx.ID_ANY, _("Reconnect audio"))
			stopItem = self._menu.Append(wx.ID_ANY, _("Disconnect audio"))
			self._recordItem = self._menu.AppendCheckItem(wx.ID_ANY, _("Record received audio"))
			openRecordingsItem = self._menu.Append(wx.ID_ANY, _("Open recordings folder"))
			statusItem = self._menu.Append(wx.ID_ANY, _("Audio status"))
			addressItem = self._menu.Append(wx.ID_ANY, _("This computer's address for the other computer"))
			diagnosticsItem = self._menu.Append(wx.ID_ANY, _("Copy audio diagnostics"))
			selfTestItem = self._menu.Append(wx.ID_ANY, _("Run helper self-test"))
			self._menu.AppendSeparator()
			installItem = self._menu.Append(wx.ID_ANY, _("Install audio server (this machine sends audio)..."))
			updateServerItem = self._menu.Append(wx.ID_ANY, _("Update or repair audio server..."))
			firewallItem = self._menu.Append(wx.ID_ANY, _("Add firewall rules for audio server..."))
			removeServerItem = self._menu.Append(wx.ID_ANY, _("Remove or disable audio server..."))
			self._menu.AppendSeparator()
			saveProfileItem = self._menu.Append(wx.ID_ANY, _("Save current settings as profile..."))
			loadProfileItem = self._menu.Append(wx.ID_ANY, _("Load connection profile..."))
			deleteProfileItem = self._menu.Append(wx.ID_ANY, _("Delete connection profile..."))
			self._menu.AppendSeparator()
			settingsItem = self._menu.Append(wx.ID_ANY, _("Audio settings..."))

			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onReceive, self._receiveItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onSend, self._sendItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onReconnect, reconnectItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onStop, stopItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onToggleRecording, self._recordItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onOpenRecordings, openRecordingsItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onStatus, statusItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onReportLocalAddress, addressItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onCopyDiagnostics, diagnosticsItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onSelfTest, selfTestItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onInstallServer, installItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onUpdateServer, updateServerItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onAddFirewallRules, firewallItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onRemoveServer, removeServerItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onSaveProfile, saveProfileItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onLoadProfile, loadProfileItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onDeleteProfile, deleteProfileItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onSettings, settingsItem)
			self._menuRoot = toolsMenu.AppendSubMenu(self._menu, _("NVDA Remote Audio"), _("NVDA Remote Audio"))
			self._updateMenuChecks()
		except Exception:
			log.error("Failed to create remote audio menu", exc_info=True)

	def _updateMenuChecks(self):
		"""Sync the Receive/Send check marks with the current helper role."""
		if self._terminating or self._menu is None or self._receiveItem is None or self._sendItem is None:
			return
		role = self._client.currentRole()
		try:
			self._receiveItem.Check(role == "subscriber")
			self._sendItem.Check(role == "publisher")
			if self._recordItem is not None:
				self._recordItem.Check(bool(_loadConfig().get("recordReceived")))
		except Exception:
			log.debug("Failed to update remote audio menu checks", exc_info=True)

	def _destroyMenu(self):
		menuRoot = self._menuRoot
		menu = self._menu
		menuOwner = gui.mainFrame.sysTrayIcon
		toolsMenu = menuOwner.toolsMenu
		self._menuRoot = None
		self._menu = None
		self._receiveItem = None
		self._sendItem = None
		self._recordItem = None
		try:
			# Reload Add-ons can be invoked from this same Tools menu. Removing
			# or destroying a submenu synchronously while wx is dispatching that
			# menu event can terminate NVDA inside native wxWidgets code. Detach
			# our references now and defer native menu teardown until the current
			# event returns.
			wx.CallAfter(_detachMenuAfterReload, menuOwner, toolsMenu, menuRoot, menu)
		except Exception:
			log.debug("Failed to schedule remote audio menu cleanup", exc_info=True)

	def onReceive(self, event):
		# Toggle: clicking the checked item disconnects.
		if self._client.currentRole() == "subscriber":
			self._stopAndAnnounce()
			return
		self._config = _loadConfig()
		self._manualStop = False
		self._autoRole = None
		self._client.start("subscriber", self._config)
		self._updateMenuChecks()

	def onSend(self, event):
		if self._client.currentRole() == "publisher":
			self._stopAndAnnounce()
			return
		self._config = _loadConfig()
		self._manualStop = False
		self._autoRole = None
		if not server_installer.is_installed():
			server_installer.offer_install(gui.mainFrame, on_done=self._onSendInstallDone)
			return
		self._client.start("publisher", self._config)
		self._updateMenuChecks()

	def _onSendInstallDone(self, success):
		if not success:
			self._updateMenuChecks()
			return
		self._config = _loadConfig()
		self._client.start("publisher", self._config)
		self._updateMenuChecks()

	def _stopAndAnnounce(self):
		wasRunning = self._client.isRunning()
		self._manualStop = True
		self._autoRole = None
		if self._autoRetryCall is not None:
			self._autoRetryCall.Stop()
			self._autoRetryCall = None
		if self._resumeReconnectCall is not None:
			self._resumeReconnectCall.Stop()
			self._resumeReconnectCall = None
		self._client.stop()
		self._updateMenuChecks()
		ui.message(_("Remote audio disconnected") if wasRunning else _("Remote audio is not connected"))

	def onInstallServer(self, event):
		server_installer.offer_install(gui.mainFrame)

	def onUpdateServer(self, event):
		server_installer.offer_update_or_repair(gui.mainFrame)

	def onAddFirewallRules(self, event):
		server_installer.add_firewall_rules_only(gui.mainFrame)

	def onRemoveServer(self, event):
		server_installer.offer_remove(gui.mainFrame)

	def onStop(self, event):
		self._stopAndAnnounce()

	def onReconnect(self, event):
		# Pick a role: prefer the running role, then last auto role, then resolved startup mode.
		role = self._client.currentRole() or self._autoRole
		if role is None:
			self._config = _loadConfig()
			candidate = _resolveStartupMode(self._config)
			if candidate in ("subscriber", "publisher"):
				role = candidate
		if role is None:
			ui.message(_("Pick Receive remote audio or Send this computer's audio first"))
			return
		# Snapshot config now so the worker doesn't race with another menu click.
		self._config = _loadConfig()
		self._manualStop = False
		self._autoRole = role
		ui.message(_("Reconnecting"))
		log.info("remoteAudio reconnect requested; role=%s", role)
		t0 = time.monotonic()
		config = dict(self._config)
		threading.Thread(
			target=self._reconnectWorker,
			args=(role, config, t0),
			name="remoteAudioReconnect",
			daemon=True,
		).start()

	def _reconnectWorker(self, role, config, t0):
		try:
			self._client.start(role, config)
		except Exception:
			log.error("remoteAudio reconnect failed", exc_info=True)
			return
		log.info(
			"remoteAudio reconnect: helper respawned in %.2f s",
			time.monotonic() - t0,
		)
		wx.CallAfter(self._updateMenuChecks)

	def onStatus(self, event):
		ui.message(self._client.statusMessage())

	def onToggleRecording(self, event):
		config = _loadConfig()
		enabled = event.IsChecked() if event is not None and hasattr(event, "IsChecked") else not config.get("recordReceived")
		config["recordReceived"] = bool(enabled)
		self._config = _saveConfig(config)
		self._updateMenuChecks()
		ui.message(_("Received-audio recording enabled") if enabled else _("Received-audio recording disabled"))
		if self._client.currentRole() == "subscriber":
			self.onReconnect(None)

	def onOpenRecordings(self, event):
		folder = str(_loadConfig().get("recordingFolder") or DEFAULT_CONFIG["recordingFolder"])
		try:
			os.makedirs(folder, exist_ok=True)
			os.startfile(folder)
		except Exception as e:
			log.error("Failed to open remote audio recordings folder", exc_info=True)
			ui.message(_("Could not open recordings folder: {error}").format(error=e))

	def onSelfTest(self, event):
		ui.message(_("Running remote audio helper self-test"))
		threading.Thread(target=self._selfTestWorker, name="remoteAudioSelfTest", daemon=True).start()

	def _selfTestWorker(self):
		success, message = _runHelperSelfTest()
		if success:
			wx.CallAfter(ui.message, message)
		else:
			wx.CallAfter(ui.message, _("Remote audio self-test failed: {error}").format(error=message))

	def onReportLocalAddress(self, event):
		# Setting up the receiving computer needs this machine's address, and
		# finding a tailnet address without sight normally means going out to
		# another application for it.
		config = _loadConfig()
		report = _localAddressReport(port=config.get("port"))
		if self._copyToClipboard(report):
			# Translators: announced with the address report after copying it.
			ui.message(_("{report}. Copied to the clipboard.").format(report=report.replace("\n", ". ")))
		else:
			ui.message(report.replace("\n", ". "))

	def onCopyDiagnostics(self, event):
		text = self._diagnosticsText()
		if self._copyToClipboard(text):
			ui.message(_("Remote audio diagnostics copied"))
		else:
			gui.messageBox(text, _("NVDA Remote Audio diagnostics"), wx.OK | wx.ICON_INFORMATION)

	def onSettings(self, event):
		gui.mainFrame.popupSettingsDialog(gui.settingsDialogs.NVDASettingsDialog, RemoteAudioSettingsPanel)

	def onSaveProfile(self, event):
		dialog = wx.TextEntryDialog(
			gui.mainFrame,
			_("Enter a name for these remote audio settings:"),
			_("Save connection profile"),
		)
		try:
			if dialog.ShowModal() != wx.ID_OK:
				return
			name = dialog.GetValue().strip()
		finally:
			dialog.Destroy()
		if not name:
			ui.message(_("Profile name cannot be empty"))
			return
		config = _loadConfig()
		config["profiles"][name] = _profileSnapshot(config)
		config["activeProfile"] = name
		_saveConfig(config)
		ui.message(_("Remote audio profile saved: {name}").format(name=name))

	def _chooseProfile(self, title):
		config = _loadConfig()
		names = sorted(config.get("profiles", {}).keys(), key=str.casefold)
		if not names:
			ui.message(_("No remote audio profiles have been saved"))
			return config, None
		dialog = wx.SingleChoiceDialog(gui.mainFrame, _("Choose a connection profile:"), title, names)
		try:
			if dialog.ShowModal() != wx.ID_OK:
				return config, None
			return config, dialog.GetStringSelection()
		finally:
			dialog.Destroy()

	def onLoadProfile(self, event):
		config, name = self._chooseProfile(_("Load connection profile"))
		if name is None:
			return
		profiles = config["profiles"]
		loaded = dict(profiles[name])
		loaded["profiles"] = profiles
		loaded["activeProfile"] = name
		self._config = _saveConfig(loaded)
		ui.message(_("Remote audio profile loaded: {name}").format(name=name))
		if self._client.isRunning():
			self.onReconnect(None)

	def onDeleteProfile(self, event):
		config, name = self._chooseProfile(_("Delete connection profile"))
		if name is None:
			return
		del config["profiles"][name]
		if config.get("activeProfile") == name:
			config["activeProfile"] = ""
		_saveConfig(config)
		ui.message(_("Remote audio profile deleted: {name}").format(name=name))

	def _copyToClipboard(self, text):
		data = wx.TextDataObject(text)
		if not wx.TheClipboard.Open():
			return False
		try:
			wx.TheClipboard.SetData(data)
			return True
		finally:
			wx.TheClipboard.Close()

	def _diagnosticsText(self):
		config = _loadConfig()
		latencyProfile = _resolveLatencyProfile(config)
		latency = _latencySettings(config)
		client = self._client.diagnosticsSnapshot()
		server = server_installer.server_status()
		started = client.get("startedAt")
		if started:
			runtime = "{0:.0f}s".format(max(0, time.time() - started))
		else:
			runtime = ""
		lines = [
			"NVDA Remote Audio diagnostics",
			"Add-on path: {0}".format(ADDON_DIR),
			"Helper path: {0}".format(HELPER_PATH),
			"Helper exists: {0}".format(os.path.exists(HELPER_PATH)),
			"Configured host: {0}".format(config.get("host")),
			"This computer's Tailscale address: {0}".format(_detectTailscaleAddress() or "none"),
			"This computer's local network address: {0}".format(_detectLanAddress() or "none"),
			"This computer's name: {0}".format(socket.gethostname()),
			"Configured port: {0}".format(config.get("port")),
			"Startup action: {0} (resolved: {1})".format(config.get("startupMode"), _resolveStartupMode(config)),
			"Latency profile: {0} (resolved: {1}; {2})".format(config.get("latencyProfile"), latencyProfile, latency),
			"Quality mode: {0} ({1})".format(config.get("qualityMode"), _qualitySettings(config)),
			"End-to-end encryption: {0}".format(bool(config.get("password"))),
			"Active profile: {0}".format(config.get("activeProfile") or "none"),
			"Capture source: {0}".format(config.get("captureProcess") or "system audio (NVDA excluded)"),
			"Playback device ID: {0}".format(config.get("outputDeviceId") or "Windows default"),
			"Receive volume: {0}%".format(config.get("receiveVolume")),
			"Receive pan: {0}".format(config.get("receivePan")),
			"Receive EQ: bass {0} dB, mid {1} dB, treble {2} dB".format(config.get("bassDb"), config.get("midDb"), config.get("trebleDb")),
			"Record received audio: {0}".format(config.get("recordReceived")),
			"Recording folder: {0}".format(config.get("recordingFolder")),
			"Helper running: {0}".format(client.get("running")),
			"Helper role: {0}".format(client.get("role")),
			"Helper runtime: {0}".format(runtime),
			"Last helper message: {0}".format(client.get("lastMessage")),
			"Server installed: {0}".format(server.get("installed")),
			"Server path: {0}".format(server.get("path")),
			"Server running: {0}".format(server.get("running")),
			"Startup Run key: {0}".format(server.get("startupRunKey")),
			"Startup shortcut: {0}".format(server.get("startupShortcut")),
			"Legacy scheduled task: {0}".format(server.get("legacyTask")),
			"Firewall rules: {0}".format(server.get("firewallRules")),
		]
		lastDiagnostics = client.get("lastDiagnostics") or {}
		if lastDiagnostics:
			lines.append("Last helper diagnostics:")
			for key in sorted(lastDiagnostics):
				lines.append("  {0}: {1}".format(key, lastDiagnostics[key]))
		lastEvent = client.get("lastEvent") or {}
		if lastEvent:
			lines.append("Last helper event:")
			for key in sorted(lastEvent):
				lines.append("  {0}: {1}".format(key, lastEvent[key]))
		return "\n".join(lines)

	def _runningRemoteClient(self):
		try:
			remoteClientPackage = importlib.import_module("_remoteClient")
		except Exception:
			return None
		return getattr(remoteClientPackage, "_remoteClient", None)

	def _syncRemoteLocalScripts(self):
		self._remoteScriptSyncCall = None
		if self._terminating:
			return
		remoteClient = self._runningRemoteClient()
		localScripts = getattr(remoteClient, "localScripts", None)
		if localScripts is not None:
			for scriptFunc in self._remoteLocalScripts:
				localScripts.add(scriptFunc)
		self._remoteScriptSyncCall = core.callLater(10000, self._syncRemoteLocalScripts)

	def _removeRemoteLocalScripts(self):
		remoteClient = self._runningRemoteClient()
		localScripts = getattr(remoteClient, "localScripts", None)
		if localScripts is None:
			return
		for scriptFunc in self._remoteLocalScripts:
			localScripts.discard(scriptFunc)

	@script(description=_("Receive remote audio"))
	def script_receiveRemoteAudio(self, gesture):
		self.onReceive(None)

	@script(description=_("Send this computer's audio"))
	def script_sendRemoteAudio(self, gesture):
		self.onSend(None)

	@script(description=_("Disconnect remote audio"))
	def script_disconnectRemoteAudio(self, gesture):
		self.onStop(None)

	@script(description=_("Reconnect remote audio"))
	def script_reconnectRemoteAudio(self, gesture):
		self.onReconnect(None)

	@script(description=_("Report remote audio status"))
	def script_reportRemoteAudioStatus(self, gesture):
		self.onStatus(None)

	@script(description=_("Copy remote audio diagnostics"))
	def script_copyRemoteAudioDiagnostics(self, gesture):
		self.onCopyDiagnostics(None)

	@script(description=_("Report this computer's address for remote audio"))
	def script_reportLocalAddress(self, gesture):
		self.onReportLocalAddress(None)

	@script(description=_("Toggle recording of received remote audio"))
	def script_toggleRemoteAudioRecording(self, gesture):
		self.onToggleRecording(None)

	def _autoStartFromSettings(self):
		self._autoStartCall = None
		if self._terminating:
			return
		self._config = _loadConfig()
		mode = _resolveStartupMode(self._config)
		if mode == "disabled":
			return
		if _validateKey(str(self._config.get("key") or "").strip()) is not None:
			return
		self._manualStop = False
		self._autoRole = mode
		self._client.start(mode, self._config)
		self._updateMenuChecks()

	def _onClientExit(self, role, exitCode, stopping):
		if self._terminating:
			return
		# The helper has already cleared its role by the time this fires; refresh the menu.
		self._updateMenuChecks()
		if stopping or self._manualStop or not self._autoRole or role != self._autoRole:
			return
		self._config = _loadConfig()
		if _resolveStartupMode(self._config) != self._autoRole:
			self._autoRole = None
			return
		if self._autoRetryCall is not None:
			self._autoRetryCall.Stop()
		self._autoRetryCall = core.callLater(5000, self._retryAutoStart, self._autoRole)

	def _retryAutoStart(self, role):
		self._autoRetryCall = None
		if self._terminating or self._manualStop or self._client.isRunning():
			return
		self._config = _loadConfig()
		if _resolveStartupMode(self._config) != role:
			return
		self._autoRole = role
		self._client.start(role, self._config)
		self._updateMenuChecks()

	def _monitorResume(self):
		self._resumeMonitorCall = None
		if self._terminating:
			return
		now = time.time()
		elapsed = now - self._lastResumeMonitorWall
		self._lastResumeMonitorWall = now
		if elapsed > RESUME_GAP_SECONDS:
			role = self._client.currentRole()
			if role is not None:
				if self._resumeReconnectCall is not None:
					self._resumeReconnectCall.Stop()
				config = _loadConfig()
				log.info("remoteAudio resume gap detected; reconnecting role=%s after settle", role)
				self._resumeReconnectCall = core.callLater(RESUME_SETTLE_MS, self._resumeReconnectAfterResume, role, config)
		self._resumeMonitorCall = core.callLater(RESUME_MONITOR_INTERVAL_MS, self._monitorResume)

	def _resumeReconnectAfterResume(self, role, config):
		self._resumeReconnectCall = None
		if self._terminating or self._manualStop or self._client.currentRole() != role:
			return
		self._client.start(role, config)
		self._updateMenuChecks()
