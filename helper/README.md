# NVDARemoteAudioHelper

Standalone Windows console EXE that does the audio work for the [NVDA Remote Audio Client](../README.md) add-on. Runs as a child process of NVDA. Speaks the [NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer) protocol directly — no NVDA dependency in the helper itself — and, on request, the [RemSound](https://github.com/Ednunp/RemSound) peer-to-peer protocol.

## What it does

- **Publisher**: WASAPI process-loopback capture → Opus or PCM → optional AES-256-GCM payload-v2 encryption → UDP relay. The captured source can also be a microphone instead of playback audio.
- **Subscriber**: UDP relay → payload authentication/decryption → Opus or PCM decode → optional WAV recording → drift-corrected buffer → volume, pan, three-band EQ, and selectable WASAPI playback.
- **Duplex**: on a RemSound connection, capture and playback at once over one UDP socket, so a phone can hear this computer and this computer can hear the phone.
- **Control plane**: TCP JSON handshake on the same port, periodic heartbeats, UDP session registration, and automatic session recovery after relay restarts.
- **Live control**: the add-on writes one-line commands to the helper's standard input to change receive volume or mute, or to ask a RemSound peer to change its own volume, without reconnecting.

The helper logs structured JSON events to stdout, one per line. The add-on parses them to drive NVDA messages.

## Requires

- .NET 9 SDK to build (https://dotnet.microsoft.com/download).
- Windows 10 build **20348 or newer** at runtime, for the WASAPI process-loopback exclusion API. Earlier Windows builds will fail to activate the audio interface.

## Build

From this directory:

```powershell
dotnet build -c Release
```

Or publish a self-contained single-file EXE the way the add-on bundles it:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -o ..\publish\win-x64 `
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```

The repo-root `build.ps1` does the publish + bundle for you.

## Run directly

The helper does not need NVDA. You can drive it from a shell to test the protocol against a server:

```powershell
# Receive
NVDARemoteAudioHelper.exe --role subscriber --host 127.0.0.1 --port 6838 --key MYKEY --opus-frame-ms 5 --prebuffer-ms 15 --output-latency-ms 15 --buffer-ms 120

# Send a generated tone (no capture, no NVDA exclusion)
NVDARemoteAudioHelper.exe --role publisher --host 127.0.0.1 --port 6838 --key MYKEY --test-tone --opus-frame-ms 5

# Send real system audio, excluding NVDA's process tree
NVDARemoteAudioHelper.exe --role publisher --host 127.0.0.1 --port 6838 --key MYKEY --exclude-pid <NVDA_PID> --bitrate 128000 --opus-frame-ms 5

# Send one application (stable process name, without .exe)
NVDARemoteAudioHelper.exe --role publisher --host 127.0.0.1 --port 6838 --key MYKEY --include-process-name foobar2000 --bitrate 128000 --opus-frame-ms 5

# Send a microphone to a RemSound peer (no relay, no server, no room key)
NVDARemoteAudioHelper.exe --transport remsound --role publisher --password-env RS_PW --peers 192.168.1.79 --capture-device-id default --opus-frame-ms 5

# Listen for RemSound peers, on both the default and the microphone
NVDARemoteAudioHelper.exe --transport remsound --role duplex --password-env RS_PW --capture-device-id default --allow-remote-control

# List every RemSound device announcing itself on this network, then exit
NVDARemoteAudioHelper.exe --discover-peers --discover-seconds 4
```

Packet size and subscriber-side jitter buffering are tunable from CLI (the add-on passes these based on the latency profile):

```
--opus-frame-ms <ms>      Opus packet duration: 5, 10, or 20. Default 10.
--disable-fec             Disable Opus in-band forward error correction.
--codec opus|pcm          Opus or uncompressed PCM16 transport.
--password-env <name>     Read the end-to-end password from an environment variable.
--prebuffer-ms <ms>       Startup buffer before playback begins. Default 90.
--output-latency-ms <ms>  WASAPI event-sync output latency. Default 80.
--buffer-ms <ms>          Max playback buffer cap. Default 450.
--output-device-id <id>   Active render endpoint ID; omitted follows the Windows default.
--receive-volume <pct>    Subscriber gain from 0 to 200 percent. Default 100.
--receive-pan <value>     Subscriber pan from -100 to 100.
--bass-db/--mid-db/--treble-db <db>
                           Three-band subscriber EQ from -12 to +12 dB.
--capture-device-id <id>  Send a recording device instead of playback audio ("default"
                          for the Windows default).
--record-folder <path>    Record received audio to a timestamped WAV file.
--list-audio-apps         Emit current audio-session applications as JSON and exit.
--list-output-devices     Emit active render endpoints as JSON and exit.
--list-input-devices      Emit active recording devices as JSON and exit.
--self-test               Test encryption, password rejection, PCM, legacy Opus, and RemSound compatibility.
```

### RemSound options

```
--transport remsound      Peer to peer with the RemSound apps. Always encrypted, so a
                          password is required. Cannot be combined with --host/--key.
--role publisher|subscriber|duplex
                          duplex sends and receives at once; the relay carries one direction.
--peers <list>            Comma-separated host[:port] to send to and accept audio from.
                          A RemSound relay host works here too. Default port 47830.
--peer-names <list>       Comma-separated device names found by discovery.
--device-name <name>      Name other RemSound devices see. Default: the computer name.
--local-port <port>       RemSound audio/heartbeat/control port. Default 47830.
--discovery-port <port>   RemSound discovery port, 0 to turn discovery off. Default 47821.
--allow-remote-control    Let peers that share the password change this computer's volume.
--discover-peers          Listen for RemSound devices and list them as JSON, then exit.
--discover-seconds <n>    How long --discover-peers listens. Default 4.
```

## Files

| File | What's in it |
|---|---|
| `Program.cs` | Entry point, arg parsing, session lifecycle. |
| `HelperOptions.cs` | CLI parsing. |
| `RemoteAudioProtocol.cs` | TCP handshake + heartbeats, UDP packet framing (`RAS1` magic, 22-byte header, 16-byte session id, sequence/timestamp), session registration. |
| `AudioPublisher.cs` | Opus encode loop, configurable 5/10/20 ms packets, test-tone generator. |
| `ProcessLoopbackCapture.cs` | `ActivateAudioInterfaceAsync` against `VAD\Process_Loopback`, `IAudioClient`/`IAudioCaptureClient` interop, include/exclude process-tree wiring. |
| `InputDeviceCapture.cs` | Microphone and other recording-device capture, resampled to the same 48 kHz stereo frames the playback path produces. |
| `AudioDeviceCatalog.cs` | Live Windows audio-session application and render-endpoint discovery. |
| `AudioSubscriber.cs` | Opus decode, in-band FEC recovery, PLC, diagnostic counters. |
| `AudioPayloadProtocol.cs` | Backward-compatible payload-v2 framing, PBKDF2-SHA256, and AES-256-GCM. |
| `ReceivedAudioRecorder.cs` | Timestamped received-audio WAV writer. |
| `HelperSelfTest.cs` | Offline protocol, encryption, and compatibility tests. |
| `PlaybackSink.cs` | WASAPI event-sync output, float ring buffer, prebuffer, underrun fade, trim, and continuous drift resampling. Live volume and mute without reopening the device. |
| `LiveControls.cs` | The one-line command channel on standard input, the receive-volume handlers, and the policy for what a remote command may do here. |
| `RemSoundProtocol.cs` | The RemSound v3 wire format: header, Format packets, heartbeat, 24-bit PCM framing, PBKDF2 key and fingerprint, nonce layout, and sealed remote control. |
| `RemSoundDiscovery.cs` | RemSound's JSON LAN discovery announcements. |
| `RemSoundIdentity.cs` | The one device identity this computer announces, kept per installation so other devices can identify it across restarts. |
| `RemSoundLink.cs` | One UDP socket for audio, heartbeats, relay address checks, and remote control, with peer health tracking. |
| `RemSoundSender.cs` | Opus or 24-bit PCM send, encrypted per packet, with the format re-announced every 250 ms. |
| `RemSoundReceiver.cs` | Per-source session tracking, Opus decode with FEC and PLC, PCM reassembly, password-fingerprint diagnosis. |
| `RemSoundRunner.cs` | Chooses what a sending RemSound helper captures, and runs link, sender, receiver, and live controls together. |
| `RemSoundSelfTestCases.cs` | RemSound compatibility cases, including the pinned cross-port key and fingerprint vectors. |
| `NetworkPriority.cs` | Best-effort qWAVE voice-priority attachment for the UDP socket. |
| `SystemTimerResolution.cs` | Holds a fine Windows timer resolution while the helper is active. |
| `AudioRingBuffer.cs` | Lock-free single-producer/single-consumer audio ring buffer. |
| `WindowsAudioThreadBoost.cs` | MMCSS / thread-priority helper for synchronous audio paths. |
| `JsonLog.cs` | One-line-JSON status events written to stdout. |
| `HResult.cs` | Throws `COMException` on non-zero HRESULTs. |

## Tests

`dotnet run -- --self-test` (or `--self-test` on the built EXE) runs everything
that needs no relay and no network: payload encryption, authentication and
version negotiation, UDP framing and its rejection of malformed and truncated
packets, the playback ring buffer's wrap-around, overflow and underrun paths,
audio shaping and clamping, the frame queue, and command-line parsing. Cases live
in `HelperSelfTestCases.cs` and `RemSoundSelfTestCases.cs`; `HelperSelfTest.cs`
holds the encryption cases and the playback-device test.

The RemSound cases pin the key and fingerprint vectors the iPhone app pins —
PBKDF2-HMAC-SHA256 at 100 000 iterations over RemSound's fixed salts — so a drift
in that derivation fails the build instead of silently making every device on the
network go quiet. They also cover the header and Format packet layouts, the
nonce-tag-ciphertext layout and nonce uniqueness, 24-bit PCM packing and
multi-part frame reassembly, sealed remote control including replay and
staleness rejection, discovery parsing and name cleanup, peer parsing, and live
volume clamping and mute.

The playback-device test opens a real output device, moves playback as a device
change would, and checks audio restarts on the new one. It reports `skipped`
rather than failing where there is no playback device.

`run-tests.ps1` runs all of it on every build. `integration-test.ps1` covers what
this cannot: a real relay, real UDP, and recovery after the relay restarts.

## Wire format

UDP packets:

```
"RAS1" (4) | version=1 (1) | kind (1) | session_id (16)
  └ kind=4 audio: + sequence_be64 (8) + timestamp_ms_be64 (8) + opus_payload
  └ kind=1 register / kind=2 register-ack / kind=3 heartbeat: header only
```

Opus payloads are kept under the server's `udp_audio_payload_max_bytes` (default 1200).

Inside `kind=4`, the end-to-end payload envelope (`AudioPayloadProtocol.cs`), which
the relay forwards without understanding:

```
"RAE2" (4) | version (1) | flags (1) | codec (1) | frame_ms (1) | nonce_base (12)
  └ body: Opus or PCM16, followed by a 16-byte GCM tag when flags bit 0 is set
```

A payload with no `RAE2` magic is a pre-0.2.0 publisher sending bare Opus, and is
still accepted when no encryption password is set.

### Payload versioning

The version byte is versioned **independently of the add-on's release number**.
What the two machines have to agree on is this wire contract, not the version in
`manifest.ini`, and the two move on different schedules.

- Current version: **2**. Oldest accepted: **2** (`AudioPayloadProtocol.CurrentVersion`
  and `.OldestSupportedVersion`).
- Bump it **only for a breaking change**: a redefined header field, a different
  nonce or AAD construction, new framing.
- An **additive** change must not bump it. A receiver ignores header bits it does
  not recognise rather than failing, and that tolerance is the only thing that
  makes the number worth checking.
- The one exception is the **codec byte**. A stream in a codec this build cannot
  decode is unusable however tolerant the parser is, so an unknown codec is
  reported as a newer publisher rather than as corruption.

The audio path is one-way UDP with no peer handshake, so this byte is the only
thing that can tell an out-of-date add-on apart from a wrong password. Both are
reported separately, all the way to the user, and the message names **which
computer** to update — that is the only actionable part, and the user cannot see
the other machine's screen to work it out. A bare drop would be
indistinguishable from a wrong password, a firewall, or the VPN being down.

Three consecutive undecodable packets end the session, so a single packet
mangled in transit never does.

TCP control: one line of JSON `{"role":"publisher"|"subscriber","key":"<key>"}` to handshake. Server returns `{"status":"ok",...}` with `session_id`, `udp_port`, `tcp_heartbeat_interval_ms`, `udp_session_timeout_ms`, `udp_audio_payload_max_bytes`. Then `{"type":"heartbeat"}` lines on the interval the server returned.

## RemSound wire format

The RemSound path is a byte-for-byte port of the parts a peer has to agree on, and
the Windows sources in [Ednunp/RemSound](https://github.com/Ednunp/RemSound)
(`src/RemSound.Core`) are the specification. It shares no framing with the relay
path above.

Every packet starts with a 12-byte little-endian header, on UDP port **47830**:

```
"RMND" (4) | version=1 (1) | type (1) | stream_id_le16 (2) | sequence_le32 (4)
  └ type=1 Format    : 32-byte base payload, +lane and padding, +8-byte password
                       fingerprint, +2-byte capture latency in tenths of a ms
  └ type=2 Audio     : Opus, or a 6-byte PCM sub-header and one part of a frame
  └ type=3 KeepAlive / type=4 Heartbeat : kind (1) + originator tick (8)
  └ type=5 Control   : sealed remote control (see below)
  └ type=10 AddrCheck: the relay's address check
```

Stream ID 0 is coerced to 1, as every other port does, and a session is keyed on
its source address together with this ID. A sender re-announces its Format packet
every 250 ms, so a receiver that joins late or misses one starts playing within a
quarter of a second. A Format packet is the one packet that is not authenticated,
so every field is validated before anything is sized from it; a format with a
password fingerprint this build does not recognise is reported as a password
problem, and a 32-byte format with no fingerprint means the sender predates
encryption.

Audio is always encrypted, and the layout is nonce(12) ‖ tag(16) ‖ ciphertext:

- Key and fingerprint both come from the shared password with PBKDF2-HMAC-SHA256,
  **100 000 iterations**, over RemSound's fixed salts. The count is a cross-port
  contract; RemSound 5.6 raised it briefly and every iPhone went silent, so it must
  never change on its own.
- Nonces are a random 48-bit prefix per stream plus a 48-bit counter, so a nonce
  cannot repeat within a stream by arithmetic and separate streams under the same
  long-lived key stay apart.
- Opus is 48 kHz stereo, restricted low delay, full complexity, 192 kbps nominal.
- PCM is signed 24-bit stereo in 2.5 ms frames (120 samples per channel): a frame
  encrypts to 748 bytes and so fits one datagram, which matters because a PCM frame
  with a part missing is dropped whole rather than played half-complete.

Discovery does not use this framing: it is a JSON announcement carrying an
instance ID, a device name, an audio port, and whether the device can send and
receive, sent every 1.5 seconds to UDP port **47821** by broadcast on every
network and by unicast to every address already known. iOS restricts broadcast, so
the unicast half is what an iPhone relies on, and it is also what crosses
Tailscale. Keys are matched case-sensitively, a device ignores its own instance ID,
and a name is trimmed and length-capped rather than trusted, because it is spoken
aloud and written to logs.

The instance ID is the one piece of state the helper keeps between runs:
`%LOCALAPPDATA%\NVDARemoteAudioHelper\remSoundInstanceId`, written on first use and
read back on every start (`RemSoundIdentity.cs`). RemSound's own ports reroll it
every time, which is why the iPhone app re-identifies a discovered peer by IP
address instead — and so loses the association whenever an address changes.
Keeping one identity per installation means a device that has this computer ticked
keeps seeing the same computer.

Remote control is a `type=5` packet whose payload is sealed with the audio key.
The sealed plaintext is the command, a delta, and the sender's Unix time; the
receiver refuses anything outside a ten-minute window, refuses a nonce it has
already accepted, and refuses a command it cannot authenticate. A device therefore
has to know the password before it can change anything here, and the add-on only
accepts those commands at all when the user has allowed it.

**A peer can never change this computer's Windows volume.** The receive-volume
commands (`VolumeUp`, `VolumeDown`, `MuteToggle`) move the gain applied to the
incoming audio and nothing else. The system-volume commands
(`SystemVolumeUp`, `SystemVolumeDown`, `SystemMuteToggle`) are refused
unconditionally in `RemControlPolicy`, switched on or not, and the helper holds no
code that sets an endpoint volume — so a speaker volume that moves on its own can
never have come from a RemSound peer. Sending those commands outward is still
offered, because that is the user acting deliberately on the other device.

## License

MIT — see the repo root `LICENSE`.
