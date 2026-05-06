import json
import os
import ipaddress
import subprocess
import threading
import time

import addonHandler
import core
import globalPluginHandler
import globalVars
import gui
import ui
import wx
from gui import guiHelper
from logHandler import log

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
	"startupMode": "auto",
	"latencyProfile": "auto",
}

STARTUP_MODES = ("auto", "disabled", "subscriber", "publisher")
LATENCY_PROFILES = ("auto", "lan", "tailscale", "internet")
LATENCY_SETTINGS = {
	"lan": {"prebufferMs": 60, "outputLatencyMs": 60, "bufferMs": 300},
	"tailscale": {"prebufferMs": 90, "outputLatencyMs": 80, "bufferMs": 450},
	"internet": {"prebufferMs": 150, "outputLatencyMs": 120, "bufferMs": 800},
}


def _loadConfig():
	config = dict(DEFAULT_CONFIG)
	try:
		with open(CONFIG_PATH, "r", encoding="utf-8") as f:
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

	return {
		"host": str(config.get("host") or DEFAULT_CONFIG["host"]).strip() or DEFAULT_CONFIG["host"],
		"port": clampInt(config.get("port"), DEFAULT_CONFIG["port"], 1, 65535),
		"key": str(config.get("key") if config.get("key") is not None else DEFAULT_CONFIG["key"]),
		"bitrate": clampInt(config.get("bitrate"), DEFAULT_CONFIG["bitrate"], 16000, 510000),
		"startupMode": config.get("startupMode") if config.get("startupMode") in STARTUP_MODES else DEFAULT_CONFIG["startupMode"],
		"latencyProfile": config.get("latencyProfile") if config.get("latencyProfile") in LATENCY_PROFILES else DEFAULT_CONFIG["latencyProfile"],
	}


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
	if ip.version == 4 and ip in ipaddress.ip_network("100.64.0.0/10"):
		return "tailscale"
	if ip.is_private or ip.is_loopback or ip.is_link_local:
		return "lan"
	return "internet"


def _latencySettings(config):
	return LATENCY_SETTINGS[_resolveLatencyProfile(config)]


class SettingsDialog(wx.Dialog):
	def __init__(self, parent, config):
		super().__init__(parent, title=_("NVDA Remote Audio Settings"))
		self._config = dict(config)

		mainSizer = wx.BoxSizer(wx.VERTICAL)
		grid = wx.FlexGridSizer(rows=6, cols=2, vgap=8, hgap=8)
		grid.AddGrowableCol(1, 1)

		self.hostCtrl = wx.TextCtrl(self, value=str(self._config["host"]))
		self.portCtrl = wx.SpinCtrl(self, min=1, max=65535, initial=int(self._config["port"]))
		self.keyCtrl = wx.TextCtrl(self, value=str(self._config["key"]))
		self.bitrateCtrl = wx.SpinCtrl(self, min=16000, max=510000, initial=int(self._config["bitrate"]))
		self.latencyChoice = wx.Choice(
			self,
			choices=[_latencyProfileLabel(profile) for profile in LATENCY_PROFILES],
		)
		self.latencyChoice.SetSelection(list(LATENCY_PROFILES).index(self._config["latencyProfile"]))
		self.startupChoice = wx.Choice(
			self,
			choices=[
				_startupModeLabel("auto"),
				_startupModeLabel("disabled"),
				_startupModeLabel("subscriber"),
				_startupModeLabel("publisher"),
			],
		)
		self.startupChoice.SetSelection(list(STARTUP_MODES).index(self._config["startupMode"]))

		for label, control in (
			(_("Server host:"), self.hostCtrl),
			(_("Audio port:"), self.portCtrl),
			(_("Key:"), self.keyCtrl),
			(_("Send bitrate:"), self.bitrateCtrl),
			(_("Latency profile:"), self.latencyChoice),
			(_("Startup action:"), self.startupChoice),
		):
			grid.Add(wx.StaticText(self, label=label), 0, wx.ALIGN_CENTER_VERTICAL)
			grid.Add(control, 1, wx.EXPAND)

		mainSizer.Add(grid, 1, wx.ALL | wx.EXPAND, 12)
		buttonSizer = self.CreateButtonSizer(wx.OK | wx.CANCEL)
		mainSizer.Add(buttonSizer, 0, wx.ALL | wx.ALIGN_RIGHT, 12)
		self.SetSizerAndFit(mainSizer)
		self.CentreOnParent()

	def getConfig(self):
		return _normalizeConfig({
			"host": self.hostCtrl.GetValue(),
			"port": self.portCtrl.GetValue(),
			"key": self.keyCtrl.GetValue(),
			"bitrate": self.bitrateCtrl.GetValue(),
			"latencyProfile": LATENCY_PROFILES[self.latencyChoice.GetSelection()],
			"startupMode": STARTUP_MODES[self.startupChoice.GetSelection()],
		})


class AudioClientProcess:
	def __init__(self, exitCallback=None):
		self._process = None
		self._readerThread = None
		self._role = None
		self._lastMessage = _("Not connected")
		self._startedAt = None
		self._stopping = False
		self._exitCallback = exitCallback

	def isRunning(self):
		return self._process is not None and self._process.poll() is None

	def currentRole(self):
		"""Return 'subscriber', 'publisher', or None if not running."""
		if not self.isRunning():
			return None
		return self._role

	def start(self, role, config):
		self.stop()
		if not os.path.exists(HELPER_PATH):
			ui.message(_("Remote audio helper is missing"))
			return
		if not str(config.get("key") or "").strip():
			ui.message(_("Remote audio key is not set"))
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
		if role == "publisher":
			args.extend([
				"--exclude-pid", str(os.getpid()),
				"--bitrate", str(config["bitrate"]),
			])
		else:
			latency = _latencySettings(config)
			args.extend([
				"--prebuffer-ms", str(latency["prebufferMs"]),
				"--output-latency-ms", str(latency["outputLatencyMs"]),
				"--buffer-ms", str(latency["bufferMs"]),
			])

		try:
			startupinfo = subprocess.STARTUPINFO()
			startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
			self._process = subprocess.Popen(
				args,
				stdin=subprocess.DEVNULL,
				stdout=subprocess.PIPE,
				stderr=subprocess.STDOUT,
				text=True,
				encoding="utf-8",
				errors="replace",
				creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
				startupinfo=startupinfo,
			)
		except Exception as e:
			self._process = None
			log.error("Failed to start remote audio helper", exc_info=True)
			ui.message(_("Failed to start remote audio: {error}").format(error=e))
			return

		self._readerThread = threading.Thread(target=self._readOutput, name="remoteAudioClientOutput", daemon=True)
		self._readerThread.start()
		if role == "subscriber":
			ui.message(_("Connecting to remote audio"))
		else:
			ui.message(_("Sending this computer's audio"))

	def stop(self):
		process = self._process
		if process is None:
			return
		self._stopping = True
		if process.poll() is None:
			try:
				process.terminate()
				process.wait(timeout=2)
			except Exception:
				try:
					process.kill()
				except Exception:
					pass
		self._process = None
		self._role = None
		self._lastMessage = _("Not connected")
		self._startedAt = None

	def statusMessage(self):
		if not self.isRunning():
			return _("Remote audio is not connected")
		role = _("receiving") if self._role == "subscriber" else _("sending")
		return _("Remote audio {role}: {message}").format(role=role, message=self._lastMessage)

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
				wx.CallAfter(ui.message, _("Remote audio stopped"))
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
		if eventName == "connected":
			if event.get("role") == "subscriber":
				wx.CallAfter(ui.message, _("Remote audio connected for receiving"))
			else:
				wx.CallAfter(ui.message, _("Remote audio connected for sending"))
		elif eventName == "error":
			wx.CallAfter(ui.message, _("Remote audio error: {message}").format(message=message))
			log.error("Remote audio helper error: %s", line)
		elif eventName == "status" and message in ("Capture started.", "Listening for remote audio."):
			wx.CallAfter(ui.message, _(message))


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

		self.keyCtrl = helper.addLabeledControl(_("Key:"), wx.TextCtrl)
		self.keyCtrl.SetValue(str(self._config["key"]))

		self.bitrateCtrl = helper.addLabeledControl(
			_("Send bitrate:"),
			wx.SpinCtrl,
			min=16000,
			max=510000,
			initial=int(self._config["bitrate"]),
		)
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

	def onSave(self):
		_saveConfig({
			"host": self.hostCtrl.GetValue(),
			"port": self.portCtrl.GetValue(),
			"key": self.keyCtrl.GetValue(),
			"bitrate": self.bitrateCtrl.GetValue(),
			"latencyProfile": LATENCY_PROFILES[self.latencyChoice.GetSelection()],
			"startupMode": STARTUP_MODES[self.startupChoice.GetSelection()],
		})


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
		self._autoRole = None
		self._autoRetryCall = None
		self._manualStop = False
		if RemoteAudioSettingsPanel not in gui.settingsDialogs.NVDASettingsDialog.categoryClasses:
			gui.settingsDialogs.NVDASettingsDialog.categoryClasses.append(RemoteAudioSettingsPanel)
		self._createMenu()
		core.callLater(1500, self._autoStartFromSettings)

	def terminate(self):
		self._client.stop()
		if self._autoRetryCall is not None:
			self._autoRetryCall.Stop()
			self._autoRetryCall = None
		self._destroyMenu()
		try:
			gui.settingsDialogs.NVDASettingsDialog.categoryClasses.remove(RemoteAudioSettingsPanel)
		except Exception:
			pass
		super().terminate()

	def _createMenu(self):
		try:
			toolsMenu = gui.mainFrame.sysTrayIcon.toolsMenu
			self._menu = wx.Menu()
			self._receiveItem = self._menu.AppendCheckItem(wx.ID_ANY, _("Receive remote audio"))
			self._sendItem = self._menu.AppendCheckItem(wx.ID_ANY, _("Send this computer's audio"))
			self._menu.AppendSeparator()
			stopItem = self._menu.Append(wx.ID_ANY, _("Disconnect audio"))
			statusItem = self._menu.Append(wx.ID_ANY, _("Audio status"))
			self._menu.AppendSeparator()
			installItem = self._menu.Append(wx.ID_ANY, _("Install audio server (this machine sends audio)..."))
			firewallItem = self._menu.Append(wx.ID_ANY, _("Add firewall rules for audio server..."))
			settingsItem = self._menu.Append(wx.ID_ANY, _("Audio settings..."))

			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onReceive, self._receiveItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onSend, self._sendItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onStop, stopItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onStatus, statusItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onInstallServer, installItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onAddFirewallRules, firewallItem)
			gui.mainFrame.sysTrayIcon.Bind(wx.EVT_MENU, self.onSettings, settingsItem)
			self._menuRoot = toolsMenu.AppendSubMenu(self._menu, _("NVDA Remote Audio"), _("NVDA Remote Audio"))
			self._updateMenuChecks()
		except Exception:
			log.error("Failed to create remote audio menu", exc_info=True)

	def _updateMenuChecks(self):
		"""Sync the Receive/Send check marks with the current helper role."""
		if self._receiveItem is None or self._sendItem is None:
			return
		role = self._client.currentRole()
		try:
			self._receiveItem.Check(role == "subscriber")
			self._sendItem.Check(role == "publisher")
		except Exception:
			log.debug("Failed to update remote audio menu checks", exc_info=True)

	def _destroyMenu(self):
		try:
			if self._menuRoot is not None:
				gui.mainFrame.sysTrayIcon.toolsMenu.Remove(self._menuRoot)
				self._menuRoot = None
			if self._menu is not None:
				self._menu.Destroy()
				self._menu = None
			self._receiveItem = None
			self._sendItem = None
		except Exception:
			log.debug("Failed to destroy remote audio menu", exc_info=True)

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
		self._client.stop()
		self._updateMenuChecks()
		ui.message(_("Remote audio disconnected") if wasRunning else _("Remote audio is not connected"))

	def onInstallServer(self, event):
		server_installer.offer_install(gui.mainFrame)

	def onAddFirewallRules(self, event):
		server_installer.add_firewall_rules_only(gui.mainFrame)

	def onStop(self, event):
		self._stopAndAnnounce()

	def onStatus(self, event):
		ui.message(self._client.statusMessage())

	def onSettings(self, event):
		gui.mainFrame.popupSettingsDialog(gui.settingsDialogs.NVDASettingsDialog, RemoteAudioSettingsPanel)

	def _autoStartFromSettings(self):
		self._config = _loadConfig()
		mode = _resolveStartupMode(self._config)
		if mode == "disabled":
			return
		if not str(self._config.get("key") or "").strip():
			return
		self._manualStop = False
		self._autoRole = mode
		self._client.start(mode, self._config)
		self._updateMenuChecks()

	def _onClientExit(self, role, exitCode, stopping):
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
		if self._manualStop or self._client.isRunning():
			return
		self._config = _loadConfig()
		if _resolveStartupMode(self._config) != role:
			return
		self._autoRole = role
		self._client.start(role, self._config)
		self._updateMenuChecks()
