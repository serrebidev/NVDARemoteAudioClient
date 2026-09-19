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
  "transport": "remsound",
  "host": "127.0.0.1",
  "port": 6838,
  "key": "",
  "remSoundPeers": "",
  "remSoundDevices": [],
  "remSoundDeviceName": "",
  "allowRemoteControl": false,
  "bitrate": 128000,
  "captureProcess": "",
  "captureDevice": "",
  "outputDeviceId": "",
  "receiveVolume": 100,
  "receivePan": 0,
  "bassDb": 0,
  "midDb": 0,
  "trebleDb": 0,
  "password": "",
  "qualityMode": "adaptive",
  "recordReceived": false,
  "recordingFolder": "%USERPROFILE%\\Documents\\NVDA Remote Audio Recordings",
  "startupMode": "auto",
  "latencyProfile": "auto",
  "announceStatus": true,
  "useFec": true,
  "verboseLogging": false,
  "profiles": {},
  "activeProfile": ""
}
```

- `transport`: `remsound` | `nvda`. `remsound` (the default) talks peer to peer with the RemSound apps, ignores `host` and `key` entirely, and requires `password`. `nvda` uses the audio server and needs `host`, `port`, and `key`. A config written before this key existed is read as `nvda`, because that is what it was written under and RemSound cannot connect without a password.
- `key` is the session key / room name. It is required on the relay path and must match on both sides, but it is not an encryption password.
- `startupMode`: `auto` | `disabled` | `subscriber` | `publisher` | `duplex`. `auto` picks `publisher` if `C:\NVDARemoteAudioServer\NVDARemoteAudioServer.exe` or `%LOCALAPPDATA%\NVDARemoteAudioServer\NVDARemoteAudioServer.exe` exists on the machine, else `subscriber`. `duplex` sends and receives at once and is only accepted with `transport: remsound`.
- `latencyProfile`: `auto` | `wired` | `lan` | `tailscale` | `internet`. These set the receive-side buffer, so they affect what this computer hears, not what it sends. `wired` is the helper's floor (5/5/60 ms) and is never chosen automatically: it is for a sender on a cable, and a Wi-Fi sender needs the room `lan` keeps. With `transport: remsound`, `auto` picks `lan` when no address was typed, because a device chosen by name was found by LAN discovery, and otherwise resolves the first typed address. On the relay path it resolves `host`: private IP or loopback → LAN, 100.64.0.0/10 or `*.ts.net` → Tailscale, else Internet.
- `captureProcess`: empty sends system audio with NVDA excluded; otherwise it is a stable process name selected from current Windows audio sessions.
- `captureDevice`: a recording-device ID (`default` for the Windows default), selected from the microphone entries in the same list. When set it wins over `captureProcess`, so a RemSound connection can send a microphone instead of playback audio.
- `outputDeviceId`: empty follows the Windows default playback device; otherwise it is an active render endpoint ID.
- `receiveVolume`: subscriber gain from 0 through 200 percent.
- `receivePan`: subscriber pan from -100 through 100.
- `bassDb`, `midDb`, `trebleDb`: three-band receive EQ, each from -12 through +12 dB.
- `password`: optional on the relay path, mandatory for `remsound`. It is passed to the helper through an environment variable, never a command-line argument.
- `remSoundPeers`: comma-separated `host[:port]` to send to and accept audio from. A RemSound relay host works here too. Entries the helper would reject are dropped here rather than in the helper, so one typo cannot stop the connection.
- `remSoundDevices`: device names chosen from **Find RemSound devices on this network**. Stored by name and matched as they appear, so a device that changes address keeps working.
- `remSoundDeviceName`: what other RemSound devices call this computer. Blank uses the computer name.
- `allowRemoteControl`: when true, RemSound peers that know the password may change the volume of the audio they are sending to this computer. False by default. A peer can never change this computer's Windows volume, whatever this is set to, so the key kept its name rather than becoming one about the speaker volume.
- `qualityMode`: `adaptive` | `opusLive` | `opusBroadcast` | `pcm`.
- `recordReceived` and `recordingFolder`: timestamped 48 kHz stereo float-WAV recording.
- `profiles` and `activeProfile`: named snapshots managed from the Tools menu. A snapshot carries every key above, so a profile remembers its connection type and RemSound devices.

- `announceStatus`: when false, routine connect/listen/capture/stopped messages are kept quiet. Errors and explicit commands such as status still speak.
- `useFec`: when true, the helper enables Opus in-band packet-loss recovery. Turning it off appends `--disable-fec` for advanced low-overhead LAN testing.
- `verboseLogging`: when true, helper diagnostic lines are written to `nvda.log`. Errors and non-diagnostic lifecycle events are still logged when this is false.

The add-on also exposes unbound NVDA Input Gestures under the `NVDA Remote Audio` category: receive, send, send and receive at the same time, disconnect, reconnect, report status, find RemSound devices, copy diagnostics, raise/lower/mute received audio, and six remote-control commands — the other device's RemSound volume and the other device's Windows volume, each up, down, and mute. Those gestures are registered with NVDA Remote's local-script list so they still run on the machine where you press them while you are controlling another machine with F11. None of them is given a default key.

## Reporting this computer's address

`Tools > NVDA Remote Audio > This computer's address for the other computer`
(also an unbound gesture) speaks and copies the address to type on the other
machine. `_detectTailscaleAddress()` connects a UDP socket to Tailscale's
MagicDNS resolver at `100.100.100.100:53` and reads back the local address the
route picked, which is this machine's tailnet address; nothing is transmitted,
and a result outside `100.64.0.0/10` is discarded as not being Tailscale.
`_detectLanAddress()` does the same against a routable address, falling back to
resolving the computer's own name for a LAN with no default route.

## Testing without NVDA

`python tools/selftest_addon.py` stubs the NVDA modules this plugin imports,
points its configuration at a scratch directory, and drives the real module —
configuration normalization and round-trip, damaged configuration files, key
validation, latency and quality resolution, startup-role selection, address
detection, spoken labels, helper-event routing, RemSound address and device-name
parsing, live volume from both ends, device grouping, and the exact arguments and
environment the helper would be launched with. No NVDA, no relay, and no helper
binary required. `run-tests.ps1` runs it.

Three checks exist specifically to keep secrets and confusion where they belong:
the encryption password must not appear in the helper's command line (any process
on the machine can read another's), must not appear in copied diagnostics (those
get pasted into bug reports), and a RemSound connection must not be started at all
without one, because it could never connect.

`python tools/mutation_check.py` breaks one thing at a time on purpose and
reports anything the suite fails to notice — a suite that always passes is
indistinguishable from one that tests nothing. It needs a clean working tree and
takes a few minutes, so it is not part of `run-tests.ps1`.

## Add-on reload safety

Remote Audio detaches its menu references immediately during Reload Add-ons, removes the stale menu item after the active event returns, and retains the unsafe detached wx wrapper until NVDA exits. This prevents NVDA from terminating when reload is started from the Tools menu or its assigned keyboard gesture.

## How the auto-retry works

If the helper exits unexpectedly (network drop, server restart, etc.) and the auto-start role is still configured, the plugin retries after 5 seconds. Manual disconnect (`Tools > NVDA Remote Audio > Disconnect audio`) sets `_manualStop` and stops the retry loop. Changing settings during a run does not interrupt the running helper — restart from the menu to apply.

## Helper invocation

The plugin builds a command line with `subprocess.Popen` and reads its stdout (the helper writes one JSON line per event). Examples:

```
NVDARemoteAudioHelper.exe --role subscriber --host <host> --port <port> --key <key> \
  --opus-frame-ms <n> --prebuffer-ms <n> --output-latency-ms <n> --buffer-ms <n> \
  --codec <opus|pcm> --password-env <variable> --output-device-id <id> \
  --receive-volume <percent> --receive-pan <value> --bass-db <db> --mid-db <db> \
  --treble-db <db> --record-folder <path>

NVDARemoteAudioHelper.exe --role publisher --host <host> --port <port> --key <key> \
  --opus-frame-ms <n> --codec <opus|pcm> --password-env <variable> \
  --exclude-pid <NVDA pid> --bitrate <bps>

NVDARemoteAudioHelper.exe --role publisher --host <host> --port <port> --key <key> \
  --opus-frame-ms <n> --codec <opus|pcm> --password-env <variable> \
  --include-process-name <name> --bitrate <bps>

NVDARemoteAudioHelper.exe --transport remsound --role <publisher|subscriber|duplex> \
  --peer-names <devices> --device-name <name> --password-env <variable> \
  --opus-frame-ms <n> --codec <opus|pcm> --capture-device-id <id> \
  --allow-remote-control --prebuffer-ms <n> --output-latency-ms <n> --buffer-ms <n> \
  --receive-volume <percent>
```

`--exclude-pid` is set to NVDA's own PID (`os.getpid()` from inside the plugin), which makes WASAPI drop NVDA's audio. A configured application replaces it with `--include-process-name`, and a configured microphone replaces both with `--capture-device-id`. A RemSound invocation carries `--transport remsound` and no `--host` or `--key` at all; `--allow-remote-control` appears only when the user has ticked it. Payload v2 adds codec metadata and optional AES-256-GCM encryption while remaining opaque to the existing relay. Updated subscribers also accept legacy Opus when no password is required.

While the helper runs, the add-on writes one-line commands to its standard input for live changes: `!volume 120`, `!volume-step -5`, `!mute toggle`, and `!control volume-down`. Anything else, or closing the pipe, still means shut down, exactly as before, so a volume change costs nothing and an old add-on's shutdown still works.

## Build a `.nvda-addon`

From the repo root:

```powershell
.\build.ps1
```

Or by hand: publish the helper, stage only `manifest.ini`, `readme.html`, `globalPlugins/`, and `bin/NVDARemoteAudioHelper.exe`, then zip the staged contents into `remoteAudioClient-<version>.nvda-addon`. The zip's root must contain `manifest.ini`, not a wrapping folder.

## Install for development

To iterate on the Python without rebuilding the EXE every time:

1. `dotnet publish` the helper once (or run `build.ps1`).
2. Copy `addon/bin/NVDARemoteAudioHelper.exe` to `%APPDATA%\nvda\addons\remoteAudioClient\bin\`.
3. Symlink (or copy) `addon/globalPlugins` → `%APPDATA%\nvda\addons\remoteAudioClient\globalPlugins`.
4. Symlink `addon/manifest.ini` and `addon/readme.html` into `%APPDATA%\nvda\addons\remoteAudioClient\`.
5. Reload plugins with `NVDA+Ctrl+F3`, or restart NVDA.

## License

MIT — see the repo root `LICENSE`.
