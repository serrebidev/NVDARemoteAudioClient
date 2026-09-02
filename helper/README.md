# NVDARemoteAudioHelper

Standalone Windows console EXE that does the audio work for the [NVDA Remote Audio Client](../README.md) add-on. Runs as a child process of NVDA. Speaks the [NVDARemoteAudioServer](https://github.com/haitun001/NVDARemoteAudioServer) protocol directly — no NVDA dependency in the helper itself.

## What it does

- **Publisher**: WASAPI process-loopback capture → Opus or PCM → optional AES-256-GCM payload-v2 encryption → UDP relay.
- **Subscriber**: UDP relay → payload authentication/decryption → Opus or PCM decode → optional WAV recording → drift-corrected buffer → volume, pan, three-band EQ, and selectable WASAPI playback.
- **Control plane**: TCP JSON handshake on the same port, periodic heartbeats, UDP session registration, and automatic session recovery after relay restarts.

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
--record-folder <path>    Record received audio to a timestamped WAV file.
--list-audio-apps         Emit current audio-session applications as JSON and exit.
--list-output-devices     Emit active render endpoints as JSON and exit.
--self-test               Test encryption, password rejection, PCM, and legacy Opus.
```

## Files

| File | What's in it |
|---|---|
| `Program.cs` | Entry point, arg parsing, session lifecycle. |
| `HelperOptions.cs` | CLI parsing. |
| `RemoteAudioProtocol.cs` | TCP handshake + heartbeats, UDP packet framing (`RAS1` magic, 22-byte header, 16-byte session id, sequence/timestamp), session registration. |
| `AudioPublisher.cs` | Opus encode loop, configurable 5/10/20 ms packets, test-tone generator. |
| `ProcessLoopbackCapture.cs` | `ActivateAudioInterfaceAsync` against `VAD\Process_Loopback`, `IAudioClient`/`IAudioCaptureClient` interop, include/exclude process-tree wiring. |
| `AudioDeviceCatalog.cs` | Live Windows audio-session application and render-endpoint discovery. |
| `AudioSubscriber.cs` | Opus decode, in-band FEC recovery, PLC, diagnostic counters. |
| `AudioPayloadProtocol.cs` | Backward-compatible payload-v2 framing, PBKDF2-SHA256, and AES-256-GCM. |
| `ReceivedAudioRecorder.cs` | Timestamped received-audio WAV writer. |
| `HelperSelfTest.cs` | Offline protocol, encryption, and compatibility tests. |
| `PlaybackSink.cs` | WASAPI event-sync output, float ring buffer, prebuffer, underrun fade, trim, and continuous drift resampling. |
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
in `HelperSelfTestCases.cs`; `HelperSelfTest.cs` holds the encryption cases and
the playback-device test.

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

## License

MIT — see the repo root `LICENSE`.
