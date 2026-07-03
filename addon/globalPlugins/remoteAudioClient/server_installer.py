"""Detect and install NVDARemoteAudioServer (https://github.com/haitun001/NVDARemoteAudioServer).

Used by the NVDA Remote Audio Client add-on so the machine that sends audio can
acquire the upstream relay binary without leaving NVDA. Runs in user context;
no admin elevation required.
"""

import ctypes
import json
import os
import shutil
import subprocess
import tempfile
import threading
import urllib.error
import urllib.request
import winreg
import zipfile
from ctypes import wintypes

import addonHandler
import gui
import ui
import wx
from logHandler import log

addonHandler.initTranslation()

GITHUB_LATEST_RELEASE_API = "https://api.github.com/repos/haitun001/NVDARemoteAudioServer/releases/latest"
WINDOWS_ASSET_NAME = "NVDARemoteAudioServer-windows-amd64.zip"
SERVER_EXE_NAME = "NVDARemoteAudioServer.exe"
SCHEDULED_TASK_NAME = "NVDARemoteAudioServer"
STARTUP_SHORTCUT_NAME = "NVDARemoteAudioServer.lnk"
STARTUP_SHORTCUT_DESCRIPTION = "Starts NVDARemoteAudioServer for NVDA Remote Audio Client"
RUN_KEY_PATH = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_VALUE_NAME = "NVDARemoteAudioServer"

# Searched in order. The first existing match wins.
INSTALL_DIR_CANDIDATES = (
	r"C:\NVDARemoteAudioServer",
	os.path.join(os.environ.get("LOCALAPPDATA", ""), "NVDARemoteAudioServer"),
)

USER_AGENT = "NVDARemoteAudioClient (+https://github.com/serrebidev/NVDARemoteAudioClient)"

FIREWALL_RULE_TCP = "NVDARemoteAudioServer (TCP)"
FIREWALL_RULE_UDP = "NVDARemoteAudioServer (UDP)"
SERVER_PORT = 6838

# ShellExecuteEx constants for UAC elevation.
_SEE_MASK_NOCLOSEPROCESS = 0x00000040
_SEE_MASK_NOASYNC = 0x00000100
_SW_HIDE = 0
_ERROR_CANCELLED = 1223


class _SHELLEXECUTEINFOW(ctypes.Structure):
	_fields_ = (
		("cbSize", wintypes.DWORD),
		("fMask", ctypes.c_ulong),
		("hwnd", wintypes.HANDLE),
		("lpVerb", wintypes.LPCWSTR),
		("lpFile", wintypes.LPCWSTR),
		("lpParameters", wintypes.LPCWSTR),
		("lpDirectory", wintypes.LPCWSTR),
		("nShow", ctypes.c_int),
		("hInstApp", wintypes.HINSTANCE),
		("lpIDList", ctypes.c_void_p),
		("lpClass", wintypes.LPCWSTR),
		("hkeyClass", wintypes.HANDLE),
		("dwHotKey", wintypes.DWORD),
		("hIconOrMonitor", wintypes.HANDLE),
		("hProcess", wintypes.HANDLE),
	)


def find_server_exe():
	"""Return the absolute path to NVDARemoteAudioServer.exe if installed, else None."""
	for candidate in INSTALL_DIR_CANDIDATES:
		if not candidate:
			continue
		exe = os.path.join(candidate, SERVER_EXE_NAME)
		if os.path.isfile(exe):
			return exe
	return None


def is_installed():
	return find_server_exe() is not None


def offer_install(parent, on_done=None):
	"""Show a yes/no dialog asking the user to install. If yes, runs the install on a
	background thread and calls on_done(success: bool) on the GUI thread when finished.
	If the server is already installed, repairs startup persistence and starts it."""
	exe = find_server_exe()
	if exe is not None:
		ensure_installed_server_ready(on_done=on_done, announce=True)
		return

	message = _(
		"NVDARemoteAudioServer is not installed on this computer.\n"
		"\n"
		"It is required for the machine that sends audio. Download and install it now from\n"
		"https://github.com/haitun001/NVDARemoteAudioServer ?"
	)
	answer = gui.messageBox(
		message,
		_("NVDA Remote Audio: install audio server?"),
		wx.YES_NO | wx.ICON_QUESTION,
		parent,
	)
	if answer != wx.YES:
		if on_done is not None:
			wx.CallAfter(on_done, False)
		return

	thread = threading.Thread(
		target=_install_worker,
		args=(on_done,),
		name="installNVDARemoteAudioServer",
		daemon=True,
	)
	thread.start()


def offer_update_or_repair(parent, on_done=None):
	"""Download the latest server release over the current install, then repair startup/firewall."""
	exe = find_server_exe()
	if exe is None:
		offer_install(parent, on_done=on_done)
		return
	answer = gui.messageBox(
		_(
			"Download and install the latest audio server over the existing copy?\n"
			"\n"
			"The current server process will be stopped and restarted. Your remote audio client settings are not changed."
		),
		_("NVDA Remote Audio: update audio server?"),
		wx.YES_NO | wx.ICON_QUESTION,
		parent,
	)
	if answer != wx.YES:
		if on_done is not None:
			wx.CallAfter(on_done, False)
		return
	threading.Thread(
		target=_install_worker,
		args=(on_done, True),
		name="updateNVDARemoteAudioServer",
		daemon=True,
	).start()


def _install_worker(on_done, replace_existing=False):
	try:
		wx.CallAfter(ui.message, _("Downloading NVDARemoteAudioServer"))
		download_url = _resolve_windows_zip_url()
		install_dir = _pick_install_dir(existing_ok=True)
		os.makedirs(install_dir, exist_ok=True)
		if replace_existing:
			_stop_server_if_running()

		zip_path = _download_to_temp(download_url)
		try:
			_extract_zip(zip_path, install_dir)
		finally:
			try:
				os.unlink(zip_path)
			except OSError:
				pass

		exe = os.path.join(install_dir, SERVER_EXE_NAME)
		if not os.path.isfile(exe):
			raise RuntimeError(
				"Download finished but {0} was not found in {1}".format(SERVER_EXE_NAME, install_dir)
			)

		_start_server_if_needed(exe)
		startup_ok = _ensure_startup_entry(exe)

		firewall_ok = _ensure_firewall_rules(exe)
		if startup_ok and firewall_ok:
			wx.CallAfter(ui.message, _("Audio server installed, configured to start at sign-in, and started. Firewall rules added."))
		elif startup_ok:
			wx.CallAfter(
				ui.message,
				_(
					"Audio server installed, configured to start at sign-in, and started, but the firewall rules could not be added. "
					"Allow inbound TCP and UDP port {port} manually if remote machines cannot connect."
				).format(port=SERVER_PORT),
			)
		elif firewall_ok:
			wx.CallAfter(
				ui.message,
				_("Audio server installed and started, but automatic startup could not be configured. Firewall rules added."),
			)
		else:
			wx.CallAfter(
				ui.message,
				_(
					"Audio server installed and started, but automatic startup and firewall rules could not be configured. "
					"Allow inbound TCP and UDP port {port} manually if remote machines cannot connect."
				).format(port=SERVER_PORT),
			)
		if on_done is not None:
			wx.CallAfter(on_done, True)
	except Exception as e:
		log.error("Failed to install NVDARemoteAudioServer", exc_info=True)
		_call_message_box(
			_("Failed to install audio server: {error}").format(error=e),
			_("NVDA Remote Audio"),
			wx.OK | wx.ICON_ERROR,
		)
		if on_done is not None:
			wx.CallAfter(on_done, False)


def ensure_installed_server_ready(on_done=None, announce=False):
	"""Repair startup persistence and start the server for an existing install."""
	exe = find_server_exe()
	if exe is None:
		if on_done is not None:
			wx.CallAfter(on_done, False)
		return

	thread = threading.Thread(
		target=_ensure_installed_server_ready_worker,
		args=(exe, on_done, announce),
		name="ensureNVDARemoteAudioServerReady",
		daemon=True,
	)
	thread.start()


def _ensure_installed_server_ready_worker(exe, on_done, announce):
	server_ok = False
	try:
		_start_server_if_needed(exe)
		startup_ok = _ensure_startup_entry(exe)
		server_ok = True
		if announce:
			if startup_ok:
				wx.CallAfter(ui.message, _("Audio server is installed, configured to start at sign-in, and running."))
			else:
				wx.CallAfter(ui.message, _("Audio server is installed and running, but automatic startup could not be configured."))
	except Exception as e:
		log.error("Failed to repair or start NVDARemoteAudioServer", exc_info=True)
		if announce:
			wx.CallAfter(ui.message, _("Failed to start or repair audio server: {error}").format(error=e))
			_call_message_box(
				_("Failed to start or repair audio server: {error}").format(error=e),
				_("NVDA Remote Audio"),
				wx.OK | wx.ICON_ERROR,
			)
	finally:
		if on_done is not None:
			wx.CallAfter(on_done, server_ok)


def _resolve_windows_zip_url():
	request = urllib.request.Request(
		GITHUB_LATEST_RELEASE_API,
		headers={
			"User-Agent": USER_AGENT,
			"Accept": "application/vnd.github+json",
		},
	)
	try:
		with urllib.request.urlopen(request, timeout=20) as response:
			payload = json.load(response)
	except urllib.error.URLError as e:
		raise RuntimeError("Could not reach GitHub: {0}".format(e.reason)) from e

	for asset in payload.get("assets", []):
		if asset.get("name") == WINDOWS_ASSET_NAME:
			url = asset.get("browser_download_url")
			if url:
				return url
	raise RuntimeError(
		"Latest release of NVDARemoteAudioServer does not contain {0}".format(WINDOWS_ASSET_NAME)
	)


def _download_to_temp(url):
	request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
	with urllib.request.urlopen(request, timeout=60) as response:
		fd, path = tempfile.mkstemp(suffix=".zip", prefix="NVDARemoteAudioServer-")
		try:
			with os.fdopen(fd, "wb") as out:
				shutil.copyfileobj(response, out, length=64 * 1024)
		except Exception:
			try:
				os.unlink(path)
			except OSError:
				pass
			raise
	return path


def _extract_zip(zip_path, install_dir):
	with zipfile.ZipFile(zip_path) as zf:
		# Reject any entry that would write outside install_dir (defence against zip-slip).
		install_root = os.path.realpath(install_dir)
		for name in zf.namelist():
			target = os.path.realpath(os.path.join(install_root, name))
			if not (target == install_root or target.startswith(install_root + os.sep)):
				raise RuntimeError("Refusing to extract entry outside install directory: {0}".format(name))
		zf.extractall(install_dir)


def _pick_install_dir(existing_ok=False):
	if existing_ok:
		exe = find_server_exe()
		if exe is not None:
			return os.path.dirname(exe)
	primary = INSTALL_DIR_CANDIDATES[0]
	try:
		os.makedirs(primary, exist_ok=True)
		probe = os.path.join(primary, ".write-test")
		with open(probe, "w") as f:
			f.write("ok")
		os.unlink(probe)
		return primary
	except OSError:
		log.debug("Cannot write to %s, falling back to LOCALAPPDATA", primary)
	fallback = INSTALL_DIR_CANDIDATES[1]
	if not fallback:
		raise RuntimeError("Cannot determine install directory: LOCALAPPDATA is not set")
	os.makedirs(fallback, exist_ok=True)
	return fallback


def _ensure_startup_entry(exe):
	"""Create a per-user startup entry. Returns False if all persistence paths fail."""
	if _register_run_key(exe):
		_delete_startup_shortcut()
		_delete_legacy_logon_task()
		return True
	if _register_startup_shortcut(exe):
		_delete_run_key()
		_delete_legacy_logon_task()
		return True
	return _register_logon_task(exe)


def _quote_command_path(path):
	return '"{0}"'.format(path.replace('"', r'\"'))


def _register_run_key(exe):
	try:
		with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, RUN_KEY_PATH, 0, winreg.KEY_SET_VALUE) as key:
			winreg.SetValueEx(key, RUN_VALUE_NAME, 0, winreg.REG_SZ, _quote_command_path(exe))
		return True
	except OSError as e:
		log.warning("Could not register NVDARemoteAudioServer Run key: %s", e)
		return False


def _delete_run_key():
	try:
		with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY_PATH, 0, winreg.KEY_SET_VALUE) as key:
			winreg.DeleteValue(key, RUN_VALUE_NAME)
	except FileNotFoundError:
		pass
	except OSError as e:
		log.debug("Could not delete NVDARemoteAudioServer Run key: %s", e)


def _run_key_exists():
	try:
		with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY_PATH, 0, winreg.KEY_QUERY_VALUE) as key:
			winreg.QueryValueEx(key, RUN_VALUE_NAME)
		return True
	except FileNotFoundError:
		return False
	except OSError as e:
		log.debug("Could not query NVDARemoteAudioServer Run key: %s", e)
		return False


def _startup_shortcut_path():
	appdata = os.environ.get("APPDATA")
	if not appdata:
		return None
	startup_dir = os.path.join(
		appdata,
		"Microsoft",
		"Windows",
		"Start Menu",
		"Programs",
		"Startup",
	)
	return os.path.join(startup_dir, STARTUP_SHORTCUT_NAME)


def _startup_shortcut_exists():
	shortcut_path = _startup_shortcut_path()
	return bool(shortcut_path and os.path.isfile(shortcut_path))


def _delete_startup_shortcut():
	shortcut_path = _startup_shortcut_path()
	if not shortcut_path:
		return
	try:
		if os.path.isfile(shortcut_path):
			os.unlink(shortcut_path)
	except OSError as e:
		log.debug("Could not delete NVDARemoteAudioServer startup shortcut: %s", e)


def _register_startup_shortcut(exe):
	"""Register startup through the user's Startup folder. This needs no admin rights."""
	shortcut_path = _startup_shortcut_path()
	if not shortcut_path:
		log.warning("Could not register startup shortcut: APPDATA is not set")
		return False
	try:
		os.makedirs(os.path.dirname(shortcut_path), exist_ok=True)
	except OSError as e:
		log.warning("Could not create Startup folder for NVDARemoteAudioServer shortcut: %s", e)
		return False

	def ps_quote(value):
		return str(value).replace("'", "''")

	ps_command = (
		"$ErrorActionPreference='Stop';"
		"$shell=New-Object -ComObject WScript.Shell;"
		f"$shortcut=$shell.CreateShortcut('{ps_quote(shortcut_path)}');"
		f"$shortcut.TargetPath='{ps_quote(exe)}';"
		f"$shortcut.WorkingDirectory='{ps_quote(os.path.dirname(exe))}';"
		"$shortcut.WindowStyle=7;"
		f"$shortcut.Description='{ps_quote(STARTUP_SHORTCUT_DESCRIPTION)}';"
		"$shortcut.Save();"
	)
	try:
		result = subprocess.run(
			[
				"powershell.exe",
				"-NoProfile",
				"-ExecutionPolicy",
				"Bypass",
				"-Command",
				ps_command,
			],
			capture_output=True,
			text=True,
			timeout=30,
			creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
		)
	except (subprocess.SubprocessError, FileNotFoundError) as e:
		log.warning("Could not register NVDARemoteAudioServer startup shortcut: %s", e)
		return False
	if result.returncode != 0:
		log.warning(
			"Startup shortcut registration failed with code %s; stdout=%r stderr=%r",
			result.returncode,
			result.stdout,
			result.stderr,
		)
		return False
	if not os.path.isfile(shortcut_path):
		log.warning("Startup shortcut registration did not create %s", shortcut_path)
		return False
	return True


def _delete_legacy_logon_task():
	"""Remove the older scheduled task path so the server is not launched twice."""
	try:
		subprocess.run(
			[
				"schtasks",
				"/delete",
				"/tn",
				SCHEDULED_TASK_NAME,
				"/f",
			],
			capture_output=True,
			text=True,
			timeout=15,
			creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
		)
	except (subprocess.SubprocessError, FileNotFoundError) as e:
		log.debug("Could not remove legacy NVDARemoteAudioServer logon task: %s", e)


def _legacy_logon_task_exists():
	try:
		result = subprocess.run(
			[
				"schtasks",
				"/query",
				"/tn",
				SCHEDULED_TASK_NAME,
			],
			capture_output=True,
			text=True,
			timeout=15,
			creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
		)
	except (subprocess.SubprocessError, FileNotFoundError) as e:
		log.debug("Could not query legacy NVDARemoteAudioServer logon task: %s", e)
		return False
	return result.returncode == 0


def _register_logon_task(exe):
	"""Fallback: register a per-user logon scheduled task. Best effort."""
	# /rl LIMITED keeps it user-context (no admin elevation needed).
	# /f overwrites any existing task with the same name.
	args = [
		"schtasks",
		"/create",
		"/tn",
		SCHEDULED_TASK_NAME,
		"/tr",
		'"{0}"'.format(exe),
		"/sc",
		"onlogon",
		"/rl",
		"limited",
		"/f",
	]
	try:
		result = subprocess.run(
			args,
			check=True,
			capture_output=True,
			text=True,
			timeout=30,
			creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
		)
	except (subprocess.CalledProcessError, FileNotFoundError) as e:
		log.warning("Could not register NVDARemoteAudioServer logon task: %s", e)
		return False
	except subprocess.SubprocessError as e:
		log.warning("NVDARemoteAudioServer logon task registration timed out or failed: %s", e)
		return False
	if result.returncode != 0:
		log.warning(
			"Logon task registration failed with code %s; stdout=%r stderr=%r",
			result.returncode,
			result.stdout,
			result.stderr,
		)
		return False
	return True


def _start_server_if_needed(exe):
	if _is_server_process_running():
		return
	_start_server_detached(exe)


def _stop_server_if_running():
	if not _is_server_process_running():
		return True
	try:
		result = subprocess.run(
			[
				"taskkill",
				"/IM",
				SERVER_EXE_NAME,
				"/F",
			],
			capture_output=True,
			text=True,
			timeout=20,
			creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
		)
	except (subprocess.SubprocessError, FileNotFoundError) as e:
		log.warning("Could not stop NVDARemoteAudioServer: %s", e)
		return False
	return result.returncode == 0 or not _is_server_process_running()


def _is_server_process_running():
	try:
		result = subprocess.run(
			[
				"tasklist",
				"/FI",
				"IMAGENAME eq {0}".format(SERVER_EXE_NAME),
				"/FO",
				"CSV",
				"/NH",
			],
			capture_output=True,
			text=True,
			timeout=15,
			creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
		)
	except (subprocess.SubprocessError, FileNotFoundError) as e:
		log.debug("Could not check whether NVDARemoteAudioServer is already running: %s", e)
		return False
	return result.returncode == 0 and SERVER_EXE_NAME.lower() in (result.stdout or "").lower()


def _start_server_detached(exe):
	"""Launch the server so it survives NVDA exit."""
	creationflags = (
		getattr(subprocess, "DETACHED_PROCESS", 0)
		| getattr(subprocess, "CREATE_NO_WINDOW", 0)
		| getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
	)
	subprocess.Popen(
		[exe],
		cwd=os.path.dirname(exe),
		creationflags=creationflags,
		close_fds=True,
		stdin=subprocess.DEVNULL,
		stdout=subprocess.DEVNULL,
		stderr=subprocess.DEVNULL,
	)


def _ensure_firewall_rules(exe):
	"""Add Windows Firewall inbound allow rules for the server's TCP and UDP port.

	Best effort. Triggers a UAC prompt if NVDA is not already elevated.
	Returns True on success, False if the user declined UAC or the command failed.
	"""
	if _firewall_rules_exist():
		return True

	exe_quoted = exe.replace("'", "''")
	tcp_name_quoted = FIREWALL_RULE_TCP.replace("'", "''")
	udp_name_quoted = FIREWALL_RULE_UDP.replace("'", "''")
	ps_command = (
		"$ErrorActionPreference='SilentlyContinue';"
		f"Remove-NetFirewallRule -DisplayName '{tcp_name_quoted}';"
		f"Remove-NetFirewallRule -DisplayName '{udp_name_quoted}';"
		"$ErrorActionPreference='Stop';"
		f"New-NetFirewallRule -DisplayName '{tcp_name_quoted}' -Direction Inbound -Action Allow "
		f"-Program '{exe_quoted}' -Protocol TCP -LocalPort {SERVER_PORT} -Profile Any | Out-Null;"
		f"New-NetFirewallRule -DisplayName '{udp_name_quoted}' -Direction Inbound -Action Allow "
		f"-Program '{exe_quoted}' -Protocol UDP -LocalPort {SERVER_PORT} -Profile Any | Out-Null;"
	)
	parameters = (
		'-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command '
		f'"{ps_command}"'
	)

	try:
		exit_code = _run_elevated("powershell.exe", parameters, wait_timeout_ms=60000)
	except PermissionError:
		log.info("User declined UAC for firewall rule setup")
		return False
	except OSError as e:
		log.warning("Could not launch elevated PowerShell: %s", e)
		return False

	if exit_code != 0:
		log.warning("Firewall rule PowerShell exited with code %s", exit_code)
		return False

	return True


def _remove_firewall_rules():
	"""Best-effort removal of the inbound rules this add-on created."""
	if not _firewall_rules_exist():
		return True
	tcp_name_quoted = FIREWALL_RULE_TCP.replace("'", "''")
	udp_name_quoted = FIREWALL_RULE_UDP.replace("'", "''")
	ps_command = (
		"$ErrorActionPreference='SilentlyContinue';"
		f"Remove-NetFirewallRule -DisplayName '{tcp_name_quoted}';"
		f"Remove-NetFirewallRule -DisplayName '{udp_name_quoted}';"
	)
	parameters = (
		'-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command '
		f'"{ps_command}"'
	)
	try:
		exit_code = _run_elevated("powershell.exe", parameters, wait_timeout_ms=60000)
	except PermissionError:
		log.info("User declined UAC for firewall rule removal")
		return False
	except OSError as e:
		log.warning("Could not launch elevated PowerShell for firewall removal: %s", e)
		return False
	return exit_code == 0


def _firewall_rules_exist():
	"""Return True iff both inbound rules are already present. Runs unelevated."""
	probe = (
		"$ErrorActionPreference='SilentlyContinue';"
		f"$tcp = Get-NetFirewallRule -DisplayName '{FIREWALL_RULE_TCP}';"
		f"$udp = Get-NetFirewallRule -DisplayName '{FIREWALL_RULE_UDP}';"
		"if ($tcp -and $udp) { exit 0 } else { exit 1 }"
	)
	try:
		result = subprocess.run(
			[
				"powershell.exe",
				"-NoProfile",
				"-ExecutionPolicy",
				"Bypass",
				"-Command",
				probe,
			],
			capture_output=True,
			text=True,
			timeout=15,
			creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
		)
	except (subprocess.SubprocessError, FileNotFoundError) as e:
		log.debug("Firewall probe failed: %s", e)
		return False
	return result.returncode == 0


def _run_elevated(exe, parameters, wait_timeout_ms):
	"""Run exe with parameters via UAC ("runas" verb) and wait for it to finish.

	Returns the process exit code. Raises PermissionError if the user cancelled UAC.
	If NVDA is already running elevated, no UAC prompt appears.
	"""
	info = _SHELLEXECUTEINFOW()
	info.cbSize = ctypes.sizeof(info)
	info.fMask = _SEE_MASK_NOCLOSEPROCESS | _SEE_MASK_NOASYNC
	info.hwnd = None
	info.lpVerb = "runas"
	info.lpFile = exe
	info.lpParameters = parameters
	info.nShow = _SW_HIDE

	shell32 = ctypes.windll.shell32
	kernel32 = ctypes.windll.kernel32
	shell32.ShellExecuteExW.argtypes = (ctypes.POINTER(_SHELLEXECUTEINFOW),)
	shell32.ShellExecuteExW.restype = wintypes.BOOL

	if not shell32.ShellExecuteExW(ctypes.byref(info)):
		err = kernel32.GetLastError()
		if err == _ERROR_CANCELLED:
			raise PermissionError("UAC prompt was cancelled.")
		raise OSError(f"ShellExecuteExW failed with Win32 error {err}")

	if not info.hProcess:
		return 0

	try:
		kernel32.WaitForSingleObject(info.hProcess, wait_timeout_ms)
		exit_code = wintypes.DWORD()
		kernel32.GetExitCodeProcess(info.hProcess, ctypes.byref(exit_code))
		return int(exit_code.value)
	finally:
		kernel32.CloseHandle(info.hProcess)


def add_firewall_rules_only(parent, on_done=None):
	"""Standalone path that just (re)installs firewall rules for an already-installed server."""
	exe = find_server_exe()
	if exe is None:
		gui.messageBox(
			_("Audio server is not installed. Install it first."),
			_("NVDA Remote Audio"),
			wx.OK | wx.ICON_INFORMATION,
			parent,
		)
		if on_done is not None:
			wx.CallAfter(on_done, False)
		return

	def worker():
		ok = _ensure_firewall_rules(exe)
		if ok:
			wx.CallAfter(ui.message, _("Firewall rules added"))
		else:
			message = _("Could not add firewall rules. Allow inbound TCP and UDP port {port} manually.").format(port=SERVER_PORT)
			wx.CallAfter(ui.message, message)
			_call_message_box(
				message,
				_("NVDA Remote Audio"),
				wx.OK | wx.ICON_WARNING,
			)
		if on_done is not None:
			wx.CallAfter(on_done, ok)

	threading.Thread(target=worker, name="addFirewallRules", daemon=True).start()


def offer_remove(parent, on_done=None):
	exe = find_server_exe()
	if exe is None and not (_run_key_exists() or _startup_shortcut_exists() or _legacy_logon_task_exists() or _firewall_rules_exist()):
		gui.messageBox(
			_("Audio server is not installed."),
			_("NVDA Remote Audio"),
			wx.OK | wx.ICON_INFORMATION,
			parent,
		)
		if on_done is not None:
			wx.CallAfter(on_done, False)
		return

	answer = gui.messageBox(
		_(
			"Disable the audio server on this computer?\n"
			"\n"
			"This stops the server and removes startup and firewall entries. Your NVDA Remote Audio Client settings are not changed."
		),
		_("NVDA Remote Audio: disable audio server?"),
		wx.YES_NO | wx.ICON_QUESTION,
		parent,
	)
	if answer != wx.YES:
		if on_done is not None:
			wx.CallAfter(on_done, False)
		return

	delete_files = False
	if exe is not None:
		delete_files = gui.messageBox(
			_("Also delete the installed server files at {path}?").format(path=os.path.dirname(exe)),
			_("NVDA Remote Audio"),
			wx.YES_NO | wx.ICON_QUESTION,
			parent,
		) == wx.YES

	threading.Thread(
		target=_remove_worker,
		args=(exe, delete_files, on_done),
		name="removeNVDARemoteAudioServer",
		daemon=True,
	).start()


def _remove_worker(exe, delete_files, on_done):
	ok = True
	try:
		if not _stop_server_if_running():
			ok = False
		_delete_run_key()
		_delete_startup_shortcut()
		_delete_legacy_logon_task()
		if not _remove_firewall_rules():
			ok = False
		if delete_files and exe is not None:
			install_dir = os.path.dirname(exe)
			try:
				shutil.rmtree(install_dir)
			except OSError as e:
				ok = False
				log.warning("Could not delete audio server install directory %s: %s", install_dir, e)
		if ok:
			wx.CallAfter(ui.message, _("Audio server disabled"))
		else:
			_call_message_box(
				_(
					"Audio server cleanup finished with warnings. Some startup, firewall, or file entries may need to be removed manually."
				),
				_("NVDA Remote Audio"),
				wx.OK | wx.ICON_WARNING,
			)
	except Exception as e:
		ok = False
		log.error("Failed to remove NVDARemoteAudioServer", exc_info=True)
		_call_message_box(
			_("Failed to remove audio server: {error}").format(error=e),
			_("NVDA Remote Audio"),
			wx.OK | wx.ICON_ERROR,
		)
	finally:
		if on_done is not None:
			wx.CallAfter(on_done, ok)


def server_status():
	exe = find_server_exe()
	return {
		"installed": exe is not None,
		"path": exe or "",
		"running": _is_server_process_running(),
		"startupRunKey": _run_key_exists(),
		"startupShortcut": _startup_shortcut_exists(),
		"legacyTask": _legacy_logon_task_exists(),
		"firewallRules": _firewall_rules_exist(),
	}


def _call_message_box(message, caption, style):
	wx.CallAfter(gui.messageBox, message, caption, style)
