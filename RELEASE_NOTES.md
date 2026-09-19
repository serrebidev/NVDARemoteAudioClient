# NVDA Remote Audio Client release notes

## 0.2.9

### A RemSound device can no longer change this computer's Windows volume

- Reported as "it turned up the volume when it did RemSound". There was a path that could do exactly that: with **Let RemSound devices that share the password change this computer's volume** ticked, an authenticated `SystemVolumeUp` from a peer called `SystemVolume.Apply`, which stepped the Windows master volume up through `AudioEndpointVolume`. The switch was on, so nothing stopped it.
- That path is gone. The helper no longer contains any code that sets an endpoint volume — no `VolumeStepUp`, no `VolumeStepDown`, no `Mute` write — so a speaker volume that moves on its own cannot have come from a RemSound peer, whatever the settings say.
- Incoming system-volume commands are refused outright, by a rule rather than by a setting: `RemControlPolicy` allows only the receive-volume commands, and only when the user has allowed remote control at all. A refusal is logged with its reason, so it is not silent.
- The setting itself now means what its name suggested: **Let RemSound devices that share the password change the volume of the audio they send here**. That moves the gain on the incoming stream and nothing else. The stored key is still `allowRemoteControl`, so existing settings and profiles keep working.
- **Sending** those commands outward is untouched: this computer can still raise, lower, or mute the other device's volume and its Windows volume, from the gestures in the NVDA Remote Audio category. That is the user acting deliberately on the device in front of them.

### A note on what was confirmed

The defect was found by reading the code path, not reproduced from a log: no volume event appears in any recorded run, and the add-on does not log the peer that asks. What is certain is that the capability existed, that the switch enabling it was on, and that it is now impossible.

### Testing

- Adds `TestControlPolicy` to the helper self-test: every system-volume kind is recognised as one and refused even when remote control is allowed, every receive-volume kind is refused when it is not, and the two sets are never confused.
- `tools/mutation_check.py` grows to 32 mutations, all caught, including one that re-opens the Windows volume path.
- Verified after the change that no endpoint-volume API remains anywhere in the helper.

## 0.2.8

### RemSound peer to peer is now the default connection type

- **Connection type** in settings still offers both, but a fresh install now starts on **RemSound peer to peer**, and it leads the list. RemSound is the connection that reaches a phone, a Mac, or a PC, over one shared password and no server; the relay is the specialist choice now, for two Windows computers where the audio server is already installed.
- **A configuration saved before the connection type existed is left on the audio server.** Those users are already set up on the relay, and moving them to RemSound would leave them unable to connect at all, because RemSound requires an encryption password. The new default applies to a config that has not been written yet, not to one that predates the choice. Configs with an explicit choice are honoured either way.
- An unrecognised connection type falls back to the default rather than being silently kept.

### What this does not change

- The relay path itself: same payload v2, same ports, same room-name behaviour, same latency profiles.
- The helper's own `--transport` command line default is still the relay, because the add-on passes `--transport remsound` explicitly when it wants RemSound and nothing otherwise. Changing that would break the relay path for anyone running an older helper with a newer add-on.
- Existing RemSound installations: an explicit `transport` in the config is never overridden.

### Testing

- `tools/selftest_addon.py`: 268 → 274 checks. New checks cover the RemSound default, the default being first in the settings list, an unknown connection type falling back, a pre-connection-type config staying on the relay with its relay settings intact, and a config-less install getting RemSound.
- The relay-path tests that previously relied on the old default now state `transport: nvda` explicitly, so they test the relay rather than whatever the default happens to be.
- `tools/mutation_check.py` grows to 31 mutations, all caught, including one for each new behaviour: a default that never changed, and an upgrade that silently moves a relay setup onto RemSound.

## 0.2.7

### Choosing a RemSound device now says how to start it

- Choosing a device from **Find RemSound devices on this network** starts no connection by itself, and a RemSound connection that is not running is silent. On the RemSound phone app, ticking a peer *is* how you start listening, so anyone arriving from that app hears "Chosen: iPhone", waits for audio, and concludes the choice did nothing. This was reported exactly that way from a real installation: the device chosen, the password correct, and no connection ever attempted.
- The message now continues: that nothing is connected yet, and which menu item starts it. When audio is already running the reminder is dropped, because reconnecting is what a device change is for and the user already knows the path.

### Testing

- `tools/selftest_addon.py` covers it both ways: the start hint is spoken when nothing is running, and is absent when a connection is already up.
- `tools/mutation_check.py` grows to 29 mutations, all caught.

## 0.2.6

### Choosing a RemSound device now says when a password is still needed

- RemSound always encrypts, so it can never connect without an encryption password. The settings panel already refuses to save a RemSound connection without one — but **Find RemSound devices on this network** is the one path that selects the connection type itself, and it performed no such check. Ticking a phone there switched the connection to RemSound, said so, and left the password empty, so every later attempt to connect was refused.
- Choosing a device now says, in that same spoken message, that RemSound also needs an encryption password and where to set it. The reminder appears only when a password really is missing: a RemSound setup that has one is silent, and the relay path never mentions a password at all.
- Found on a real installation, which is the worst way to find it: an add-on at the RemSound connection type, a phone chosen, no password, and the same "RemSound connections need an encryption password" on every start with nothing earlier tying the two together.

### Testing

- `tools/selftest_addon.py` covers the reminder in all three cases: present for a RemSound setup with no password, absent once one is set, and never on the relay path.
- `tools/mutation_check.py` grows to 28 mutations, all caught.
- Re-verified live against the RemSound iPhone app, including the path the add-on actually uses when you tick a discovered device: resolving the phone by name (`--peer-names iPhone`) brought back 24.3 seconds of the phone's microphone with every 100 ms window audible and zero authentication failures.

## 0.2.5

### This computer keeps one RemSound identity

- The helper announced a brand-new discovery instance ID on every start, so no other RemSound device could identify it across restarts. The iPhone app documents what that forces it to do, in its own source: a discovered peer is re-identified by IP address, because "the address is the only stable key". Everything the user had set up for this computer was therefore tied to an address that can change.
- The identity is now kept once per installation in `%LOCALAPPDATA%\NVDARemoteAudioHelper\remSoundInstanceId` and announced on every run, so a device that has this computer ticked keeps seeing the same computer — including after a restart, and including when this computer's address changes.
- A damaged or hand-edited identity file is replaced rather than announced. A read-only profile or a full disk falls back to a fresh identity per run instead of breaking audio, which is what the other RemSound ports do anyway. An explicit identity still wins where a caller has one to pin.
- No wire-format change: packet layout, password handling, ports, and codecs are untouched, so this does not affect any device already set up.

### Verified against the RemSound iPhone app

- Two-way audio with a real iPhone running RemSound was exercised over the LAN, in both codecs, sending a tone to the phone and recording the phone's microphone here.
- Opus: 48.4 seconds recorded, every 100 ms window audible, zero authentication failures, zero unrecovered gaps, zero send errors, 2–3 ms round trip.
- PCM: 39.4 seconds recorded, zero authentication failures, zero unrecovered gaps. The 24-bit multi-part frame reassembly had only ever been covered by self-tests; it is now confirmed against a different implementation on the other end.
- The one thing the phone withheld until its own switch was set was its microphone: RemSound on iOS does not send to a peer until that peer is ticked **and** sending is switched on. Both are on the phone, and both are what the app's own per-peer status text names when they are not.

### Testing

- Adds `RemSoundIdentity.cs` cases to the helper self-test: one identity per installation, distinct identities for distinct installations, an explicit identity winning, and malformed, empty, missing, and empty-GUID values all refused rather than announced.
- `tools/mutation_check.py` grows to 27 mutations, all caught.

## 0.2.4

### A second connection type: RemSound, peer to peer

- **Connection type** in settings now chooses between **NVDA Remote Audio server** (the existing relay, a room name on port 6838) and **RemSound peer to peer**. The audio-server path is unchanged and remains the default, so existing profiles keep working exactly as before.
- RemSound connections speak the wire protocol of the [RemSound](https://github.com/Ednunp/RemSound) apps: the Windows sender, its send-only service, the iPhone and Mac app, and the Android receiver. A Windows computer running this add-on can send audio to a phone, receive a phone's microphone, or both.
- RemSound always encrypts, so a connection type of RemSound requires an encryption password; the settings panel refuses to save one without it. The key is derived the same way every RemSound app derives it (PBKDF2-HMAC-SHA256, 100 000 iterations, RemSound's fixed salts), and the add-on's self-test pins the same key and fingerprint vectors the iPhone app pins. If the derivation ever drifted, this add-on could no longer talk to any RemSound device.
- A sender that disagrees about the password is told apart from one that needs updating: the Format packet carries a password fingerprint, so a mismatch now says so instead of leaving the user with silence.
- Packets are laid out exactly as RemSound lays them out — nonce, tag, ciphertext — with a fresh 48-bit nonce prefix per stream and a counter, so a nonce cannot repeat within a stream.

### Sending and receiving at the same time

- **Send and receive at the same time (RemSound)** is a new startup action, Tools-menu item, and unbound gesture. Two-way audio is only offered on a RemSound connection, because the relay server carries audio in one direction; choosing it over the relay says so rather than failing later.
- The helper grows a microphone capture source, so a RemSound connection can send the Windows default recording device or a named one instead of the system mix. The same list of recording devices feeds the **Audio to send** choice beside the existing application list.
- A duplex connection sends with `--bitrate` and receives with the usual jitter buffer at once, from one UDP socket.

### Finding devices, and naming them

- **Find RemSound devices on this network** listens for RemSound's discovery announcements and lists what answered, with each device's name, what it can do (sends, receives, or both), and every address it answered on. Devices are grouped by name, because a phone on Wi-Fi and Tailscale at once, or a PC with several adapters, announces once per address and the user is choosing a device, not an adapter.
- Devices chosen from that list are stored by name and matched as they appear, so a phone that changes address keeps working. Addresses can also be typed for anything discovery cannot reach, such as a Tailscale name or a RemSound relay.
- **Name other RemSound devices see for this computer** overrides the computer name, which is what a screen reader user hears on the other device when choosing.

### Volume and remote control without reconnecting

- The helper now reads live commands from its standard input, so a volume change takes effect immediately instead of costing a disconnect and reconnect. A command is one line starting with `!`; any other byte, or the pipe closing, still means shut down, so the old contract is intact.
- New unbound gestures under the **NVDA Remote Audio** category: raise, lower, and mute received audio, and raise, lower, and mute the other device's RemSound volume or Windows volume. Nothing is bound to a key, and every command is also reachable from the Tools menu.
- Lowering the receive volume while nothing is playing now changes the saved value for the next connection instead of doing nothing.
- **Let RemSound devices that share the password change this computer's volume** is off by default and off unless asked for. Remote-control commands are sealed with the audio key, refused when they are more than ten minutes old, and refused when replayed, so a command cannot be forged by anyone who does not know the password.
- Volume the other device changes, and Windows volume the other device changes, are spoken and recorded differently, because only one of them belongs in this add-on's saved settings.

### Testing

- Adds `RemSoundSelfTestCases.cs` to the helper self-test: the pinned cross-port key and fingerprint vectors, the header and Format layouts, the nonce-tag-ciphertext layout and nonce uniqueness, 24-bit PCM packing and multi-part reassembly, sealed remote control including replay and staleness, discovery parsing and name cleanup, peer parsing, the new command-line options, and live volume clamping and mute.
- `tools/selftest_addon.py` grows from 171 to 263 checks, covering RemSound address and device-name parsing, the reason a connection cannot start, the exact command line and environment a RemSound connection launches with, live volume events from both ends, the `!` command marker, and device grouping for the chooser.
- `tools/mutation_check.py` grows to 26 mutations, including eleven for the RemSound transport. All 26 are currently caught.
- The iPhone interop path was exercised live: a two-way session against a real iPhone running RemSound carried audio both ways with no decryption failures.

### Documentation

- The root README, the bundled `readme.html`, and `helper/README.md` document the RemSound connection type, the duplex role, microphone sending, live volume, remote control, and the RemSound wire format.
- `addon/README.md` documents the new configuration keys and shows a RemSound helper invocation beside the existing relay one.

### Compatibility

- RemSound connections need a password on both devices and the same port (47830 by default; discovery uses 47821).
- The relay path, its payload-v2 encryption, its latency profiles, and its profiles are untouched. Leaving the connection type alone behaves exactly as 0.2.3 did.

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
