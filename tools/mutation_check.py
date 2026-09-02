#!/usr/bin/env python3
"""Check that the test suite would actually catch a broken build.

A suite that always passes is indistinguishable from a suite that tests nothing,
and the difference only shows the day it should have caught a regression and did
not. This breaks one thing at a time on purpose, runs the suite, and reports any
mutation the suite failed to notice. Each miss is a hole in the tests.

    python tools/mutation_check.py

Every mutation is reverted afterwards, including on failure. The working tree
must be clean first, so an interrupted run can never lose real work: recover with
`git checkout -- .`. Not part of `run-tests.ps1` -- it rebuilds the helper once
per C# mutation and takes a few minutes. Run it when adding tests, or when a test
starts looking suspiciously easy to satisfy.
"""

import io
import os
import subprocess
import sys

REPO_ROOT = os.path.dirname(os.path.abspath(os.path.dirname(__file__)))
HELPER_EXE = os.path.join(
    REPO_ROOT, "helper", "bin", "Release", "net9.0-windows", "NVDARemoteAudioHelper.exe"
)
ADDON_SUITE = [sys.executable, os.path.join(REPO_ROOT, "tools", "selftest_addon.py")]

ADDON = "addon/globalPlugins/remoteAudioClient/__init__.py"

#: (file, exact text to replace, replacement, "py" or "cs", what it breaks).
#: A mutation must be a plausible mistake, not merely a syntax error -- the point
#: is to check the tests, and the compiler already catches what will not build.
MUTATIONS = [
    (ADDON,
     'args.extend(["--password-env", passwordEnvName])',
     'args.extend(["--password", password])',
     "py", "the encryption password reaching the helper's command line"),
    (ADDON,
     '"tailscale": _("Tailscale: low latency"),',
     '"tailscale": _("LAN: lowest latency"),',
     "py", "two settings sharing one spoken label"),
    (ADDON,
     "\tif ip in TAILSCALE_CGNAT_NETWORK:\n\t\treturn _hostnameAddress()\n\treturn address",
     "\treturn address",
     "py", "the tailnet address reported a second time as the LAN address"),
    (ADDON,
     "\t\tif ipaddress.ip_address(address) in TAILSCALE_CGNAT_NETWORK:\n\t\t\treturn address",
     "\t\tif True:\n\t\t\treturn address",
     "py", "an ordinary address reported as the Tailscale one"),
    (ADDON,
     '"port": clampInt(config.get("port"), DEFAULT_CONFIG["port"], 1, 65535),',
     '"port": int(config.get("port", DEFAULT_CONFIG["port"])),',
     "py", "a port that is never clamped or validated"),
    (ADDON,
     '"End-to-end encryption: {0}".format(bool(config.get("password"))),',
     '"End-to-end encryption: {0}".format(config.get("password")),',
     "py", "the password printed into copied diagnostics"),
    ("helper/AudioRingBuffer.cs",
     "public void Clear() => Volatile.Write(ref _head, Volatile.Read(ref _tail));",
     "public void Clear() => DropOldest(BufferedBytes);",
     "cs", "a device change counted as dropped audio"),
    ("helper/AudioPayloadProtocol.cs",
     "\t\tif (peerVersion > Version)",
     "\t\tif (peerVersion > Version + 10)",
     "cs", "a newer publisher going unreported"),
    ("helper/AudioPayloadProtocol.cs",
     "\t\t\t\treturn AudioPayloadDecodeStatus.AuthenticationFailed;\n\t\t\t}\n\t\t}",
     "\t\t\t\treturn AudioPayloadDecodeStatus.Malformed;\n\t\t\t}\n\t\t}",
     "cs", "a wrong password blamed on the network"),
    ("helper/RemoteAudioProtocol.cs",
     "\t\tif (packet.Length < AudioHeaderLength)",
     "\t\tif (packet.Length < HeaderLength)",
     "cs", "a truncated audio packet parsed anyway"),
    ("helper/RemoteAudioProtocol.cs",
     "BinaryPrimitives.WriteUInt64BigEndian(packet.Slice(HeaderLength, 8), sequence);",
     "BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(HeaderLength + 4, 4), (uint)sequence);",
     "cs", "a sequence number truncated to 32 bits"),
    ("helper/PlaybackSink.cs",
     "\t\t_volume = Math.Clamp(receiveVolume, 0, 200) / 100f;",
     "\t\t_volume = receiveVolume / 100f;",
     "cs", "a playback volume that is never clamped"),
    ("helper/PlaybackSink.cs",
     "\t\tRebuildOutputIfStale();\n",
     "\t\t\n",
     "cs", "playback never following the output device"),
    ("helper/HelperOptions.cs",
     'throw new ArgumentException("PCM mode requires --opus-frame-ms 5 so packets stay within the relay MTU.");',
     "{ }",
     "cs", "PCM at a frame size that exceeds the relay's UDP limit"),
]


def build_helper():
    result = subprocess.run(
        ["dotnet", "build", os.path.join(REPO_ROOT, "helper", "NVDARemoteAudioHelper.csproj"),
         "-c", "Release", "-v", "q", "--nologo"],
        capture_output=True, text=True, cwd=REPO_ROOT,
    )
    return result.returncode == 0


def working_tree_is_clean():
    result = subprocess.run(
        ["git", "status", "--porcelain", "--untracked-files=no"],
        capture_output=True, text=True, cwd=REPO_ROOT,
    )
    return result.returncode == 0 and not result.stdout.strip()


def main():
    os.chdir(REPO_ROOT)
    if not working_tree_is_clean():
        print("Refusing to run: commit or stash your changes first.")
        print("This rewrites source files in place, and an interrupted run would")
        print("otherwise be indistinguishable from your own edits.")
        return 2

    if not os.path.exists(HELPER_EXE) and not build_helper():
        print("Could not build the helper.")
        return 2

    caught, missed = [], []
    for path, old, new, kind, description in MUTATIONS:
        original = io.open(path, "rb").read()
        text = original.decode("utf-8")
        eol = "\r\n" if "\r\n" in text else "\n"
        flat = text.replace("\r\n", "\n")
        found = flat.count(old)
        if found != 1:
            # The code moved out from under the mutation. That is not a passing
            # result: this mutation is no longer testing anything.
            missed.append((description, "stale mutation: matched %d times, expected 1" % found))
            continue

        mutated = flat.replace(old, new, 1)
        io.open(path, "wb").write(
            (mutated.replace("\n", eol) if eol == "\r\n" else mutated).encode("utf-8")
        )
        try:
            if kind == "cs":
                if not build_helper():
                    caught.append((description, "rejected by the compiler"))
                    continue
                result = subprocess.run([HELPER_EXE, "--self-test"], capture_output=True, text=True)
            else:
                result = subprocess.run(ADDON_SUITE, capture_output=True, text=True)
            if result.returncode == 0:
                missed.append((description, "the suite passed anyway"))
            else:
                caught.append((description, "the suite failed, as it should"))
        finally:
            io.open(path, "wb").write(original)
        print("  checked: %s" % description)

    build_helper()

    print("")
    print("Caught %d of %d mutations." % (len(caught), len(MUTATIONS)))
    for description, note in caught:
        print("  ok   %s (%s)" % (description, note))
    if missed:
        print("")
        print("%d MUTATION(S) THE SUITE DID NOT CATCH:" % len(missed))
        for description, note in missed:
            print("  MISS %s (%s)" % (description, note))
        print("")
        print("Each of these is a hole in the tests. Add a check that fails when")
        print("the described behaviour is broken, then run this again.")
        return 1
    print("")
    print("Every mutation was caught; the suite has teeth.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
