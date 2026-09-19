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
	"transport": "nvda",
	"remSoundPeers": "",
	"remSoundDevices": [],
	"remSoundDeviceName": "",
	"allowRemoteControl": False,
	"captureDevice": "",
	"profiles": {},
	"activeProfile": "",
}

TRANSPORTS = ("nvda", "remsound")
REMSOUND_PORT = 47830
# Characters a host name or address can contain; the helper applies the same rule.
_PEER_ALLOWED = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_:")

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


STARTUP_MODES = ("auto", "disabled", "subscriber", "publisher", "duplex")
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
		"transport": config.get("transport") if config.get("transport") in TRANSPORTS else DEFAULT_CONFIG["transport"],
		"remSoundPeers": ", ".join(_parsePeerList(config.get("remSoundPeers"))),
		"remSoundDevices": _parseDeviceNames(config.get("remSoundDevices")),
		"remSoundDeviceName": "".join(
			c for c in str(config.get("remSoundDeviceName") or "") if c.isprintable()
		).strip()[:64],
		"allowRemoteControl": asBool(config.get("allowRemoteControl"), DEFAULT_CONFIG["allowRemoteControl"]),
		"captureDevice": "".join(c for c in str(config.get("captureDevice") or "") if c.isprintable()).strip()[:512],
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


def _parsePeerList(value):
	"""Host names and addresses from a comma-separated setting, invalid entries dropped.

	A malformed entry would stop the helper from starting, so the add-on keeps only
	what the helper will accept and the settings field shows what was kept.
	"""
	if isinstance(value, (list, tuple)):
		entries = value
	else:
		entries = str(value or "").replace(";", ",").split(",")
	peers = []
	for entry in entries:
		entry = str(entry).strip()
		if not entry or len(entry) > 255 or any(c not in _PEER_ALLOWED for c in entry):
			continue
		if entry.count(":") > 1:
			continue
		if ":" in entry:
			host, _sep, port = entry.partition(":")
			if not host or not port.isdigit() or not 1 <= int(port) <= 65535:
				continue
		if entry.lower() not in (p.lower() for p in peers):
			peers.append(entry)
	return peers


def _parseDeviceNames(value):
	"""RemSound device names chosen from discovery, as a clean list."""
	if isinstance(value, str):
		value = value.split(",")
	if not isinstance(value, (list, tuple)):
		return []
	names = []
	for name in value:
		name = "".join(c for c in str(name) if c.isprintable() and c != ",").strip()[:128]
		if name and name.casefold() not in (n.casefold() for n in names):
			names.append(name)
	return names


def _transportLabel(transport):
	return {
		# Translators: connection type using the NVDA Remote Audio server.
		"nvda": _("NVDA Remote Audio server (room name, port 6838)"),
		# Translators: connection type compatible with the RemSound apps.
		"remsound": _("RemSound peer to peer (Windows, iPhone and Android RemSound apps)"),
	}.get(transport, _("NVDA Remote Audio server (room name, port 6838)"))


def _startupModeLabel(mode):
	return {
		"auto": _("Automatic: server sends, client receives"),
		"disabled": _("Do not connect automatically"),
		"subscriber": _("Receive remote audio"),
		"publisher": _("Send this computer's audio"),
		# Translators: startup choice that sends and receives over a RemSound connection.
		"duplex": _("Send and receive at the same time (RemSound only)"),
	}.get(mode, _("Automatic: server sends, client receives"))


def _roleLabel(role):
	return {
		"subscriber": _("receiving"),
		"publisher": _("sending"),
		"duplex": _("sending and receiving"),
	}.get(role, _("receiving"))


def _connectionProblem(role, config):
	"""A spoken reason the helper cannot start with this configuration, or None."""
	if config.get("transport") == "remsound":
		if not str(config.get("password") or ""):
			# Translators: RemSound always encrypts, so it cannot connect without a password.
			return _("RemSound connections need an encryption password. Set the same password here and in the RemSound app on the other device.")
		return None
	if role == "duplex":
		# Translators: the relay server carries audio in one direction only.
		return _("Sending and receiving at the same time needs the RemSound connection type")
	return _validateKey(str(config.get("key") or "").strip())


def _remSoundPasswordWarning(config):
	"""The spoken password reminder for a RemSound setup that has none, or an empty string.

	The settings panel refuses to save RemSound without a password, so choosing a
	device -- the one path that sets the connection type itself -- is the only way to
	end up in a RemSound setup that can never start. That is the moment to say so,
	not after the user has pressed connect and been told it cannot run.
	"""
	if config.get("transport") == "remsound" and not str(config.get("password") or ""):
		# Translators: RemSound always encrypts, so a setup without a password cannot start.
		return _("RemSound also needs an encryption password: set the same one the other device uses in NVDA Remote Audio settings.")
	return ""


def _helperArguments(role, config, helperPath=None, nvdaPid=None):
	"""The helper command line and the environment it needs, for one connection.

	Returns (args, env). The password travels in env, never in args, because any
	process on the machine can read another's command line.
	"""
	helperPath = helperPath or HELPER_PATH
	nvdaPid = os.getpid() if nvdaPid is None else nvdaPid
	remsound = config.get("transport") == "remsound"
	args = [helperPath, "--role", role]
	if remsound:
		args.extend(["--transport", "remsound"])
		peers = _parsePeerList(config.get("remSoundPeers"))
		if peers:
			args.extend(["--peers", ",".join(peers)])
		devices = _parseDeviceNames(config.get("remSoundDevices"))
		if devices:
			args.extend(["--peer-names", ",".join(devices)])
		deviceName = str(config.get("remSoundDeviceName") or "").strip()
		if deviceName:
			args.extend(["--device-name", deviceName])
		if config.get("allowRemoteControl"):
			args.append("--allow-remote-control")
	else:
		args.extend([
			"--host", config["host"],
			"--port", str(config["port"]),
			"--key", config["key"],
		])
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
	if role in ("publisher", "duplex"):
		captureDevice = str(config.get("captureDevice") or "").strip()
		captureProcess = str(config.get("captureProcess") or "").strip()
		if captureDevice:
			args.extend(["--capture-device-id", captureDevice])
		elif captureProcess:
			args.extend(["--include-process-name", captureProcess])
		else:
			args.extend(["--exclude-pid", str(nvdaPid)])
		args.extend(["--bitrate", str(config["bitrate"])])
	if role in ("subscriber", "duplex"):
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
	return args, childEnv


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
	if config.get("transport") == "remsound":
		# Devices chosen by name were found by LAN discovery; typed addresses say
		# for themselves whether they are local, on a tailnet, or far away.
		peers = _parsePeerList(config.get("remSoundPeers"))
		if not peers:
			return "lan"
		host = peers[0].rsplit(":", 1)[0].lower() if peers[0].count(":") == 1 else peers[0].lower()
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


def _queryHelperCatalog(flag, eventName, collectionName, timeout=5):
	"""Return a live helper catalog, or an empty list if discovery is unavailable."""
	if not os.path.exists(HELPER_PATH):
		return []
	flags = list(flag) if isinstance(flag, (list, tuple)) else [flag]
	try:
		startupinfo = subprocess.STARTUPINFO()
		startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
		completed = subprocess.run(
			[HELPER_PATH] + flags,
			stdin=subprocess.DEVNULL,
			stdout=subprocess.PIPE,
			stderr=subprocess.STDOUT,
			text=True,
			encoding="utf-8",
			errors="replace",
			timeout=timeout,
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


def _inputDevices():
	devices = _queryHelperCatalog("--list-input-devices", "input_devices", "devices")
	return [device for device in devices if isinstance(device, dict) and device.get("Id")]


def _discoverRemSoundDevices(seconds=4):
	"""RemSound devices announcing themselves on this network, found by the helper."""
	peers = _queryHelperCatalog(
		["--discover-peers", "--discover-seconds", str(seconds)],
		"peers",
		"peers",
		timeout=seconds + 6,
	)
	return [peer for peer in peers if isinstance(peer, dict) and peer.get("Name")]


def _groupPeers(peers):
	"""One entry per device name, with every address it answered on.

	A phone on Wi-Fi and Tailscale at once, or a PC with several network adapters,
	announces once per address; the user is choosing a device, not an adapter.
	"""
	grouped = {}
	for peer in peers:
		name = str(peer.get("Name") or "").strip()
		if not name:
			continue
		entry = grouped.setdefault(name.casefold(), {
			"Name": name,
			"Addresses": [],
			"CanSend": False,
			"CanReceive": False,
		})
		address = str(peer.get("Address") or "")
		if address and address not in entry["Addresses"]:
			entry["Addresses"].append(address)
		entry["CanSend"] = entry["CanSend"] or bool(peer.get("CanSend"))
		entry["CanReceive"] = entry["CanReceive"] or bool(peer.get("CanReceive"))
	return sorted(grouped.values(), key=lambda entry: entry["Name"].casefold())


def _peerChoiceLabel(peer):
	if peer["CanSend"] and peer["CanReceive"]:
		# Translators: a discovered RemSound device that can send and receive audio.
		what = _("sends and receives")
	elif peer["CanSend"]:
		# Translators: a discovered RemSound device that only sends audio.
		what = _("sends audio")
	elif peer["CanReceive"]:
		# Translators: a discovered RemSound device that only plays audio.
		what = _("receives audio")
	else:
		# Translators: a discovered RemSound device with sending and receiving both off.
		what = _("idle")
	return _("{name}, {what}, {addresses}").format(
		name=peer["Name"], what=what, addresses=", ".join(peer["Addresses"]))


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
	def __init__(self, exitCallback=None, volumeCallback=None):
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
		self._volumeCallback = volumeCallback
		self._transport = DEFAULT_CONFIG["transport"]
		#: The latest RemSound devices the running helper saw on the network.
		self.discoveredPeers = []

	def isRunning(self):
		return self._process is not None and self._process.poll() is None

	def currentRole(self):
		"""Return 'subscriber', 'publisher', 'duplex', or None if not running."""
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
		problem = _connectionProblem(role, config)
		if problem is not None:
			self._speak(problem)
			return

		self._role = role
		self._transport = config.get("transport", DEFAULT_CONFIG["transport"])
		self.discoveredPeers = []
		self._lastMessage = _("Connecting")
		self._startedAt = time.time()
		self._stopping = False

		args, childEnv = _helperArguments(role, config)

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
		elif role == "duplex":
			self._speakStatus(_("Sending and receiving remote audio"))
		else:
			self._speakStatus(_("Sending this computer's audio"))

	def transport(self):
		return self._transport if self.isRunning() else None

	def sendCommand(self, command):
		"""Send a live command (volume, mute, remote control) to the running helper.

		Returns False when no helper is running to take it. Every command starts
		with '!' because any other byte on the helper's input means "shut down".
		"""
		process = self._process
		if process is None or process.poll() is not None or process.stdin is None:
			return False
		try:
			process.stdin.write("!" + command + "\n")
			process.stdin.flush()
			return True
		except Exception:
			log.debug("Remote audio helper command failed", exc_info=True)
			return False

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
		return _("Remote audio {role}: {message}").format(role=_roleLabel(self._role), message=self._lastMessage)

	def diagnosticsSnapshot(self):
		return {
			"running": self.isRunning(),
			"role": self._role or "",
			"lastMessage": self._lastMessage,
			"startedAt": self._startedAt,
			"lastEvent": dict(self._lastEvent),
			"lastDiagnostics": dict(self._lastDiagnostics),
			"transport": self._transport,
			"discoveredPeers": list(self.discoveredPeers),
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
			peer = str(event.get("peer") or "")
			if peer:
				# Translators: a RemSound device answered; {peer} is its name or address.
				self._queueStatus(_("Remote audio connected to {peer}").format(peer=peer))
			elif event.get("role") == "subscriber":
				self._queueStatus(_("Remote audio connected for receiving"))
			else:
				self._queueStatus(_("Remote audio connected for sending"))
		elif eventName == "peer_lost":
			# Translators: a RemSound device stopped answering for five seconds.
			self._queueStatus(_("Lost contact with {peer}").format(peer=str(event.get("peer") or "")))
		elif eventName == "receiving":
			# Translators: audio from a RemSound device started playing.
			self._queueStatus(_("Receiving audio from {peer}").format(peer=str(event.get("peer") or "")))
		elif eventName == "warning":
			# A problem the user has to fix, such as a password that differs on the
			# other device, so it is spoken whatever the status preference says.
			wx.CallAfter(ui.message, _("Remote audio: {message}").format(message=message))
			log.warning("Remote audio helper warning: %s", line)
		elif eventName == "volume":
			self._handleVolumeEvent(event, message)
		elif eventName == "control_sent":
			wx.CallAfter(ui.message, _(message))
		elif eventName == "peers":
			peers = event.get("peers")
			if isinstance(peers, list):
				self.discoveredPeers = [peer for peer in peers if isinstance(peer, dict)]
		elif eventName == "error":
			wx.CallAfter(ui.message, _("Remote audio error: {message}").format(message=message))
			log.error("Remote audio helper error: %s", line)
		elif eventName == "status" and message in ("Capture started.", "Listening for remote audio."):
			self._queueStatus(_(message))
		elif eventName == "recording":
			self._queueStatus(_(message))

	def _handleVolumeEvent(self, event, message):
		fromOtherDevice = event.get("source") == "remote"
		if event.get("system"):
			# Translators: another device changed this computer's Windows volume.
			wx.CallAfter(ui.message, _("{peer} changed the Windows volume. {message}").format(
				peer=str(event.get("peer") or ""), message=message))
			return
		volume = event.get("volume")
		muted = bool(event.get("muted"))
		if muted:
			# Translators: received audio is muted; the volume it returns to is included.
			text = _("Received audio muted, volume {volume} percent").format(volume=volume)
		else:
			# Translators: the volume of received audio, 0 to 200 percent.
			text = _("Receive volume {volume} percent").format(volume=volume)
		if fromOtherDevice:
			# Translators: another device changed how loud this computer plays its audio.
			text = _("{peer} changed it: {change}").format(peer=str(event.get("peer") or ""), change=text)
		# A change the user asked for, or one another device made, is always worth
		# hearing: nobody should wonder why the audio just got quieter.
		wx.CallAfter(ui.message, text)
		if isinstance(volume, int) and self._volumeCallback is not None:
			wx.CallAfter(self._volumeCallback, volume)


class RemoteAudioSettingsPanel(gui.settingsDialogs.SettingsPanel):
	title = _("NVDA Remote Audio")

	def makeSettings(self, settingsSizer):
		self._config = _loadConfig()
		helper = guiHelper.BoxSizerHelper(self, sizer=settingsSizer)

		self.transportChoice = helper.addLabeledControl(
			_("Connection type:"),
			wx.Choice,
			choices=[_transportLabel(transport) for transport in TRANSPORTS],
		)
		self.transportChoice.SetSelection(list(TRANSPORTS).index(self._config["transport"]))

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

		self.remSoundPeersCtrl = helper.addLabeledControl(
			# Translators: addresses of RemSound devices, such as a phone's IP address, a Tailscale address, or a RemSound relay.
			_("RemSound devices by address (comma-separated, for example 192.168.1.20 or relay.example.com):"),
			wx.TextCtrl,
		)
		self.remSoundPeersCtrl.SetValue(str(self._config["remSoundPeers"]))
		self.remSoundDevicesCtrl = helper.addLabeledControl(
			# Translators: names of RemSound devices chosen with "Find RemSound devices on this network".
			_("RemSound devices by name (comma-separated; use Find RemSound devices in the Tools menu):"),
			wx.TextCtrl,
		)
		self.remSoundDevicesCtrl.SetValue(", ".join(self._config["remSoundDevices"]))
		self.remSoundNameCtrl = helper.addLabeledControl(
			_("Name other RemSound devices see for this computer (blank uses the computer name):"),
			wx.TextCtrl,
		)
		self.remSoundNameCtrl.SetValue(str(self._config["remSoundDeviceName"]))
		self.allowRemoteControlCheck = helper.addItem(wx.CheckBox(
			self,
			label=_("Let RemSound devices that share the password change this computer's volume"),
		))
		self.allowRemoteControlCheck.SetValue(bool(self._config["allowRemoteControl"]))

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
		# Each entry is (application process name, recording device ID); at most one is set.
		self._captureValues = [("", "")]
		captureChoices = [_('System audio (NVDA excluded)')]
		configuredCapture = self._config["captureProcess"]
		configuredDevice = self._config["captureDevice"]
		for app in _audioApps():
			processName = str(app.get("ProcessName") or "").strip().lower()
			if not processName or (processName, "") in self._captureValues:
				continue
			displayName = str(app.get("DisplayName") or processName)
			state = _("playing") if app.get("Playing") else _("idle")
			captureChoices.append(_("{display} ({process}, {state})").format(display=displayName, process=processName, state=state))
			self._captureValues.append((processName, ""))
		# Translators: send the default microphone instead of playback audio.
		captureChoices.append(_("Microphone: Windows default recording device"))
		self._captureValues.append(("", "default"))
		for device in _inputDevices():
			deviceId = str(device.get("Id") or "").strip()
			if not deviceId or ("", deviceId) in self._captureValues:
				continue
			# Translators: send this microphone or other recording device.
			captureChoices.append(_("Microphone: {name}").format(name=str(device.get("Name") or deviceId)))
			self._captureValues.append(("", deviceId))
		configured = ("", configuredDevice) if configuredDevice else (configuredCapture, "")
		if configured not in self._captureValues:
			if configuredDevice:
				captureChoices.append(_("Saved recording device (not currently available)"))
			else:
				captureChoices.append(_("{process} (not currently available)").format(process=configuredCapture))
			self._captureValues.append(configured)
		self.captureChoice = helper.addLabeledControl(_("Audio to send:"), wx.Choice, choices=captureChoices)
		self.captureChoice.SetSelection(self._captureValues.index(configured))

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
			choices=[_startupModeLabel(mode) for mode in STARTUP_MODES],
		)
		self.startupChoice.SetSelection(list(STARTUP_MODES).index(self._config["startupMode"]))
		self.announceStatusCheck = helper.addItem(wx.CheckBox(self, label=_("Announce connection status messages")))
		self.announceStatusCheck.SetValue(bool(self._config["announceStatus"]))
		self.useFecCheck = helper.addItem(wx.CheckBox(self, label=_("Use Opus packet-loss recovery")))
		self.useFecCheck.SetValue(bool(self._config["useFec"]))
		self.verboseLoggingCheck = helper.addItem(wx.CheckBox(self, label=_("Verbose diagnostic logging")))
		self.verboseLoggingCheck.SetValue(bool(self._config["verboseLogging"]))

	def isValid(self):
		transport = TRANSPORTS[self.transportChoice.GetSelection()]
		if transport == "remsound" and not self.passwordCtrl.GetValue():
			gui.messageBox(
				_("RemSound connections are always encrypted. Enter the same encryption password that the other device uses."),
				_("NVDA Remote Audio"), wx.OK | wx.ICON_ERROR)
			self.passwordCtrl.SetFocus()
			return False
		rawPeers = [entry.strip() for entry in self.remSoundPeersCtrl.GetValue().replace(";", ",").split(",") if entry.strip()]
		rejected = [entry for entry in rawPeers if not _parsePeerList(entry)]
		if rejected:
			gui.messageBox(
				_("These are not host names or addresses: {entries}").format(entries=", ".join(rejected)),
				_("NVDA Remote Audio"), wx.OK | wx.ICON_ERROR)
			self.remSoundPeersCtrl.SetFocus()
			return False
		if transport == "remsound":
			return super().isValid()
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
			"captureProcess": self._captureValues[self.captureChoice.GetSelection()][0],
			"captureDevice": self._captureValues[self.captureChoice.GetSelection()][1],
			"transport": TRANSPORTS[self.transportChoice.GetSelection()],
			"remSoundPeers": self.remSoundPeersCtrl.GetValue(),
			"remSoundDevices": self.remSoundDevicesCtrl.GetValue(),
			"remSoundDeviceName": self.remSoundNameCtrl.GetValue(),
			"allowRemoteControl": self.allowRemoteControlCheck.GetValue(),
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
		self._client = AudioClientProcess(self._onClientExit, self._onVolumeChanged)
		self._menu = None
		self._menuRoot = None
		self._receiveItem = None
		self._sendItem = None
		self._duplexItem = None
		self._discovering = False
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
			self.script_sendAndReceiveRemoteAudio,
			self.script_findRemSoundDevices,
			self.script_receiveVolumeUp,
			self.script_receiveVolumeDown,
			self.script_toggleReceiveMute,
			self.script_otherDeviceVolumeUp,
			self.script_otherDeviceVolumeDown,
			self.script_otherDeviceMute,
			self.script_otherDeviceSystemVolumeUp,
			self.script_otherDeviceSystemVolumeDown,
			self.script_otherDeviceSystemMute,
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
			self._duplexItem = self._menu.AppendCheckItem(wx.ID_ANY, _("Send and receive at the same time (RemSound)"))
			self._menu.AppendSeparator()
			reconnectItem = self._menu.Append(wx.ID_ANY, _("Reconnect audio"))
			stopItem = self._menu.Append(wx.ID_ANY, _("Disconnect audio"))
			self._recordItem = self._menu.AppendCheckItem(wx.ID_ANY, _("Record received audio"))
			openRecordingsItem = self._menu.Append(wx.ID_ANY, _("Open recordings folder"))
			statusItem = self._menu.Append(wx.ID_ANY, _("Audio status"))
			addressItem = self._menu.Append(wx.ID_ANY, _("This computer's address for the other computer"))
			findDevicesItem = self._menu.Append(wx.ID_ANY, _("Find RemSound devices on this network..."))
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
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onDuplex, self._duplexItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onFindDevices, findDevicesItem)
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
			if self._duplexItem is not None:
				self._duplexItem.Check(role == "duplex")
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
		self._duplexItem = None
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
		# Only the audio server needs installing; RemSound talks peer to peer.
		if self._config.get("transport") != "remsound" and not server_installer.is_installed():
			server_installer.offer_install(gui.mainFrame, on_done=self._onSendInstallDone)
			return
		self._client.start("publisher", self._config)
		self._updateMenuChecks()

	def onDuplex(self, event):
		if self._client.currentRole() == "duplex":
			self._stopAndAnnounce()
			return
		self._config = _loadConfig()
		if self._config.get("transport") != "remsound":
			self._updateMenuChecks()
			ui.message(_("Sending and receiving at the same time needs the RemSound connection type. Change it in NVDA Remote Audio settings."))
			return
		self._manualStop = False
		self._autoRole = None
		self._client.start("duplex", self._config)
		self._updateMenuChecks()

	def _onVolumeChanged(self, volume):
		"""Remember a live volume change, from a gesture or from the other device."""
		if self._terminating:
			return
		config = _loadConfig()
		if config.get("receiveVolume") != volume:
			config["receiveVolume"] = volume
			self._config = _saveConfig(config)

	def _changeReceiveVolume(self, step=None, mute=False):
		role = self._client.currentRole()
		if role in ("subscriber", "duplex"):
			self._client.sendCommand("mute toggle" if mute else "volume-step {0}".format(step))
			return
		if mute:
			ui.message(_("Remote audio is not receiving; there is nothing to mute"))
			return
		# Nothing is playing, so change the saved volume for the next connection.
		config = _loadConfig()
		config["receiveVolume"] = max(0, min(200, int(config.get("receiveVolume", 100)) + step))
		self._config = _saveConfig(config)
		ui.message(_("Receive volume {volume} percent").format(volume=self._config["receiveVolume"]))

	def _sendRemoteControl(self, command):
		if self._client.transport() != "remsound":
			ui.message(_("Changing the other device's volume needs a running RemSound connection"))
			return
		self._client.sendCommand("control " + command)

	def onFindDevices(self, event):
		if self._discovering:
			ui.message(_("Already searching for RemSound devices"))
			return
		# A running RemSound connection is already listening; its list is current.
		if self._client.transport() == "remsound" and self._client.discoveredPeers:
			self._showDevices(list(self._client.discoveredPeers))
			return
		self._discovering = True
		ui.message(_("Searching for RemSound devices for a few seconds"))
		threading.Thread(target=self._discoverWorker, name="remoteAudioDiscover", daemon=True).start()

	def _discoverWorker(self):
		try:
			peers = _discoverRemSoundDevices()
		except Exception:
			log.error("RemSound discovery failed", exc_info=True)
			peers = []
		wx.CallAfter(self._showDevices, peers)

	def _showDevices(self, peers):
		self._discovering = False
		devices = _groupPeers(peers)
		if not devices:
			gui.messageBox(
				_("No RemSound devices answered. Open the RemSound app on the other device, check it is on the same network, "
					"and allow local network access on an iPhone. Over Tailscale or the internet, type the device's address "
					"in NVDA Remote Audio settings instead."),
				_("Find RemSound devices"), wx.OK | wx.ICON_INFORMATION)
			return
		config = _loadConfig()
		chosen = [name.casefold() for name in config.get("remSoundDevices", [])]
		dialog = wx.MultiChoiceDialog(
			gui.mainFrame,
			_("Check the devices to send audio to and hear audio from. The other device must use the same password, "
				"and in its RemSound app, tick this computer too."),
			_("Find RemSound devices"),
			[_peerChoiceLabel(device) for device in devices],
		)
		try:
			dialog.SetSelections([index for index, device in enumerate(devices) if device["Name"].casefold() in chosen])
			if dialog.ShowModal() != wx.ID_OK:
				return
			selected = [devices[index]["Name"] for index in dialog.GetSelections()]
		finally:
			dialog.Destroy()
		found = [device["Name"].casefold() for device in devices]
		# Devices chosen earlier that are switched off right now stay chosen.
		kept = [name for name in config.get("remSoundDevices", []) if name.casefold() not in found]
		config["remSoundDevices"] = kept + selected
		switched = config.get("transport") != "remsound"
		config["transport"] = "remsound"
		config["activeProfile"] = ""
		self._config = _saveConfig(config)
		if selected:
			message = _("Chosen: {names}").format(names=", ".join(selected))
		else:
			message = _("No devices chosen")
		if switched:
			message += ". " + _("Connection type changed to RemSound")
		warning = _remSoundPasswordWarning(config)
		if warning:
			message += ". " + warning
		ui.message(message)
		if self._client.isRunning():
			self.onReconnect(None)

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
			if candidate in ("subscriber", "publisher", "duplex"):
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
			"Connection type: {0}".format(config.get("transport")),
			"Configured host: {0}".format(config.get("host")),
			"RemSound devices by address: {0}".format(config.get("remSoundPeers") or "none"),
			"RemSound devices by name: {0}".format(", ".join(config.get("remSoundDevices") or []) or "none"),
			"RemSound name for this computer: {0}".format(config.get("remSoundDeviceName") or socket.gethostname()),
			"Accept remote volume commands: {0}".format(config.get("allowRemoteControl")),
			"This computer's Tailscale address: {0}".format(_detectTailscaleAddress() or "none"),
			"This computer's local network address: {0}".format(_detectLanAddress() or "none"),
			"This computer's name: {0}".format(socket.gethostname()),
			"Configured port: {0}".format(config.get("port")),
			"Startup action: {0} (resolved: {1})".format(config.get("startupMode"), _resolveStartupMode(config)),
			"Latency profile: {0} (resolved: {1}; {2})".format(config.get("latencyProfile"), latencyProfile, latency),
			"Quality mode: {0} ({1})".format(config.get("qualityMode"), _qualitySettings(config)),
			"End-to-end encryption: {0}".format(bool(config.get("password"))),
			"Active profile: {0}".format(config.get("activeProfile") or "none"),
			"Capture source: {0}".format(
				("recording device " + config["captureDevice"]) if config.get("captureDevice")
				else config.get("captureProcess") or "system audio (NVDA excluded)"),
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
		discovered = client.get("discoveredPeers") or []
		if discovered:
			lines.append("RemSound devices seen on this network:")
			for peer in discovered:
				lines.append("  {0} at {1}:{2} (sends: {3}, receives: {4})".format(
					peer.get("Name"), peer.get("Address"), peer.get("AudioPort"), peer.get("CanSend"), peer.get("CanReceive")))
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

	@script(description=_("Send and receive remote audio at the same time (RemSound)"))
	def script_sendAndReceiveRemoteAudio(self, gesture):
		self.onDuplex(None)

	@script(description=_("Find RemSound devices on this network"))
	def script_findRemSoundDevices(self, gesture):
		self.onFindDevices(None)

	@script(description=_("Raise the volume of received remote audio"))
	def script_receiveVolumeUp(self, gesture):
		self._changeReceiveVolume(5)

	@script(description=_("Lower the volume of received remote audio"))
	def script_receiveVolumeDown(self, gesture):
		self._changeReceiveVolume(-5)

	@script(description=_("Mute or unmute received remote audio"))
	def script_toggleReceiveMute(self, gesture):
		self._changeReceiveVolume(mute=True)

	@script(description=_("Raise the other device's RemSound volume"))
	def script_otherDeviceVolumeUp(self, gesture):
		self._sendRemoteControl("volume-up")

	@script(description=_("Lower the other device's RemSound volume"))
	def script_otherDeviceVolumeDown(self, gesture):
		self._sendRemoteControl("volume-down")

	@script(description=_("Mute or unmute RemSound on the other device"))
	def script_otherDeviceMute(self, gesture):
		self._sendRemoteControl("mute")

	@script(description=_("Raise the other device's Windows volume"))
	def script_otherDeviceSystemVolumeUp(self, gesture):
		self._sendRemoteControl("system-volume-up")

	@script(description=_("Lower the other device's Windows volume"))
	def script_otherDeviceSystemVolumeDown(self, gesture):
		self._sendRemoteControl("system-volume-down")

	@script(description=_("Mute or unmute the other device's Windows volume"))
	def script_otherDeviceSystemMute(self, gesture):
		self._sendRemoteControl("system-mute")

	def _autoStartFromSettings(self):
		self._autoStartCall = None
		if self._terminating:
			return
		self._config = _loadConfig()
		mode = _resolveStartupMode(self._config)
		if mode == "disabled":
			return
		if _connectionProblem(mode, self._config) is not None:
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
