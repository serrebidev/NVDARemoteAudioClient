# NVDARemoteAudioClient

NVDA add-on that streams system audio between two Windows machines using [NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer) as a relay. NVDA Remote already carries NVDA speech — this carries everything else (music, games, browser, Discord, etc.) without re-sending NVDA's own output.

## What's in here

- `addon/` — the NVDA add-on (Python, manifest, docs).
- `helper/` — a .NET 9 Windows helper EXE that does the actual capture, Opus encode, UDP, decode, and playback. Bundled into the add-on.
- `build.ps1` — builds the helper and packages a `.nvda-addon`.

The add-on is just a launcher and a settings UI. The helper does the audio work in a separate process so audio glitches and network jitter cannot stall NVDA.

## Install (end users)

You need both pieces, on both computers:

1. Install [NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer) on **whichever machine should send audio**. It listens on TCP+UDP port 6838.
2. On **both** machines, download `remoteAudioClient-<version>.nvda-addon` from [Releases](https://github.com/serrebidev/NVDARemoteAudioClient/releases) and open it. NVDA installs it.
3. Restart NVDA.
4. On **both** machines, open `NVDA menu > Preferences > Settings > NVDA Remote Audio` and set:
   - **Server host** — IP/hostname of the machine running NVDARemoteAudioServer.
   - **Audio port** — 6838 (default).
   - **Key** — same string on both sides. **Required, no default.**
5. Restart NVDA again, or pick `NVDA menu > Tools > NVDA Remote Audio > Receive remote audio` (client) / `Send this computer's audio` (server).

The add-on does not change anything in NVDA Remote. Use NVDA Remote on its usual port (6837) the same way you always have.

## Required system

- Windows 10 build **20348 or newer** (process-loopback exclusion API). This includes Windows 10 22H2 with recent updates and all Windows 11.
- NVDA 2025.1+.
- Inbound TCP+UDP 6838 reachable on the server side.

## How it sends "everything except NVDA"

In publisher mode the helper opens a [WASAPI process loopback](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params) capture with `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE` against the running `nvda.exe` PID. NVDA's audio (speech, tones) is never read off the device, so it cannot be sent. Everything else playing on the default render endpoint goes through Opus and UDP to the server.

## Latency

Wire format: 48 kHz stereo Opus, 15 ms packets (3 × 5 ms frames repacketized), with packet-loss concealment on the receiver. Receiver picks a jitter-buffer profile from the host you set:

| Profile | Picked when host is | Prebuffer | Output latency | Buffer cap |
|---|---|---|---|---|
| LAN | private/loopback IP | 60 ms | 60 ms | 300 ms |
| Tailscale | 100.64.0.0/10 or `*.ts.net` | 90 ms | 80 ms | 450 ms |
| Internet | anything else | 150 ms | 120 ms | 800 ms |

Override with `Settings > NVDA Remote Audio > Latency profile`.

## "Automatic" startup

If `C:\NVDARemoteAudioServer\NVDARemoteAudioServer.exe` exists on the machine, the add-on starts as **publisher** (sends). Otherwise it starts as **subscriber** (receives). Override under `Startup action` in settings.

## Build from source

You need the .NET 9 SDK. From an elevated-or-not PowerShell at the repo root:

```powershell
.\build.ps1
```

That publishes the helper, stages it into `addon/bin/`, and produces `remoteAudioClient-<version>.nvda-addon` at the repo root. See [`helper/README.md`](helper/README.md) and [`addon/README.md`](addon/README.md) for details.

## Security

Audio is sent **unencrypted over UDP**. Don't use this on the open internet without a VPN or Tailscale. The server's "key" is a shared identifier, not a secret — anyone who can reach port 6838 with the right key can join the channel.

## License

MIT. See [LICENSE](LICENSE).

## Credit

Wire protocol and relay server: [haitun001/NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer). This repo only ships the *client* side; you still need to run that server somewhere.
