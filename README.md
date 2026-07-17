# NVDA Remote Audio Client

A screen-reader-first NVDA add-on for sending live Windows audio between two computers with low delay, strong encryption, and no duplicate NVDA speech.

[![Join SerrebiProjects on Telegram](https://img.shields.io/badge/Telegram-SerrebiProjects-2CA5E0?style=for-the-badge&logo=telegram&logoColor=white)](https://t.me/SerrebiProjects)

**Have a question, hit a bug, or want early word on new releases?** Join the [SerrebiProjects Telegram group](https://t.me/SerrebiProjects) — the community hub for NVDA Remote Audio Client and my other projects, and the fastest place to get help.

## Features

- Sends all system audio except NVDA, or isolates one selected application.
- Keeps NVDA speech on NVDA Remote where it belongs, so you do not hear it twice.
- Encrypts audio end to end with an optional shared password; the relay never gets the password or unencrypted audio.
- Offers adaptive Opus, 5 ms live Opus, broadcast-quality Opus, and uncompressed PCM.
- Chooses sensible LAN, Tailscale, and Internet latency settings automatically.
- Lets the receiver choose a playback device, adjust volume and pan, and shape bass, midrange, and treble.
- Records received audio to timestamped WAV files.
- Saves complete connection setups as named profiles.
- Reconnects after sleep, resume, server restarts, and unexpected disconnects.
- Installs, updates, repairs, and removes the relay server from NVDA's Tools menu.
- Provides accessible menus, spoken status, copyable diagnostics, a helper self-test, and bindable NVDA Input Gestures.

## Download and install

Grab `remoteAudioClient-X.Y.Z.nvda-addon` from the [Releases page](https://github.com/serrebidev/NVDARemoteAudioClient/releases), open it, and let NVDA install it. Install the same version on both computers, then restart NVDA.

For the highlights in version 0.2.1, see the [release notes](RELEASE_NOTES.md).

## First connection

1. On the computer that will send audio, open **NVDA menu > Tools > NVDA Remote Audio > Install audio server (this machine sends audio)...**
2. Approve the firewall prompt if other computers need to reach this machine. The server uses TCP and UDP port 6838.
3. On both computers, open **NVDA menu > Preferences > Settings > NVDA Remote Audio**.
4. Enter the sender's host name or IP address, port 6838, and the same session key on both sides. The session key is a room name, not a password.
5. Set the same **End-to-end encryption password** on both computers. This is strongly recommended.
6. Choose **Send this computer's audio** on the sender and **Receive remote audio** on the receiver.

The add-on normally detects its role for you: a computer with the audio server sends, and a computer without it receives. You can override that under **Startup action**.

## Audio choices

**Audio to send** defaults to the full system mix with NVDA excluded at the Windows audio layer. To send one application, start it and make it create an audio session before opening settings. Reconnect remote audio if that application restarts.

**Audio quality** has four choices:

- **Adaptive Opus** follows the selected latency profile.
- **Opus live** uses 5 ms frames for the lowest codec delay.
- **Opus broadcast** favors quality and resilience over the last few milliseconds.
- **PCM** sends uncompressed 48 kHz stereo audio and is intended for a clean LAN.

The receiver can follow the Windows default playback device or stay pinned to another active output. Volume ranges from 0 to 200 percent, pan ranges from full left to full right, and each EQ band ranges from -12 to +12 dB.

## Controls and profiles

The **NVDA Remote Audio** Tools submenu includes receive, send, reconnect, disconnect, recording, recordings folder, status, diagnostics, helper self-test, connection profiles, audio-server management, and settings.

Receive, send, reconnect, disconnect, status, diagnostics, and recording are also available as unbound commands under the **NVDA Remote Audio** Input Gestures category. These gestures stay local while you control another computer through NVDA Remote.

Connection profiles save the host, room, password, role, quality, routing, playback, recording, and latency settings together. Use **Save current settings as profile**, **Load connection profile**, and **Delete connection profile** from the Tools submenu.

## Security and compatibility

With a password set, every audio packet is authenticated and encrypted with AES-256-GCM before it leaves the helper. A room-specific key is derived with PBKDF2-SHA256, and a wrong password is rejected clearly. The password is passed to the helper through an environment variable instead of its visible command line.

Leaving the password empty enables unencrypted compatibility with older add-on versions. Use that only on a trusted LAN or inside a VPN such as Tailscale. Both computers need version 0.2.0 or newer for encrypted audio and PCM.

The add-on still uses [NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer) 0.5 as its relay. Payload v2 is opaque to the server, so encryption and the new codecs do not require a replacement relay protocol.

## Requirements

- Windows 10 build 20348 or newer, or Windows 11.
- NVDA 2025.1 or newer.
- TCP and UDP port 6838 reachable on the sending computer.
- The same add-on version on both computers for the smoothest upgrade.

Settings are stored in `%APPDATA%\nvda\remoteAudioClient.json`. Recordings default to `%USERPROFILE%\Documents\NVDA Remote Audio Recordings`. Removing the add-on does not delete either location.

## Building

Install the .NET 9 SDK and Python, then run:

```powershell
.\run-tests.ps1
.\integration-test.ps1
.\build.ps1
```

The integration test uses an installed `NVDARemoteAudioServer.exe` on isolated test ports. The build produces `dist\remoteAudioClient-X.Y.Z.nvda-addon` and includes a self-contained Windows x64 helper.

See [helper/README.md](helper/README.md) for the audio and protocol internals and [addon/README.md](addon/README.md) for the NVDA-side configuration and process supervision.

## Debug logging

Enable **Verbose diagnostic logging** in settings when you need helper timing, packet, buffer, authentication, or recording details in `nvda.log`. Leave it off for normal use. **Copy audio diagnostics** gives you a compact report without exposing the encryption password.

## Contributing

Pull requests are welcome. If NVDA Remote Audio Client has been useful to you, open a PR with a fix or feature and I'll review it.

## Credit

[Ednunp/RemSound](https://github.com/Ednunp/RemSound) inspired the application isolation, encrypted transport, quality choices, audio shaping, recording, profiles, and accessible workflow in this release.

[haitun001/NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer) provides the relay protocol and server. This repository ships the client add-on and helper.

## License

MIT. See [LICENSE](LICENSE).

## Community and support

Report bugs and request features in [Issues](https://github.com/serrebidev/NVDARemoteAudioClient/issues). For questions, feedback, and release news, join the [SerrebiProjects Telegram group](https://t.me/SerrebiProjects).
