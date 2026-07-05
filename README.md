# NVDARemoteAudioClient

NVDA add-on that streams system audio between two Windows machines, using [NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer) as the relay. NVDA Remote already carries NVDA's speech; this carries everything else — music, games, browser audio, Discord, whatever — without touching NVDA's own output.

[![Join SerrebiProjects on Telegram](https://img.shields.io/badge/Telegram-SerrebiProjects-2CA5E0?style=for-the-badge&logo=telegram&logoColor=white)](https://t.me/SerrebiProjects)

**Have a question, hit a bug, or want early word on new releases?** Join the [SerrebiProjects Telegram group](https://t.me/SerrebiProjects) — the community hub for this add-on and my other projects, and the fastest place to get help.

## Features

- Sends or receives system audio between two Windows machines — music, games, Discord, anything on the default output device except NVDA's own speech.
- Excludes NVDA's own audio at the OS level (WASAPI process-loopback), so it can never leak into the stream.
- Auto-detects role on startup: sends if the audio server is installed on this machine, receives otherwise. Override it if you want.
- Auto-picks a latency profile (LAN, Tailscale, Internet) from the server address you set, each tuned with its own prebuffer and buffer cap.
- One-click install, update, repair, and removal of the relay server (NVDARemoteAudioServer), including firewall rules.
- Tray menu and unbound NVDA Input Gestures for receive, send, reconnect, disconnect, status, and diagnostics.
- Auto-reconnects after sleep/resume and on unexpected disconnects.
- Does not touch NVDA Remote. Keep using NVDA Remote on port 6837 exactly like you always have.

## Install

1. On **both** machines: download `remoteAudioClient-<version>.nvda-addon` from [Releases](https://github.com/serrebidev/NVDARemoteAudioClient/releases) and open it. NVDA installs it.
2. Restart NVDA.
3. On the machine that will **send** audio: `NVDA menu > Tools > NVDA Remote Audio > Install audio server (this machine sends audio)...`. This downloads [NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer), installs it to `C:\NVDARemoteAudioServer\` (or `%LOCALAPPDATA%\NVDARemoteAudioServer\` if that's not writable), starts it, sets it to auto-start at sign-in, and prompts for UAC to add firewall rules for TCP+UDP 6838. Say no to the UAC prompt and everything else still installs — run `Add firewall rules for audio server...` later if you change your mind.
4. On **both** machines: `NVDA menu > Preferences > Settings > NVDA Remote Audio`, set:
   - **Server host** — IP or hostname of the sending machine.
   - **Audio port** — 6838. Don't change it unless you also changed it on the server.
   - **Session key / room name** — same string on both sides. Required, no default. **This is a room name, not a password.**
5. Restart NVDA, or just pick `Receive remote audio` / `Send this computer's audio` from the Tools menu right now. Picking `Send` when the server isn't installed offers to install it on the spot.

## Required system

- Windows 10 build **20348 or newer** (process-loopback exclusion API). Any recently-updated Windows 10 22H2, or any Windows 11, qualifies.
- NVDA 2025.1 or newer.
- Inbound TCP+UDP 6838 reachable on the server machine.

## Controls

Tools menu: receive, send, reconnect, disconnect, status, copy diagnostics, install/update/remove server, add firewall rules, settings. The receive/send/disconnect/reconnect/status/diagnostics actions are also unbound NVDA Input Gestures under the `NVDA Remote Audio` category — bind your own keys if you want them. While you're controlling another machine through NVDA Remote, those gestures stay local instead of getting sent as keystrokes to the remote side.

Settings that matter:

- **Announce connection status** — on by default. Turn off if you don't want "Connecting" / "Connected" spoken every time the link comes up. Errors still speak either way.
- **Opus packet-loss recovery (FEC)** — on by default. Turn off only if you're deliberately testing raw packet-loss behavior on a LAN.
- **Verbose logging** — off by default. Turn on when you need helper timing details in `nvda.log` for a bug report, not for normal use.
- **Latency profile** — auto-detected from server host (see table below). Override it if the auto-pick guesses wrong for your network.
- **Startup action** — auto-detected: publisher if the audio server is installed on this machine, subscriber otherwise. Override it if you want a fixed role or no auto-connect at all.

## How it works

Publisher side opens a [WASAPI process-loopback](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params) capture with `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE` against `nvda.exe`'s own PID. NVDA's speech and tones never get captured, period. Everything else on the default render device gets Opus-encoded and sent over UDP.

Wire format is 48 kHz stereo Opus over UDP. LAN uses 5 ms Opus packets; Tailscale and Internet profiles use 10 ms packets. FEC, packet-loss concealment, a low-latency WASAPI event-sync output path, and drift-corrected playout buffering are all always active on the receiving end.

| Profile | Picked when host is | Prebuffer | Output latency | Buffer cap |
|---|---|---|---|---|
| LAN | private/loopback IP | 15 ms | 15 ms | 120 ms |
| Tailscale | 100.64.0.0/10 or `*.ts.net` | 50 ms | 20 ms | 250 ms |
| Internet | anything else | 100 ms | 30 ms | 600 ms |

## Security — read this

Audio goes out **unencrypted, over UDP**. Do not run this over the open internet without a VPN or Tailscale. The session key is a shared room name, not a secret — anyone who reaches port 6838 with the right key is in your channel, full stop.

## Building from source

You need the .NET 9 SDK. From PowerShell at the repo root:

```powershell
.\build.ps1
```

This builds the helper, stages a clean add-on package, validates it, and produces `dist/remoteAudioClient-<version>.nvda-addon`. See [`helper/README.md`](helper/README.md) and [`addon/README.md`](addon/README.md) for the internals — `addon/` is the NVDA add-on (Python), `helper/` is the .NET 9 EXE that does the actual capture/encode/decode/playback as a separate process, so audio glitches and network jitter can't stall NVDA.

## Contributing

Pull requests are welcome. If this add-on has been useful to you, open a PR with a fix or feature and I'll review it.

## License

MIT. See [LICENSE](LICENSE).

## Credit

Original idea: [Ednunp/RemSound](https://github.com/Ednunp/RemSound), a standalone Windows app for streaming low-latency audio between computers with screen-reader accessibility in mind. This repo builds on that idea, packaged as a single self-contained NVDA add-on.

Wire protocol and relay server: [haitun001/NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer). This repo only ships the *client* side; you still need to run that server somewhere.

## Community and support

Report bugs and request features in [Issues](https://github.com/serrebidev/NVDARemoteAudioClient/issues). For questions, feedback, and release news, join the [SerrebiProjects Telegram group](https://t.me/SerrebiProjects).
