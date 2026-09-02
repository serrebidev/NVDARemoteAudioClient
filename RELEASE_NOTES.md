# NVDA Remote Audio Client release notes

## 0.2.3

### Playback follows the output device

- Receiving playback set to follow the Windows default now follows it for the whole session, not only at connect time.
- Unplugging headphones, a Bluetooth headset dropping, or changing the Windows output moves playback to the current device instead of leaving it rendering into the old one.
- A pinned playback device is re-opened when it is unplugged and returns.
- Buffered audio and the drift estimate are discarded across the switch, because both belonged to the previous device's clock.
- Emptying the buffer for that switch is no longer counted as dropped audio, so a healthy device change stops reading as buffer overflow in diagnostics.
- Adds `endpoint_rebuilds` to the receiver's diagnostics.

### Version mismatches say which computer to update

- The end-to-end payload envelope carries a version that is checked in both directions, so a receiver that cannot read the sender's audio now says whether this computer or the sending computer is the one to update.
- A wrong encryption password, audio damaged on the network, and a version mismatch are reported as three different problems rather than one message mentioning all of them.
- A publisher older than 0.2.0 reaching a receiver that requires encryption is named as such, rather than only reported as unencrypted audio.
- Three consecutive undecodable packets end a session, so a single packet damaged in transit does not.
- The payload version is documented in `helper/README.md` and reported in the helper self-test and receiver diagnostics.

### This computer's address

- Adds **This computer's address for the other computer** to the Tools menu and to Input Gestures. It speaks and copies this machine's Tailscale address, local network address, computer name, and port.
- The Tailscale address is detected by asking Windows which local address would reach the tailnet, with no Tailscale command line involved and nothing transmitted.
- Both addresses are added to copied diagnostics.

### Testing

- Adds `tools/selftest_addon.py`: 168 checks over the add-on's own logic with the NVDA modules stubbed out. Configuration normalization and round-trip, damaged and hand-edited configuration files, key validation, latency and quality resolution, startup-role selection, address detection, spoken labels, helper-event routing, and the arguments the helper is actually launched with. No NVDA, no relay, and no helper binary; `run-tests.ps1` runs it.
- Two of those checks guard secrets directly: the encryption password must never appear in the helper's command line, where any process on the machine could read it, and must never appear in copied diagnostics, which get pasted into bug reports.
- Adds `helper/HelperSelfTestCases.cs`, covering UDP framing and its rejection of malformed and truncated packets, the playback ring buffer's wrap-around, overflow and underrun paths, audio shaping and its clamping, the frame queue, command-line parsing, and payload edge cases including nonce uniqueness within and across sessions.
- The helper self-test also covers payload version negotiation, unknown codecs, truncated envelopes, and the difference between a version mismatch and a password mismatch; and it opens a real playback device, moves playback as a device change would, and checks audio restarts on the new one. That reports `skipped` where there is no playback device.
- Adds `tools/mutation_check.py`, which breaks one thing at a time on purpose and reports anything the suite fails to notice. All 14 mutations are currently caught. It found three real holes in the new tests, which are now closed.
- The relay integration test now checks that a wrong password is not reported as an add-on version problem.

## 0.2.2

### Safe add-on reloads

- Fixes NVDA terminating when Reload Add-ons is invoked from the Tools menu or its assigned keyboard gesture.
- Removes the stale Tools-menu item after the active event returns while retaining its unsafe wx wrapper until NVDA exits.
- Preserves normal Remote Audio timer, helper-process, settings-panel, and NVDA Remote script cleanup during reload.

## 0.2.1

### Relay restart recovery

- Automatically tears down and recreates capture or playback after the relay server restarts or a heartbeat transport fails.
- Prevents a disconnected publisher from remaining alive without a working control session.
- Adds the negotiated TCP heartbeat and UDP timeout to connection diagnostics.
- Extends the real-relay integration test past the control idle timeout and verifies recovery after forcibly restarting the relay.
- Tested with NVDA 2026.2 beta 7 in addition to the automated helper and add-on checks.

## 0.2.0

### Secure payload-v2 transport

- Optional end-to-end AES-256-GCM encryption with PBKDF2-SHA256 key derivation.
- The password is passed to the helper through an environment variable and never sent to the relay.
- Clear rejection of wrong passwords, unencrypted publishers when encryption is required, and damaged packets.
- Backward-compatible reception of legacy Opus streams when no password is configured.

### Audio routing and quality

- Send all system audio except NVDA, or isolate one running application.
- Choose adaptive Opus, 5 ms live Opus, broadcast-quality Opus, or uncompressed PCM16.
- Select the receive playback endpoint and set gain from 0 to 200 percent.
- Pan the received stream and apply bass, midrange, and treble EQ from -12 to +12 dB.

### Recording, profiles, and diagnostics

- Record received audio to timestamped 48 kHz stereo WAV files.
- Toggle recording and open its folder from the Tools menu.
- Save, load, and delete named connection profiles.
- Run protocol and encryption self-tests from the Tools menu.
- Expanded copied diagnostics for security, codec, shaping, recording, and profile state.

### Compatibility

- Requires NVDA 2025.1 or newer and Windows 10 build 20348 or newer.
- Uses the existing NVDARemoteAudioServer 0.5 relay; no relay protocol upgrade is required because payload v2 is opaque to the server.
- Both endpoints must run 0.2.0 to use encryption or PCM. Leave the password empty for legacy Opus compatibility during a staged upgrade.
