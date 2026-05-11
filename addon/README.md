# remoteAudioClient (NVDA add-on)

NVDA add-on side of [NVDA Remote Audio Client](../README.md). Spawns and supervises `NVDARemoteAudioHelper.exe` based on user settings. Does not touch the audio device itself — that's the helper's job.

## What's here

- `manifest.ini` — name, version, NVDA compatibility, doc pointer.
- `readme.html` — bundled in-NVDA add-on docs (what users see in `Tools > Add-ons`).
- `globalPlugins/remoteAudioClient/__init__.py` — the whole plugin: settings panel, NVDA Settings category, Tools-menu submenu, helper process supervision, auto-start/auto-retry.
- `bin/NVDARemoteAudioHelper.exe` — built by `build.ps1`. Not in git.

## Settings (where it persists)

`%APPDATA%\nvda\remoteAudioClient.json`. Schema:

```json
{
  "host": "127.0.0.1",
  "port": 6838,
  "key": "",
  "bitrate": 128000,
  "startupMode": "auto",
  "latencyProfile": "auto"
}
```

- `key` is required. The plugin refuses to start if it's blank.
- `startupMode`: `auto` | `disabled` | `subscriber` | `publisher`. `auto` picks `publisher` if `C:\NVDARemoteAudioServer\NVDARemoteAudioServer.exe` or `%LOCALAPPDATA%\NVDARemoteAudioServer\NVDARemoteAudioServer.exe` exists on the machine, else `subscriber`.
- `latencyProfile`: `auto` | `lan` | `tailscale` | `internet`. `auto` picks based on the `host` value (private IP → LAN, 100.64.0.0/10 or `*.ts.net` → Tailscale, else Internet).

## How the auto-retry works

If the helper exits unexpectedly (network drop, server restart, etc.) and the auto-start role is still configured, the plugin retries after 5 seconds. Manual disconnect (`Tools > NVDA Remote Audio > Disconnect audio`) sets `_manualStop` and stops the retry loop. Changing settings during a run does not interrupt the running helper — restart from the menu to apply.

## Helper invocation

The plugin builds a command line with `subprocess.Popen` and reads its stdout (the helper writes one JSON line per event). Examples:

```
NVDARemoteAudioHelper.exe --role subscriber --host <host> --port <port> --key <key> \
  --prebuffer-ms <n> --output-latency-ms <n> --buffer-ms <n>

NVDARemoteAudioHelper.exe --role publisher --host <host> --port <port> --key <key> \
  --exclude-pid <NVDA pid> --bitrate <bps>
```

`--exclude-pid` is set to NVDA's own PID (`os.getpid()` from inside the plugin), which is what makes WASAPI drop NVDA's audio from the captured stream.

## Build a `.nvda-addon`

From the repo root:

```powershell
.\build.ps1
```

Or by hand: publish the helper, copy it into `addon/bin/`, then zip the contents of `addon/` into `remoteAudioClient-<version>.nvda-addon`. The zip's root must contain `manifest.ini`, not a wrapping folder.

## Install for development

To iterate on the Python without rebuilding the EXE every time:

1. `dotnet publish` the helper once (or run `build.ps1`).
2. Copy `addon/bin/NVDARemoteAudioHelper.exe` to `%APPDATA%\nvda\addons\remoteAudioClient\bin\`.
3. Symlink (or copy) `addon/globalPlugins` → `%APPDATA%\nvda\addons\remoteAudioClient\globalPlugins`.
4. Symlink `addon/manifest.ini` and `addon/readme.html` into `%APPDATA%\nvda\addons\remoteAudioClient\`.
5. Reload plugins with `NVDA+Ctrl+F3`, or restart NVDA.

## License

MIT — see the repo root `LICENSE`.
