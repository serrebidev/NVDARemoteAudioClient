#!/usr/bin/env python3
"""Exercise the NVDA add-on's own logic outside NVDA.

`run-tests.ps1` compiles the add-on, which proves it parses and nothing more.
Everything the Python half actually decides -- what a saved configuration
normalizes to, which latency profile a host resolves to, which helper events are
spoken and which are swallowed -- has until now only ever run inside NVDA, on a
machine with a relay, with a screen reader as the only test harness.

This stubs the NVDA modules the plugin imports, points its configuration at a
scratch directory, and drives the real module. It needs no NVDA, no relay, and
no helper binary. Run with: python tools/selftest_addon.py
"""

import io
import json
import os
import shutil
import sys
import tempfile
import types

# Importing the plugin out of addon/ must not leave .pyc files behind: the
# add-on tree is what gets packaged, and run-tests.ps1 rightly refuses to build
# from a tree with generated files in it.
sys.dont_write_bytecode = True

REPO_ROOT = os.path.dirname(os.path.abspath(os.path.dirname(__file__)))
ADDON_ROOT = os.path.join(REPO_ROOT, "addon")

failures = []
checks = 0


def check(label, got, want):
	global checks
	checks += 1
	if got != want:
		failures.append("{0}\n    got:  {1!r}\n    want: {2!r}".format(label, got, want))


def check_true(label, got):
	check(label, bool(got), True)


# --- NVDA stubs -------------------------------------------------------------

#: Everything ui.message() was asked to say, in order.
spoken = []
#: Everything handed to wx.CallAfter, already invoked.
deferred = []
#: Every helper process the plugin tried to launch.
launched = []
#: Installed by installNVDAStubs(); stands in for subprocess.Popen.
fakePopen = None


def _module(name, **attrs):
	mod = types.ModuleType(name)
	for key, value in attrs.items():
		setattr(mod, key, value)
	sys.modules[name] = mod
	return mod


def installNVDAStubs(configPath):
	class Log:
		def __getattr__(self, level):
			def emit(*args, **kwargs):
				pass
			return emit

	class Addon:
		path = os.path.join(ADDON_ROOT)

	def initTranslation():
		# NVDA installs the add-on's gettext as a builtin; the module body calls
		# _() while the class bodies are still being created, so it has to exist
		# before the import, not after.
		import builtins
		if not hasattr(builtins, "_"):
			setattr(builtins, "_", lambda text: text)

	def script(**kwargs):
		def decorate(func):
			func.__script_description = kwargs.get("description")
			return func
		return decorate

	class SettingsPanel:
		title = ""

		def __init__(self, *args, **kwargs):
			pass

	class NVDASettingsDialog:
		categoryClasses = []

	class GlobalPluginBase:
		def __init__(self, *args, **kwargs):
			pass

		def terminate(self):
			pass

	def callAfter(func, *args, **kwargs):
		deferred.append((func, args, kwargs))
		return func(*args, **kwargs)

	def message(text):
		spoken.append(text)

	appArgs = types.SimpleNamespace(configPath=configPath)

	class FakePopen:
		"""Captures the command line and environment the plugin would launch with."""

		def __init__(self, args, **kwargs):
			launched.append({"args": list(args), "env": kwargs.get("env")})
			self.args = list(args)
			self.stdin = types.SimpleNamespace(close=lambda: None)
			self.stdout = []
			self.returncode = None

		def poll(self):
			return self.returncode

		def wait(self, timeout=None):
			self.returncode = 0
			return 0

		def terminate(self):
			self.returncode = 0

		def kill(self):
			self.returncode = 0

	global fakePopen
	fakePopen = FakePopen

	initTranslation()
	_module("addonHandler", initTranslation=initTranslation, getCodeAddon=lambda: Addon())
	_module("core", callLater=lambda *a, **k: types.SimpleNamespace(Stop=lambda: None))
	_module("globalPluginHandler", GlobalPlugin=GlobalPluginBase)
	_module("globalVars", appArgs=appArgs)
	_module("logHandler", log=Log())
	_module("scriptHandler", script=script)
	_module("ui", message=message)

	guiHelper = _module("gui.guiHelper", BoxSizerHelper=object)
	settingsDialogs = _module(
		"gui.settingsDialogs",
		SettingsPanel=SettingsPanel,
		NVDASettingsDialog=NVDASettingsDialog,
	)
	_module(
		"gui",
		guiHelper=guiHelper,
		settingsDialogs=settingsDialogs,
		mainFrame=types.SimpleNamespace(sysTrayIcon=None),
		messageBox=lambda *a, **k: None,
	)

	_module(
		"wx",
		CallAfter=callAfter,
		IsMainThread=lambda: True,
		ID_ANY=-1,
		ID_OK=5100,
		Menu=object,
		CheckBox=object,
		TextCtrl=object,
		Choice=object,
		Slider=object,
		SpinCtrl=object,
		Button=object,
		TextDataObject=object,
		TheClipboard=types.SimpleNamespace(Open=lambda: False, SetData=None, Close=None),
		EVT_MENU=None,
		OK=1,
		ICON_INFORMATION=2,
	)


def loadPlugin(configPath):
	installNVDAStubs(configPath)
	sys.path.insert(0, ADDON_ROOT)
	sys.path.insert(0, os.path.join(ADDON_ROOT, "globalPlugins"))
	import importlib
	return importlib.import_module("remoteAudioClient")


# --- Tests ------------------------------------------------------------------

def testKeyValidation(mod):
	check("empty key is rejected", mod._validateKey("") is not None, True)
	check("non-string key is rejected", mod._validateKey(None) is not None, True)
	check("tab in key is rejected", mod._validateKey("room\tname") is not None, True)
	check("newline in key is rejected", mod._validateKey("room\nname") is not None, True)
	check("plain key is accepted", mod._validateKey("living room"), None)
	check("unicode key is accepted", mod._validateKey("wohnzimmer über"), None)
	# The server counts UTF-8 bytes, not characters: a key of 128 accented
	# characters is 256 bytes and must be refused before the helper starts.
	check("key at the byte limit is accepted", mod._validateKey("a" * mod.MAX_KEY_BYTES), None)
	check("key over the byte limit is rejected", mod._validateKey("a" * (mod.MAX_KEY_BYTES + 1)) is not None, True)
	check("multibyte key over the byte limit is rejected", mod._validateKey("ü" * 65) is not None, True)


def testConfigNormalization(mod):
	defaults = mod._normalizeConfig({})
	check("default port", defaults["port"], 6838)
	check("default quality", defaults["qualityMode"], "adaptive")

	clamped = mod._normalizeConfig({
		"port": 999999,
		"receiveVolume": 5000,
		"receivePan": -5000,
		"bassDb": 99,
		"bitrate": 1,
		"host": "   example.org   ",
		"qualityMode": "nonsense",
		"startupMode": "nonsense",
		"latencyProfile": "nonsense",
		"announceStatus": "no",
		"useFec": "yes",
		"captureProcess": "  Spotify.EXE  ",
	})
	check("port is clamped", clamped["port"], 65535)
	check("receive volume is clamped", clamped["receiveVolume"], 200)
	check("receive pan is clamped", clamped["receivePan"], -100)
	check("EQ is clamped", clamped["bassDb"], 12)
	check("bitrate is clamped", clamped["bitrate"], 16000)
	check("host is trimmed", clamped["host"], "example.org")
	check("unknown quality falls back", clamped["qualityMode"], "adaptive")
	check("unknown startup mode falls back", clamped["startupMode"], "auto")
	check("unknown latency profile falls back", clamped["latencyProfile"], "auto")
	check("string false is a bool", clamped["announceStatus"], False)
	check("string true is a bool", clamped["useFec"], True)
	check("capture process is folded", clamped["captureProcess"], "spotify.exe")

	junk = mod._normalizeConfig({"port": "abc", "receiveVolume": None, "host": ""})
	check("unparseable port falls back", junk["port"], 6838)
	check("null volume falls back", junk["receiveVolume"], 100)
	check("empty host falls back", junk["host"], "127.0.0.1")

	# A profile is a whole configuration, so it has to be normalized the same way,
	# and must never carry its own nested copy of every other profile.
	nested = mod._normalizeConfig({
		"profiles": {
			"  Home  ": {"port": 70000, "host": "pc.local", "profiles": {"x": {}}},
			"": {"port": 1},
			"bad": "not a dict",
		},
		"activeProfile": "Home",
	})
	check("profile names are trimmed", sorted(nested["profiles"]), ["Home"])
	check("profile values are normalized", nested["profiles"]["Home"]["port"], 65535)
	check("profiles do not nest", "profiles" in nested["profiles"]["Home"], False)
	check("active profile survives", nested["activeProfile"], "Home")

	orphan = mod._normalizeConfig({"profiles": {}, "activeProfile": "Gone"})
	check("active profile pointing nowhere is cleared", orphan["activeProfile"], "")


def testConfigRoundTrip(mod):
	config = mod._loadConfig()
	check("a missing config file yields defaults", config["port"], 6838)

	config["host"] = "pc.tail1234.ts.net"
	config["password"] = "hunter2"
	config["receiveVolume"] = 150
	config["profiles"]["Study"] = mod._profileSnapshot(config)
	config["activeProfile"] = "Study"
	mod._saveConfig(config)

	check_true("config file was written", os.path.exists(mod.CONFIG_PATH))
	reloaded = mod._loadConfig()
	check("host survives a round trip", reloaded["host"], "pc.tail1234.ts.net")
	check("password survives a round trip", reloaded["password"], "hunter2")
	check("volume survives a round trip", reloaded["receiveVolume"], 150)
	check("the profile survives a round trip", sorted(reloaded["profiles"]), ["Study"])
	check("the active profile survives a round trip", reloaded["activeProfile"], "Study")
	check("a snapshot carries no profile list", "profiles" in reloaded["profiles"]["Study"], False)

	# A truncated or hand-edited file must not stop the add-on from loading.
	with io.open(mod.CONFIG_PATH, "w", encoding="utf-8") as f:
		f.write('{"host": "half-writ')
	check("a damaged config falls back to defaults", mod._loadConfig()["host"], "127.0.0.1")

	with io.open(mod.CONFIG_PATH, "w", encoding="utf-8") as f:
		json.dump(["not", "a", "mapping"], f)
	check("a config of the wrong shape falls back", mod._loadConfig()["port"], 6838)

	os.remove(mod.CONFIG_PATH)


def testLatencyProfiles(mod):
	def resolve(host):
		return mod._resolveLatencyProfile({"latencyProfile": "auto", "host": host})

	check("loopback is LAN", resolve("127.0.0.1"), "lan")
	check("localhost is LAN", resolve("localhost"), "lan")
	check("a private address is LAN", resolve("192.168.1.20"), "lan")
	check("a link-local address is LAN", resolve("169.254.10.10"), "lan")
	check("MagicDNS is Tailscale", resolve("desktop.tail9999.ts.net"), "tailscale")
	check("a CGNAT address is Tailscale", resolve("100.101.102.103"), "tailscale")
	# Python counts the documentation ranges (203.0.113.0/24 and friends) as
	# private, so a genuinely global address is the only honest test here.
	check("a public address is Internet", resolve("8.8.8.8"), "internet")
	check("an unresolvable name is Internet", resolve("some.host.example"), "internet")
	check("an explicit profile wins", mod._resolveLatencyProfile(
		{"latencyProfile": "internet", "host": "127.0.0.1"}), "internet")

	for profile in ("lan", "tailscale", "internet"):
		settings = mod.LATENCY_SETTINGS[profile]
		check_true("{0} prebuffer is positive".format(profile), settings["prebufferMs"] > 0)
		check_true(
			"{0} buffer exceeds its prebuffer".format(profile),
			settings["bufferMs"] > settings["prebufferMs"],
		)


def testQualityModes(mod):
	check("live Opus uses 5 ms frames",
		mod._qualitySettings({"qualityMode": "opusLive"}), {"codec": "opus", "frameMs": 5})
	check("broadcast Opus uses 20 ms frames",
		mod._qualitySettings({"qualityMode": "opusBroadcast"}), {"codec": "opus", "frameMs": 20})
	# PCM is only framed at 5 ms; anything longer exceeds the relay's UDP limit.
	check("PCM uses 5 ms frames",
		mod._qualitySettings({"qualityMode": "pcm"}), {"codec": "pcm", "frameMs": 5})
	check("adaptive follows the latency profile",
		mod._qualitySettings({"qualityMode": "adaptive", "latencyProfile": "lan", "host": "127.0.0.1"}),
		{"codec": "opus", "frameMs": 5})
	check("adaptive follows Internet too",
		mod._qualitySettings({"qualityMode": "adaptive", "latencyProfile": "internet", "host": "1.1.1.1"}),
		{"codec": "opus", "frameMs": 10})


def testStartupRoles(mod):
	original = mod.server_installer.is_installed
	try:
		mod.server_installer.is_installed = lambda: True
		check("the relay machine sends", mod._resolveStartupMode({"startupMode": "auto"}), "publisher")
		mod.server_installer.is_installed = lambda: False
		check("a machine without the relay receives", mod._resolveStartupMode({"startupMode": "auto"}), "subscriber")
		check("an explicit role wins", mod._resolveStartupMode({"startupMode": "publisher"}), "publisher")
		check("disabled stays disabled", mod._resolveStartupMode({"startupMode": "disabled"}), "disabled")
	finally:
		mod.server_installer.is_installed = original


def testLocalAddresses(mod):
	# Drive the classification with known answers rather than with whatever this
	# machine's network happens to be. Asserting only "if Tailscale is up" means
	# the checks never run on a machine without it -- which is most of them.
	realProbe = mod._probeLocalAddress
	realHostname = mod._hostnameAddress
	try:
		def probeReturning(value):
			return lambda target: value

		mod._probeLocalAddress = probeReturning("100.90.80.70")
		check("a CGNAT probe result is the Tailscale address", mod._detectTailscaleAddress(), "100.90.80.70")

		# Without Tailscale the probe answers with an ordinary address, and calling
		# that the tailnet address would have the user type an unreachable one.
		mod._probeLocalAddress = probeReturning("192.168.1.5")
		check("a private address is not a Tailscale address", mod._detectTailscaleAddress(), None)
		mod._probeLocalAddress = probeReturning("8.8.8.8")
		check("a public address is not a Tailscale address", mod._detectTailscaleAddress(), None)
		mod._probeLocalAddress = probeReturning(None)
		check("no route means no Tailscale address", mod._detectTailscaleAddress(), None)
		mod._probeLocalAddress = probeReturning("not an address")
		check("an unparseable probe result is discarded", mod._detectTailscaleAddress(), None)

		mod._probeLocalAddress = probeReturning("192.168.1.5")
		check("an ordinary address is the LAN address", mod._detectLanAddress(), "192.168.1.5")

		# When Tailscale carries the default route the probe answers with the tailnet
		# address. Reporting it as the LAN address too would tell the user to type
		# the same thing twice, so it falls back to the hostname lookup.
		mod._hostnameAddress = lambda: "10.0.0.9"
		mod._probeLocalAddress = probeReturning("100.90.80.70")
		check("a tailnet default route falls back to the hostname", mod._detectLanAddress(), "10.0.0.9")
		mod._hostnameAddress = lambda: None
		check("with no hostname address the LAN address is absent", mod._detectLanAddress(), None)

		# No route at all still has to report something on an isolated LAN, which is
		# exactly where the user has to type an address by hand.
		mod._hostnameAddress = lambda: "10.0.0.9"
		mod._probeLocalAddress = probeReturning(None)
		check("with no route the hostname address is used", mod._detectLanAddress(), "10.0.0.9")

		mod._probeLocalAddress = probeReturning("100.90.80.70")
		mod._hostnameAddress = lambda: "10.0.0.9"
		report = mod._localAddressReport(port=6838)
		check_true("the report uses the detected Tailscale address", "100.90.80.70" in report)
		check_true("the report uses the detected LAN address", "10.0.0.9" in report)
	finally:
		mod._probeLocalAddress = realProbe
		mod._hostnameAddress = realHostname

	# Whatever this machine really has, the live functions must agree with their
	# own contract rather than raise.
	import ipaddress
	live = mod._detectTailscaleAddress()
	if live is not None:
		check_true("a live Tailscale address is in the CGNAT range",
			ipaddress.ip_address(live) in mod.TAILSCALE_CGNAT_NETWORK)
	liveLan = mod._detectLanAddress()
	if liveLan is not None:
		check("a live LAN address is never the tailnet address",
			ipaddress.ip_address(liveLan) in mod.TAILSCALE_CGNAT_NETWORK, False)

	report = mod._localAddressReport(
		{"tailscale": "100.90.80.70", "lan": "192.168.1.5", "hostname": "studio-pc"}, port=6838)
	check_true("the report names the tailnet address", "100.90.80.70" in report)
	check_true("the report names the LAN address", "192.168.1.5" in report)
	check_true("the report names the computer", "studio-pc" in report)
	check_true("the report names the port", "6838" in report)

	empty = mod._localAddressReport({"tailscale": None, "lan": None, "hostname": ""}, port=6838)
	check_true("no address is said plainly rather than left blank", len(empty) > 0)
	check_true("no address does not claim one", "6838" not in empty)

	# The probes must never raise, whatever the machine's network looks like.
	# Whatever this machine's network looks like, the probes must return an
	# address or None -- never raise, and never strand the caller.
	for target in (("240.0.0.1", 9), ("::1", 9), ("", 0)):
		try:
			probed = mod._probeLocalAddress(target)
		except Exception as e:  # noqa: BLE001 - the point is that nothing escapes
			failures.append("probing {0!r} raised {1!r}".format(target, e))
			continue
		check_true("probing {0!r} yields a string or None".format(target),
			probed is None or isinstance(probed, str))

	check_true("the hostname fallback yields a string or None",
		mod._hostnameAddress() is None or isinstance(mod._hostnameAddress(), str))


def testHelperEventHandling(mod):
	client = mod.AudioClientProcess()
	client._announceStatus = True
	client._verboseLogging = False

	del spoken[:]
	client._handleLine("")
	client._handleLine("this is not JSON at all")
	check("junk output is ignored rather than spoken", spoken, [])

	del spoken[:]
	client._handleLine(json.dumps({"event": "connected", "role": "subscriber", "message": "Connected."}))
	check("connecting to receive is announced once", len(spoken), 1)
	check("the last message is kept for the status command", client._lastMessage, "Connected.")

	del spoken[:]
	client._handleLine(json.dumps({"event": "connected", "role": "publisher", "message": "Connected."}))
	check("connecting to send is announced once", len(spoken), 1)

	del spoken[:]
	client._handleLine(json.dumps({
		"event": "error",
		"message": "The sending computer has a newer version of NVDA Remote Audio Client than this one.",
	}))
	check("an error is always spoken", len(spoken), 1)
	check_true("the error text reaches the user", "newer version" in spoken[0])

	# Errors must not be silenced by the announce-status preference: that setting
	# is about routine chatter, and a user who turned it off still needs to be
	# told why the audio stopped.
	del spoken[:]
	client._announceStatus = False
	client._handleLine(json.dumps({"event": "error", "message": "Something broke."}))
	check("an error is spoken even with status announcements off", len(spoken), 1)

	del spoken[:]
	client._handleLine(json.dumps({"event": "status", "message": "Listening for remote audio."}))
	check("routine status respects the preference", spoken, [])

	client._announceStatus = True
	del spoken[:]
	client._handleLine(json.dumps({"event": "status", "message": "Listening for remote audio."}))
	check("routine status is announced when asked for", len(spoken), 1)

	del spoken[:]
	client._handleLine(json.dumps({"event": "status", "message": "Reconnecting to host:6838."}))
	check("unrecognized status is not spoken", spoken, [])

	# Diagnostics arrive every five seconds; speaking them would be unusable, but
	# they must still be captured for the diagnostics report.
	del spoken[:]
	client._handleLine(json.dumps({
		"event": "diagnostic",
		"message": "Subscriber audio statistics.",
		"packets_received": 500,
		"endpoint_rebuilds": 2,
		"version_mismatches": 0,
	}))
	check("diagnostics are never spoken", spoken, [])
	check("diagnostics are captured", client._lastDiagnostics.get("packets_received"), 500)
	check("the new endpoint counter is captured", client._lastDiagnostics.get("endpoint_rebuilds"), 2)

	snapshot = client.diagnosticsSnapshot()
	check("the snapshot reports not running", snapshot["running"], False)
	check("the snapshot carries the diagnostics", snapshot["lastDiagnostics"].get("packets_received"), 500)
	check_true("the snapshot is a copy", snapshot["lastDiagnostics"] is not client._lastDiagnostics)

	check("a stopped client reports itself stopped", client.currentRole(), None)
	check_true("the status message says so", "not connected" in client.statusMessage().lower())
	# stop() on a client that never started must be a no-op, not a crash.
	client.stop()


def testLabelsCoverEveryChoice(mod):
	"""Every stored value must have a spoken label.

	A control that falls through to a wrong label, or to a blank one, is not a
	cosmetic problem here: the label is the only way a blind user can tell what
	the setting is currently set to.
	"""
	for mode in mod.STARTUP_MODES:
		label = mod._startupModeLabel(mode)
		check_true("startup mode {0!r} has a label".format(mode), label and label.strip())
	for profile in mod.LATENCY_PROFILES:
		label = mod._latencyProfileLabel(profile)
		check_true("latency profile {0!r} has a label".format(profile), label and label.strip())
	for quality in mod.QUALITY_MODES:
		label = mod._qualityModeLabel(quality)
		check_true("quality mode {0!r} has a label".format(quality), label and label.strip())

	# Distinct values must not share a label, or two different settings would be
	# indistinguishable when read aloud.
	for name, values, labeller in (
		("startup mode", mod.STARTUP_MODES, mod._startupModeLabel),
		("latency profile", mod.LATENCY_PROFILES, mod._latencyProfileLabel),
		("quality mode", mod.QUALITY_MODES, mod._qualityModeLabel),
	):
		labels = [labeller(v) for v in values]
		check("every {0} label is distinct".format(name), len(set(labels)), len(labels))

	# Every latency profile the resolver can return must have settings behind it.
	for profile in mod.LATENCY_PROFILES:
		if profile == "auto":
			continue
		check_true("latency profile {0!r} has settings".format(profile), profile in mod.LATENCY_SETTINGS)


def testSecretsNeverReachTheCommandLine(mod):
	"""The encryption password must never be visible in the helper's arguments.

	Any process on the machine can read another process's command line. The
	password is handed over in an environment variable instead, and this is the
	check that keeps it that way.
	"""
	import subprocess as realSubprocess
	originalPopen = realSubprocess.Popen
	originalHelperPath = mod.HELPER_PATH
	realSubprocess.Popen = fakePopen
	# start() refuses to launch when the helper binary is missing, and the binary
	# is staged into addon/bin/ by build.ps1 -- which runs *after* the tests. Point
	# at a stand-in that exists, so this checks the arguments rather than quietly
	# checking nothing on a clean checkout.
	standIn = os.path.join(os.path.dirname(mod.CONFIG_PATH), "NVDARemoteAudioHelper.exe")
	with io.open(standIn, "w", encoding="utf-8") as f:
		f.write("not a real helper")
	mod.HELPER_PATH = standIn
	del launched[:]
	try:
		config = mod._normalizeConfig({
			"host": "studio.example",
			"port": 6838,
			"key": "living room",
			"password": "correct horse battery staple",
			"captureProcess": "",
			"qualityMode": "opusLive",
		})
		client = mod.AudioClientProcess()
		client.start("publisher", config)
		check("the publisher was launched", len(launched), 1)
		if not launched:
			# Without this the assertions below raise IndexError and the run dies
			# before reporting the other checks.
			failures.append("no helper was launched; the checks below could not run")
			return

		args = launched[0]["args"]
		env = launched[0]["env"] or {}
		joined = " ".join(args)
		check_true("the password is not in the command line", "correct horse battery staple" not in joined)
		check_true("the password is not passed as --password", "--password" not in args)
		check_true("the password variable is named instead", "--password-env" in args)
		variable = args[args.index("--password-env") + 1]
		check("the named variable carries the password", env.get(variable), "correct horse battery staple")

		# The room key is not a secret in the same sense -- the relay needs it -- but
		# the rest of the invocation still has to be right.
		check("the role is passed", args[args.index("--role") + 1], "publisher")
		check("the host is passed", args[args.index("--host") + 1], "studio.example")
		check("the key is passed", args[args.index("--key") + 1], "living room")
		check_true("system capture excludes NVDA by PID", "--exclude-pid" in args)
		check("live Opus uses 5 ms frames", args[args.index("--opus-frame-ms") + 1], "5")
		client.stop()

		# Isolating one application swaps PID exclusion for name inclusion.
		del launched[:]
		config["captureProcess"] = "spotify"
		client = mod.AudioClientProcess()
		client.start("publisher", config)
		args = launched[0]["args"]
		check_true("application capture includes the process name", "--include-process-name" in args)
		check_true("application capture does not also exclude NVDA", "--exclude-pid" not in args)
		client.stop()

		# The receiving side passes its shaping settings and nothing it should not.
		del launched[:]
		config = mod._normalizeConfig({
			"host": "studio.example",
			"key": "living room",
			"password": "hunter2",
			"receiveVolume": 150,
			"receivePan": -40,
			"bassDb": 3,
			"midDb": -2,
			"trebleDb": 6,
			"outputDeviceId": "{device-id}",
			"recordReceived": True,
			"recordingFolder": os.path.join(os.path.expanduser("~"), "Documents", "Recordings"),
		})
		client = mod.AudioClientProcess()
		client.start("subscriber", config)
		args = launched[0]["args"]
		joined = " ".join(args)
		check_true("the receiver's password is not in the command line", "hunter2" not in joined)
		check("the receive volume is passed", args[args.index("--receive-volume") + 1], "150")
		check("the pan is passed", args[args.index("--receive-pan") + 1], "-40")
		check("the bass setting is passed", args[args.index("--bass-db") + 1], "3")
		check("the treble setting is passed", args[args.index("--treble-db") + 1], "6")
		check("the playback device is passed", args[args.index("--output-device-id") + 1], "{device-id}")
		check_true("recording is requested", "--record-folder" in args)
		check_true("a receiver does not carry publisher options", "--bitrate" not in args)
		client.stop()

		# Recording off must not pass a folder at all, rather than an empty one.
		del launched[:]
		config["recordReceived"] = False
		client = mod.AudioClientProcess()
		client.start("subscriber", config)
		check_true("recording off passes no folder", "--record-folder" not in launched[0]["args"])
		client.stop()

		# An invalid key is refused before a process is ever started.
		del launched[:]
		del spoken[:]
		bad = mod._normalizeConfig({"host": "pc", "key": "with\ttab"})
		client = mod.AudioClientProcess()
		client.start("subscriber", bad)
		check("an invalid key starts no helper", len(launched), 0)
		check_true("an invalid key is announced", len(spoken) > 0)

		# A missing helper binary must be reported, not silently do nothing. This
		# is also the state a clean checkout is in, which is why the stand-in above
		# exists at all.
		del launched[:]
		del spoken[:]
		mod.HELPER_PATH = os.path.join(os.path.dirname(standIn), "definitely-not-here.exe")
		client = mod.AudioClientProcess()
		client.start("subscriber", mod._normalizeConfig({"host": "pc", "key": "room"}))
		check("a missing helper starts nothing", len(launched), 0)
		check_true("a missing helper is announced", any("missing" in m.lower() for m in spoken))
	finally:
		realSubprocess.Popen = originalPopen
		mod.HELPER_PATH = originalHelperPath


def testDiagnosticsNeverLeakThePassword(mod):
	"""Diagnostics are copied to the clipboard and pasted into bug reports."""
	config = mod._loadConfig()
	config["host"] = "studio.example"
	config["key"] = "living room"
	config["password"] = "a-very-secret-password"
	config["profiles"]["Study"] = mod._profileSnapshot(config)
	mod._saveConfig(config)
	try:
		plugin = object.__new__(mod.GlobalPlugin)
		plugin._client = mod.AudioClientProcess()
		text = plugin._diagnosticsText()

		check_true("the report is not empty", len(text) > 200)
		check_true("the password is not in the report", "a-very-secret-password" not in text)
		# Whether encryption is on is exactly what a bug report needs to know.
		check_true("the report says encryption is on", "End-to-end encryption: True" in text)
		check_true("the report names the host", "studio.example" in text)
		check_true("the report includes this computer's addresses", "Tailscale address" in text)
		check_true("the report includes the helper path", "Helper path" in text)
		check_true("the report includes the resolved latency profile", "Latency profile" in text)
		check_true("every line is printable", all(line.isprintable() for line in text.splitlines()))
	finally:
		os.remove(mod.CONFIG_PATH)


def testServerStatusIsReadable(mod):
	"""server_status() is read-only and must work on a machine with nothing installed."""
	status = mod.server_installer.server_status()
	for key in ("installed", "path", "running", "startupRunKey", "startupShortcut", "legacyTask", "firewallRules"):
		check_true("server status reports {0!r}".format(key), key in status)
	check_true("installed is a bool", isinstance(status["installed"], bool))
	check_true("path is a string", isinstance(status["path"], str))
	check("is_installed agrees with server_status", mod.server_installer.is_installed(), status["installed"])

	# A path with a quote in it must not be able to break out of the quoted
	# command line the startup entry is built from.
	quoted = mod.server_installer._quote_command_path(r"C:\Program Files\Server.exe")
	check_true("a path is quoted", quoted.startswith(chr(34)) and quoted.endswith(chr(34)))
	hostile = mod.server_installer._quote_command_path(r'C:\evil" --flag "x')
	check_true("an embedded quote is escaped", r'\"' in hostile)
	check("the escaped path is still one quoted argument", hostile.count(chr(34)) - hostile.count(chr(92) + chr(34)), 2)


def main():
	scratch = tempfile.mkdtemp(prefix="remoteAudioSelfTest")
	try:
		mod = loadPlugin(scratch)
		print("Loaded the add-on outside NVDA. Config path: {0}".format(mod.CONFIG_PATH))
		for test in (
			testKeyValidation,
			testConfigNormalization,
			testConfigRoundTrip,
			testLatencyProfiles,
			testQualityModes,
			testStartupRoles,
			testLocalAddresses,
			testHelperEventHandling,
			testLabelsCoverEveryChoice,
			testSecretsNeverReachTheCommandLine,
			testDiagnosticsNeverLeakThePassword,
			testServerStatusIsReadable,
		):
			# A test that runs no checks reports "ok" while proving nothing. That is
			# how a suite drifts into passing on an environment it never exercised:
			# the add-on's helper binary is staged by build.ps1, which runs after
			# the tests, so on a clean checkout a test that depends on it used to
			# sail past having launched nothing at all.
			before = checks
			test(mod)
			if checks == before:
				failures.append("{0} ran no checks at all".format(test.__name__))
				print("  {0}: RAN NO CHECKS".format(test.__name__))
			else:
				print("  {0}: ok ({1} checks)".format(test.__name__, checks - before))
	finally:
		shutil.rmtree(scratch, ignore_errors=True)

	print("")
	if failures:
		print("{0} of {1} add-on checks FAILED:".format(len(failures), checks))
		for failure in failures:
			print("  " + failure)
		return 1
	print("All {0} add-on checks passed.".format(checks))
	return 0


if __name__ == "__main__":
	sys.exit(main())
