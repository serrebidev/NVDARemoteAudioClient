# NVDA Remote Audio Client release notes

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
