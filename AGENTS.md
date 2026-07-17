@C:\Users\admin\.codex\RTK.md

# AGENTS.md

## Project

NVDA Remote Audio Client is a Windows NVDA add-on with two runtime parts:

- `addon/` contains the Python global plugin, settings UI, menus, process supervision, documentation, and audio-server installer.
- `helper/` contains the self-contained .NET audio client for capture, playback, codecs, encryption, recording, networking, and diagnostics.

The project uses `NVDARemoteAudioServer` as an opaque relay. Keep the existing server handshake and UDP framing backward compatible unless a release explicitly coordinates a server change.

## Accessibility

- Start every assistant response with a short Markdown H2 heading so NVDA users can navigate replies by heading.
- Give every new wx control a useful visible label and keyboard-reachable position.
- Announce errors and explicit actions through `ui.message`; routine status must respect `announceStatus`.
- Add user actions to the NVDA Input Gestures category when they make sense without a menu.
- Keep local gestures registered with NVDA Remote so they are not forwarded to the controlled computer.
- Never rely on color, pointer interaction, or unlabeled icon-only controls.

## Source conventions

- Preserve tabs in the existing Python and C# source files.
- Keep audio and network work out of the NVDA process; the Python plugin should supervise the helper rather than process audio itself.
- Pass secrets to the helper through environment variables, never visible command-line arguments or diagnostics.
- Preserve legacy unwrapped Opus reception when no encryption password is configured.
- Keep payloads within the relay-advertised UDP limit. PCM currently requires 5 ms frames.
- Do not package generated files, bytecode, logs, symbols, local configuration, or nested archives.
- Update `README.md`, `addon/readme.html`, the component READMEs, `RELEASE_NOTES.md`, and `addon/manifest.ini` when user-visible behavior or compatibility changes.

## Validation

Run these from the repository root before committing:

```powershell
.\run-tests.ps1
.\integration-test.ps1
.\build.ps1
git diff --check
```

`run-tests.ps1` builds and publishes the helper, exercises protocol/encryption/audio-shaping self-tests, checks live audio discovery and option validation, compiles the Python modules, and validates the manifest and documentation.

`integration-test.ps1` requires an installed `C:\NVDARemoteAudioServer\NVDARemoteAudioServer.exe`. It automatically relaunches under PowerShell 7 when invoked from Windows PowerShell 5.1 and uses isolated test ports. It must pass encrypted Opus, encrypted PCM, WAV recording, and wrong-password rejection.

`build.ps1` reruns the source checks and creates `dist\remoteAudioClient-X.Y.Z.nvda-addon`. Inspect the archive before release; its root must contain `manifest.ini`, `readme.html`, `globalPlugins/`, and `bin/`.

## Installation checks

- Preserve `%APPDATA%\nvda\remoteAudioClient.json` when updating a local installation.
- Stop the running helper before replacing its executable.
- Prefer the NVDA add-on installer for user-facing upgrades; direct deployment is for development only.
- Verify the installed relay against the latest upstream Windows release before replacing it. Do not restart an identical current server just to change its timestamp.
- After installation, restart NVDA or reload plugins and confirm the installed manifest version and helper self-test.

## Release checklist

1. Confirm the worktree contains only intended changes.
2. Run the full validation sequence above before committing.
3. Commit the reviewed source and documentation.
4. Push the branch and create the matching `vX.Y.Z` GitHub release.
5. Attach the `.nvda-addon` from `dist/` and use `RELEASE_NOTES.md` as the release notes.
6. Verify the release asset and tag from GitHub after upload.
