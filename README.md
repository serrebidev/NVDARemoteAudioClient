# NVDA Remote Audio Client

A screen-reader-first NVDA add-on for sending live Windows audio between two computers with low delay, strong encryption, and no duplicate NVDA speech.

[![Join SerrebiProjects on Telegram](https://img.shields.io/badge/Telegram-SerrebiProjects-2CA5E0?style=for-the-badge&logo=telegram&logoColor=white)](https://t.me/SerrebiProjects)

**Questions, bugs, or release news?** Join the [SerrebiProjects Telegram group](https://t.me/SerrebiProjects), the fastest place to get help.

## Features

- Connects two ways, and defaults to peer to peer with the [RemSound](https://github.com/Ednunp/RemSound) apps for Windows, iPhone, Android, and Mac. An [NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer) relay is the other choice, for two Windows computers.
- Sends all system audio except NVDA, isolates one selected application, or sends a microphone.
- Sends and receives at the same time on a RemSound connection, so a phone can hear this computer and this computer can hear the phone.
- Keeps NVDA speech on NVDA Remote where it belongs, so you do not hear it twice.
- Encrypts audio end to end with an optional shared password; neither the relay nor the network gets the password or unencrypted audio.
- Offers adaptive Opus, 5 ms live Opus, broadcast-quality Opus, and uncompressed PCM.
- Chooses sensible LAN, Tailscale, and Internet latency settings automatically, with a lower-latency **Wired** profile for a sender on a cable.
- Lets the receiver choose a playback device, adjust volume and pan, and shape bass, midrange, and treble.
- Changes received volume, mute, and the other device's volume from a gesture, without reconnecting.
- Finds RemSound devices on the network and remembers them by name, even when their address changes.
- Records received audio to timestamped WAV files.
- Saves complete connection setups as named profiles.
- Reconnects after sleep, resume, server restarts, and unexpected disconnects.
- Follows the receiving computer's audio output when headphones are unplugged or the default device changes.
- Reports this computer's Tailscale and local network address for setting up the other computer.
- Says which computer's add-on to update when the two versions cannot talk to each other.
- Installs, updates, repairs, and removes the relay server from NVDA's Tools menu.
- Provides accessible menus, spoken status, copyable diagnostics, a helper self-test, and bindable NVDA Input Gestures.

## Download and install

Grab `remoteAudioClient-X.Y.Z.nvda-addon` from the [Releases page](https://github.com/serrebidev/NVDARemoteAudioClient/releases), open it, and let NVDA install it. Install the same version on both computers, then restart NVDA.

For the highlights in version 0.2.8, see the [release notes](RELEASE_NOTES.md).

## First connection

There are two ways to connect, and the **Connection type** setting picks between them. It defaults to **RemSound peer to peer**, which is the one to use with a phone or Mac; choose **NVDA Remote Audio server** instead when both ends are Windows computers using the relay.

### Through the audio server (both computers run Windows)

1. On the computer that will send audio, open **NVDA menu > Tools > NVDA Remote Audio > Install audio server (this machine sends audio)...**
2. Approve the firewall prompt if other computers need to reach this machine. The server uses TCP and UDP port 6838.
3. On both computers, open **NVDA menu > Preferences > Settings > NVDA Remote Audio** and leave **Connection type** on **NVDA Remote Audio server**.
4. Enter the sender's host name or IP address, port 6838, and the same session key on both sides. The session key is a room name, not a password.
5. Set the same **End-to-end encryption password** on both computers. This is strongly recommended.
6. Choose **Send this computer's audio** on the sender and **Receive remote audio** on the receiver.

The add-on normally detects its role for you: a computer with the audio server sends, and a computer without it receives. You can override that under **Startup action**.

### Peer to peer with a RemSound device (Windows, iPhone, Android, or Mac)

1. On both devices, set the **same encryption password**. RemSound always encrypts, so a RemSound connection has no unencrypted mode.
2. On this computer, set **Connection type** to **RemSound peer to peer**.
3. To find the other device by name, open **NVDA menu > Tools > NVDA Remote Audio > Find RemSound devices on this network...**, make sure the RemSound app is open on the other device, and tick it in the list. The connection type is switched to RemSound for you, and the add-on tells you if the encryption password is still missing — a RemSound connection cannot start without one. Choosing a device does not start the audio by itself; it says which menu item does.
4. If discovery cannot reach it — over Tailscale or the open internet, for example — type its address or Tailscale name into **RemSound devices by address** instead.
5. In the other device's own RemSound app, tick this computer the same way it appears there, and make sure that app is actually sending its microphone — RemSound on a phone does not send until sending is switched on, and its per-device status text says which of the two is stopping it.
6. Choose **Receive remote audio**, **Send this computer's audio**, or **Send and receive at the same time (RemSound)**.

This computer keeps one device identity for the life of the installation, so the other device only has to be set up once: it still recognizes this computer after a restart, and after this computer's address changes.

RemSound connections use UDP port 47830, with discovery on UDP 47821, and need no relay server at all.

## Audio choices

**Audio to send** defaults to the full system mix with NVDA excluded at the Windows audio layer. To send one application, start it and make it create an audio session before opening settings. Reconnect remote audio if that application restarts. To send a microphone instead — which is how a phone hears this computer on a RemSound connection — choose **Microphone: Windows default recording device** or a named device from the same list.

**Audio quality** has four choices:

- **Adaptive Opus** follows the selected latency profile.
- **Opus live** uses 5 ms frames for the lowest codec delay.
- **Opus broadcast** favors quality and resilience over the last few milliseconds.
- **PCM** sends uncompressed 48 kHz stereo audio and is intended for a clean LAN.

On a RemSound connection the same choices are used, with RemSound's own wire encoding: Opus at 48 kHz stereo, or 24-bit PCM in 2.5 ms frames. The sender re-announces the format every quarter of a second, so a device that joins late, or misses one announcement, picks the stream up without reconnecting.

The receiver can follow the Windows default playback device or stay pinned to another active output. Following the default now means following it for the whole session: unplugging headphones, a Bluetooth link dropping, or changing the Windows output moves playback to the endpoint that is actually current instead of leaving it on the old one. Volume ranges from 0 to 200 percent, pan ranges from full left to full right, and each EQ band ranges from -12 to +12 dB.

## Controls and profiles

The **NVDA Remote Audio** Tools submenu includes receive, send, send and receive at the same time, reconnect, disconnect, recording, recordings folder, status, this computer's address, find RemSound devices, diagnostics, helper self-test, connection profiles, audio-server management, and settings.

**This computer's address for the other computer** speaks and copies this machine's Tailscale address, local network address, computer name, and port — the details to type on the other computer during setup.

**Find RemSound devices on this network...** lists every RemSound device that answered, saying for each one whether it sends, receives, or both, and every address it answered on. That list is also what the settings panel's device picker fills from.

Received volume, mute, and the other device's volume can be changed from a gesture while audio is running, with no reconnect and no trip back to settings. See **Input Gestures > NVDA Remote Audio** for **Raise/Lower/Mute received remote audio**, **Raise/Lower the other device's RemSound volume**, **Mute or unmute RemSound on the other device**, and the three matching Windows-volume commands.

Receive, send, send and receive at the same time, reconnect, disconnect, status, this computer's address, find RemSound devices, diagnostics, recording, and every volume and remote-control command are also available as unbound commands under the **NVDA Remote Audio** Input Gestures category. These gestures stay local while you control another computer through NVDA Remote.

Connection profiles save the connection type, host or RemSound devices, room, password, role, quality, routing, playback, recording, and latency settings together. Use **Save current settings as profile**, **Load connection profile**, and **Delete connection profile** from the Tools submenu.

## Security and compatibility

With a password set, every audio packet is authenticated and encrypted with AES-256-GCM before it leaves the helper. A room-specific key is derived with PBKDF2-SHA256, and a wrong password is rejected clearly. The password is passed to the helper through an environment variable instead of its visible command line.

On a RemSound connection the password is not optional: RemSound has no unencrypted mode, and the key is derived exactly as the RemSound apps derive it, so the same password on both devices is what makes them understand each other. A sender whose password differs is reported as a password problem, not as silence.

The one exception is the relay path, where leaving the password empty enables unencrypted compatibility with older add-on versions. Use that only on a trusted LAN or inside a VPN such as Tailscale. Both computers need version 0.2.0 or newer for encrypted audio and PCM.

A RemSound device can change the volume of the audio it is sending to this computer only if **Let RemSound devices that share the password change the volume of the audio they send here** is ticked. Those commands are sealed with the audio key, ignored when they are more than ten minutes old, and ignored when replayed, so nobody without the password can forge one.

**No RemSound device can change this computer's Windows volume.** That is true whether or not the setting above is ticked: the helper contains no code that sets an endpoint volume, so a speaker volume that moves on its own can never come from a peer. This computer can still change the other device's volume, from its own gestures, because that is you acting deliberately on the device in front of you.

When the two computers cannot understand each other's audio, the receiver says which one to update rather than falling silent. A wrong encryption password, a damaged network path, and a version mismatch are reported as three different problems, because they are fixed on different machines.

The relay path still uses [NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer) 0.5. Payload v2 is opaque to the server, so encryption and the codecs do not require a replacement relay protocol. The RemSound path does not use that server at all.

## Requirements

- Windows 10 build 20348 or newer, or Windows 11.
- NVDA 2025.1 or newer.
- Through the audio server: TCP and UDP port 6838 reachable on the sending computer.
- Peer to peer with RemSound: UDP port 47830 reachable on both devices, plus UDP 47821 if discovery is to work. No server is installed and no firewall rule for port 6838 is needed.
- The same add-on version on both computers for the smoothest upgrade.

Settings are stored in `%APPDATA%\nvda\remoteAudioClient.json`. Recordings default to `%USERPROFILE%\Documents\NVDA Remote Audio Recordings`. Removing the add-on does not delete either location.

## Building

Install the .NET 9 SDK and Python, then run:

```powershell
.\run-tests.ps1
.\integration-test.ps1
.\build.ps1
```

`run-tests.ps1` includes the helper self-tests and `python tools/selftest_addon.py`, which exercises the add-on's own logic with the NVDA modules stubbed out, so it needs neither NVDA nor a relay. `python tools/mutation_check.py` breaks one thing at a time on purpose and reports anything the suite fails to notice.

The integration test uses an installed `NVDARemoteAudioServer.exe` on isolated test ports. The build produces `dist\remoteAudioClient-X.Y.Z.nvda-addon` and includes a self-contained Windows x64 helper.

See [helper/README.md](helper/README.md) for the audio and protocol internals and [addon/README.md](addon/README.md) for the NVDA-side configuration and process supervision.

## Debug logging

Enable **Verbose diagnostic logging** in settings when you need helper timing, packet, buffer, authentication, or recording details in `nvda.log`. Leave it off for normal use. **Copy audio diagnostics** gives you a compact report without exposing the encryption password.

## Contributing

Pull requests are welcome. If NVDA Remote Audio Client has been useful to you, open a PR with a fix or feature and I'll review it.

## Credit

[Ednunp/RemSound](https://github.com/Ednunp/RemSound) inspired the application isolation, encrypted transport, quality choices, audio shaping, recording, profiles, and accessible workflow in this add-on. From 0.2.4 it is also a peer: the helper speaks RemSound's own wire protocol, so its Windows, iPhone, Android, and Mac apps can send to and receive from this add-on directly. The RemSound sources are the specification for that protocol, and the key and fingerprint vectors this add-on pins come from RemSound's own cross-port tests.

[haitun001/NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer) provides the relay protocol and server. This repository ships the client add-on and helper.

## License

MIT. See [LICENSE](LICENSE).

## Community and support

Report bugs and request features in [Issues](https://github.com/serrebidev/NVDARemoteAudioClient/issues). For questions, feedback, and release news, join the [SerrebiProjects Telegram group](https://t.me/SerrebiProjects).
