using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NVDARemoteAudioHelper;

/// <summary>
/// Checks for the RemSound-compatible transport. The PBKDF2 vectors are the same ones
/// the iPhone app pins, generated from the Windows app's parameters: if they ever
/// fail, this add-on can no longer talk to any RemSound device, however right the
/// rest looks.
/// </summary>
internal static class RemSoundSelfTestCases
{
	public static void RunAll()
	{
		TestCrossPortKeyVectors();
		TestHeaderAndFormat();
		TestEncryptionLayout();
		TestPcmFraming();
		TestControlSealing();
		TestDiscoveryParsing();
		TestDeviceIdentity();
		TestPeerSpecs();
		TestOptions();
		TestLiveControls();
	}

	private static void Expect(bool condition, string what)
	{
		if (!condition)
		{
			throw new InvalidOperationException(what);
		}
	}

	private static void TestCrossPortKeyVectors()
	{
		Expect(RemCrypto.Pbkdf2Iterations == 100_000, "The RemSound key iteration count changed; every other RemSound app would go silent.");
		Expect(Convert.ToHexStringLower(RemCrypto.DeriveKey("")) ==
			"e7fe94e96d7cfa6c51ba8e1590f50e37e234e5b0b3e662b24be48a4261f59c18",
			"The RemSound key for an empty password differs from the Android and iPhone apps.");
		Expect(Convert.ToHexStringLower(RemCrypto.DeriveKey("test123")) ==
			"b419d5d5ab025172af8ea4f8923ef9176bf2c0e720e40873d82aa4350f0e87d3",
			"The RemSound key differs from the one the iPhone app derives.");
		Expect(Convert.ToHexStringLower(RemCrypto.DeriveKey("correct horse battery staple")) ==
			"5b105a781f1a3705d9ca53b4cf37014840484f99bb26c29ce22f067f15a12a8d",
			"The RemSound key for a long password differs from the iPhone app.");
		Expect(Convert.ToHexStringLower(RemCrypto.Fingerprint("")) == "7a78e2d810154bf7",
			"The RemSound password fingerprint for an empty password differs.");
		Expect(Convert.ToHexStringLower(RemCrypto.Fingerprint("test123")) == "fb6a9f52926ac190",
			"The RemSound password fingerprint differs from the iPhone app.");
	}

	private static void TestHeaderAndFormat()
	{
		var header = new byte[RemPacket.HeaderSize];
		RemPacket.WriteHeader(header, RemPacketType.Audio, 0, 0xDEADBEEF);
		Expect(Encoding.ASCII.GetString(header, 0, 4) == "RMND", "The RemSound magic is not RMND on the wire.");
		Expect(header[4] == 1, "The RemSound header version is not 1.");
		Expect(RemPacket.TryReadHeader(header, out var type, out var streamId, out var sequence) &&
			type == RemPacketType.Audio && sequence == 0xDEADBEEF, "A RemSound header did not survive a round trip.");
		Expect(streamId == 1, "Stream ID 0 was not coerced to 1, as every RemSound port does.");
		var badVersion = header.ToArray();
		badVersion[4] = 2;
		Expect(!RemPacket.TryReadHeader(badVersion, out _, out _, out _), "A header with another version was accepted.");
		Expect(!RemPacket.TryReadHeader(header.AsSpan(0, 11), out _, out _, out _), "A truncated header was accepted.");

		var fingerprint = RemCrypto.Fingerprint("test123");
		var format = new RemFormat(48000, 2, 16, 1, 4, 192000, (int)RemCodec.Opus, 240, CaptureLatencyMs: 10);
		var payload = new byte[RemPacket.FormatPayloadWithCaptureSize];
		Expect(RemPacket.WriteFormatPayload(payload, format, fingerprint) == 46, "The format payload is not the 46-byte layout.");
		Expect(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(28)) == 240, "The frame size is not a sample count at offset 28.");
		Expect(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(44)) == 100, "Capture latency is not in tenths of a millisecond.");
		Expect(RemPacket.TryReadFormat(payload, out var parsed, out var parsedFingerprint) && parsed == format &&
			parsedFingerprint is not null && parsedFingerprint.SequenceEqual(fingerprint),
			"A RemSound format did not survive a round trip.");

		// Older senders stop at 32 bytes; that means no fingerprint, i.e. "needs to update".
		Expect(RemPacket.TryReadFormat(payload.AsSpan(0, 32), out var legacy, out var noFingerprint) &&
			legacy.FrameSamplesPerChannel == 240 && noFingerprint is null, "A 32-byte format from an older RemSound was misread.");
		Expect(!RemPacket.TryReadFormat(payload.AsSpan(0, 31), out _, out _), "A short format payload was accepted.");

		Expect(format.IsUsable(out _), "A normal Opus format was rejected.");
		Expect(!(format with { Channels = 0 }).IsUsable(out _), "A format with no channels was accepted.");
		Expect(!(format with { FrameSamplesPerChannel = 100_000 }).IsUsable(out _), "An oversized frame was accepted.");
		Expect(!(format with { Codec = 9 }).IsUsable(out _), "An unknown codec was accepted.");
		Expect(!(format with { SampleRate = 44100 }).IsUsable(out _), "Opus at 44.1 kHz was accepted.");
	}

	private static void TestEncryptionLayout()
	{
		var key = RemCrypto.DeriveKey("test123");
		using var gcm = new AesGcm(key, RemCrypto.TagBytes);
		var nonces = new RemNonceSequence();
		var plaintext = Encoding.UTF8.GetBytes("RemSound audio frame");
		var packet = new byte[plaintext.Length + RemCrypto.OverheadBytes];
		RemCrypto.EncryptInto(gcm, nonces, plaintext, packet);

		// The layout every port expects is nonce || tag || ciphertext; open it by hand.
		var opened = new byte[plaintext.Length];
		gcm.Decrypt(packet.AsSpan(0, 12), packet.AsSpan(28), packet.AsSpan(12, 16), opened);
		Expect(opened.SequenceEqual(plaintext), "RemSound packets are not laid out nonce, tag, ciphertext.");

		var output = new byte[64];
		Expect(RemCrypto.TryDecryptInto(gcm, packet, output, out var written) && written == plaintext.Length,
			"A RemSound packet did not decrypt.");
		var tampered = packet.ToArray();
		tampered[^1] ^= 1;
		Expect(!RemCrypto.TryDecryptInto(gcm, tampered, output, out _), "A tampered RemSound packet decrypted.");
		using var wrong = new AesGcm(RemCrypto.DeriveKey("wrong"), RemCrypto.TagBytes);
		Expect(!RemCrypto.TryDecryptInto(wrong, packet, output, out _), "A wrong password decrypted RemSound audio.");

		var second = new byte[packet.Length];
		RemCrypto.EncryptInto(gcm, nonces, plaintext, second);
		Expect(!second.AsSpan(0, 12).SequenceEqual(packet.AsSpan(0, 12)), "Two RemSound packets shared a nonce.");
	}

	private static void TestPcmFraming()
	{
		var samples = new float[] { 0f, 0.5f, -0.5f, 1f, -1f, 0.25f };
		var packed = new byte[samples.Length * 3];
		RemPcmFrame.FloatToInt24(samples, packed);
		var unpacked = new float[samples.Length];
		RemPcmFrame.Int24ToFloat(packed, unpacked);
		Expect(samples.Zip(unpacked).All(pair => Math.Abs(pair.First - pair.Second) < 1e-6), "24-bit PCM did not survive a round trip.");
		Expect(packed[9] == 0xFF && packed[10] == 0xFF && packed[11] == 0x7F, "Full scale is not 0x7FFFFF.");

		var assembler = new RemPcmAssembler();
		var frame = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
		Expect(!assembler.TryAdd(frame.AsSpan(0, 1454), 7, 0, 3, out _), "A frame completed after its first part.");
		Expect(!assembler.TryAdd(frame.AsSpan(1454, 1454), 7, 1, 3, out _), "A frame completed after its second part.");
		Expect(assembler.TryAdd(frame.AsSpan(2908), 7, 2, 3, out var whole) && whole.SequenceEqual(frame),
			"A three-part PCM frame did not reassemble.");
		Expect(!assembler.TryAdd(frame.AsSpan(0, 10), 8, 0, 2, out _) | !assembler.TryAdd(frame.AsSpan(0, 10), 8, 1 + 1 - 1 + 0, 3, out _),
			"A part from a different split was accepted.");
		Expect(!assembler.TryAdd(frame.AsSpan(0, 10), 9, 1, 2, out _), "A frame missing its first part completed.");

		var header = new byte[6];
		RemPcmFrame.WriteSubHeader(header, 42, 1, 2);
		Expect(RemPcmFrame.TryReadSubHeader(header, out var id, out var part, out var parts) && id == 42 && part == 1 && parts == 2,
			"A PCM sub-header did not survive a round trip.");
		header[4] = 2;
		Expect(!RemPcmFrame.TryReadSubHeader(header, out _, out _, out _), "A PCM part index past the part count was accepted.");

		// An encrypted 2.5 ms stereo frame must fit one datagram, or one lost packet costs two frames.
		Expect(RemSoundSender.PcmFrameSamples * 2 * 3 + RemCrypto.OverheadBytes <= RemPacket.MaxAudioPayloadBytes,
			"A PCM frame no longer fits one datagram.");
	}

	private static void TestControlSealing()
	{
		var key = RemCrypto.DeriveKey("test123");
		using var gcm = new AesGcm(key, RemCrypto.TagBytes);
		var now = DateTime.UtcNow;
		var sealedPayload = RemControlSealing.Seal(key, RemControlKind.VolumeDown, -5, new DateTimeOffset(now).ToUnixTimeSeconds());
		Expect(sealedPayload.Length == 38, "A sealed control payload is not RemSound's 38 bytes.");

		var guard = new RemControlGuard();
		Expect(guard.TryAccept(gcm, sealedPayload, now, out var kind, out var delta, out _) &&
			kind == RemControlKind.VolumeDown && delta == -5, "A sealed volume command was refused.");
		Expect(!guard.TryAccept(gcm, sealedPayload, now, out _, out _, out _), "A replayed volume command was accepted.");

		var stale = RemControlSealing.Seal(key, RemControlKind.MuteToggle, 0, new DateTimeOffset(now.AddMinutes(-11)).ToUnixTimeSeconds());
		Expect(!guard.TryAccept(gcm, stale, now, out _, out _, out _), "A volume command eleven minutes old was accepted.");

		using var otherGcm = new AesGcm(RemCrypto.DeriveKey("other"), RemCrypto.TagBytes);
		var fresh = RemControlSealing.Seal(key, RemControlKind.SystemMuteToggle, 0, new DateTimeOffset(now).ToUnixTimeSeconds());
		Expect(!new RemControlGuard().TryAccept(otherGcm, fresh, now, out _, out _, out _),
			"A volume command sealed with another password was accepted.");
		// The old two-byte plaintext form could be forged by anyone on the network.
		Expect(!new RemControlGuard().TryAccept(gcm, new byte[] { 5, 0 }, now, out _, out _, out _),
			"An unsealed volume command was accepted.");
	}

	private static void TestDiscoveryParsing()
	{
		var from = IPAddress.Parse("192.168.1.79");
		var own = Guid.NewGuid().ToString();
		var id = Guid.NewGuid().ToString();
		var announcement = RemSoundDiscovery.BuildAnnouncement(id, "iPhone", 47830, false, true);
		var json = Encoding.UTF8.GetString(announcement);
		Expect(json.Contains("\"InstanceId\"") && json.Contains("\"CanReceive\":true"), "Announcements lost their PascalCase keys.");
		Expect(RemSoundDiscovery.TryParseAnnouncement(announcement, from, own, out var peer) &&
			peer.Name == "iPhone" && peer.CanReceive && !peer.CanSend && peer.Address.Equals(from),
			"A RemSound announcement was not understood.");
		Expect(!RemSoundDiscovery.TryParseAnnouncement(RemSoundDiscovery.BuildAnnouncement(own, "me", 47830, true, true), from, own, out _),
			"This computer's own announcement was listed as a device.");
		Expect(!RemSoundDiscovery.TryParseAnnouncement(RemSoundDiscovery.BuildAnnouncement(id, "x", 99999, true, true), from, own, out _),
			"An announcement with an impossible port was accepted.");
		Expect(!RemSoundDiscovery.TryParseAnnouncement(Encoding.UTF8.GetBytes("not json"), from, own, out _),
			"A garbled announcement was accepted.");
		Expect(!RemSoundDiscovery.TryParseAnnouncement(Encoding.UTF8.GetBytes("{\"instanceid\":\"" + id + "\",\"audioport\":1}"), from, own, out _),
			"Lower-case keys were accepted; RemSound matches them case-sensitively.");
		var noisy = RemSoundDiscovery.BuildAnnouncement(id, new string('a', 300) + "\n", 47830, true, false);
		Expect(RemSoundDiscovery.TryParseAnnouncement(noisy, from, own, out var trimmed) && trimmed.Name.Length == 128 &&
			!trimmed.Name.Contains('\n'), "A long or control-character name was not cleaned up.");
	}

	private static void TestDeviceIdentity()
	{
		var folder = Path.Combine(Path.GetTempPath(), "nvda-remote-audio-identity-" + Guid.NewGuid().ToString("N"));
		try
		{
			// One identity per installation. A phone that has ticked this computer
			// must see the same device after a restart, or the user has to find and
			// tick it again, which is exactly what re-rolling the ID on every start
			// forces every other RemSound port to live with.
			var first = RemSoundIdentity.Resolve(null, folder);
			var second = RemSoundIdentity.Resolve(null, folder);
			Expect(first == second, "The RemSound device identity changed between runs.");
			Expect(RemSoundIdentity.Normalize(first) == first, "The remembered identity is not a usable GUID.");

			var elsewhere = RemSoundIdentity.Resolve(null, Path.Combine(folder, "other-install"));
			Expect(elsewhere != first, "Two installations were given the same RemSound device identity.");

			// A caller with its own reason to pin an identity still wins.
			var pinned = Guid.NewGuid().ToString("D");
			Expect(RemSoundIdentity.Resolve(pinned, folder) == pinned, "An explicit RemSound identity was ignored.");

			// Anything a peer could not parse must never be announced.
			Expect(RemSoundIdentity.Normalize("not a guid") is null, "A malformed RemSound identity was accepted.");
			Expect(RemSoundIdentity.Normalize("") is null, "An empty RemSound identity was accepted.");
			Expect(RemSoundIdentity.Normalize(null) is null, "A missing RemSound identity was accepted.");
			Expect(RemSoundIdentity.Normalize(Guid.Empty.ToString()) is null, "The empty GUID was accepted as an identity.");

			// A damaged or hand-edited identity file is replaced rather than announced.
			File.WriteAllText(Path.Combine(folder, "remSoundInstanceId"), "garbage");
			Expect(RemSoundIdentity.Normalize(RemSoundIdentity.Resolve(null, folder)) is not null,
				"A damaged identity file was not recovered from.");
		}
		finally
		{
			try
			{
				Directory.Delete(folder, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

	private static void TestPeerSpecs()
	{
		Expect(RemPeerSpec.TryParse("192.168.1.20", out var plain) && plain.Port == 47830, "A bare address did not default to port 47830.");
		Expect(RemPeerSpec.TryParse("relay.example.com:5000", out var ported) && ported.Host == "relay.example.com" && ported.Port == 5000,
			"A host with a port was misread.");
		Expect(!RemPeerSpec.TryParse("pc:0", out _), "Port 0 was accepted.");
		Expect(!RemPeerSpec.TryParse("bad host", out _), "A host with a space was accepted.");
		Expect(!RemPeerSpec.TryParse("", out _), "An empty peer was accepted.");
	}

	private static void TestOptions()
	{
		const string variable = "NVDA_REMOTE_AUDIO_SELFTEST_REMSOUND";
		Environment.SetEnvironmentVariable(variable, "plexbox");
		try
		{
			var options = HelperOptions.Parse([
				"--transport", "remsound", "--role", "duplex", "--password-env", variable,
				"--peers", "192.168.1.20, relay.example.com:5000", "--peer-names", "iPhone,Pixel 8",
				"--capture-device-id", "default", "--allow-remote-control",
			]);
			Expect(options.Transport == AudioTransport.RemSound && options.Role == ConnectionRole.Duplex, "RemSound duplex was misparsed.");
			Expect(options.Peers.Count == 2 && options.Peers[1].Port == 5000, "RemSound peers were misparsed.");
			Expect(options.PeerNames.SequenceEqual(["iPhone", "Pixel 8"]), "RemSound device names were misparsed.");
			Expect(options.AllowRemoteControl && options.CaptureDeviceId == "default", "RemSound switches were misparsed.");
			Expect(options.LocalPort == 47830 && options.DiscoveryPort == 47821, "RemSound's default ports changed.");

			// A subscriber needs nothing but the password: a phone can find it by itself.
			var listener = HelperOptions.Parse(["--transport", "remsound", "--role", "subscriber", "--password-env", variable]);
			Expect(listener.Peers.Count == 0, "A bare RemSound receiver was refused.");
			ExpectRejected(["--transport", "remsound", "--role", "subscriber", "--password-env", variable, "--peers", "a b"],
				"a RemSound peer with a space in it");
		}
		finally
		{
			Environment.SetEnvironmentVariable(variable, null);
		}
		// RemSound has no unencrypted mode.
		ExpectRejected(["--transport", "remsound", "--role", "subscriber"], "a RemSound connection without a password");
		ExpectRejected(["--role", "duplex", "--host", "pc", "--key", "room", "--test-tone"], "sending and receiving through the one-way relay");
		ExpectRejected(["--transport", "carrier-pigeon", "--role", "subscriber", "--host", "pc", "--key", "room"], "an unknown transport");
		Expect(HelperOptions.Parse(["--list-input-devices"]).ListInputDevices, "--list-input-devices was not recognised.");
		Expect(HelperOptions.Parse(["--discover-peers", "--discover-seconds", "2"]).DiscoverSeconds == 2, "--discover-peers was misparsed.");
	}

	private static void TestLiveControls()
	{
		var shaper = new AudioShaper(48000, 2, 100, 0, 0, 0, 0);
		shaper.Volume = 500;
		Expect(shaper.Volume == 200, "A live volume change was not clamped to 200 percent.");
		shaper.Volume = -3;
		Expect(shaper.Volume == 0, "A live volume change went below zero.");
		shaper.Volume = 100;
		shaper.Muted = true;
		var samples = Enumerable.Repeat(0.5f, 16).ToArray();
		shaper.Process(samples);
		Expect(samples.All(sample => sample == 0f), "Muted received audio still played.");
		shaper.Muted = false;
		samples = Enumerable.Repeat(0.5f, 16).ToArray();
		shaper.Process(samples);
		Expect(samples.Any(sample => sample != 0f), "Unmuting did not bring the audio back.");

		Expect(LiveControls.ParseSwitch("toggle", false) == true && LiveControls.ParseSwitch("off", true) == false &&
			LiveControls.ParseSwitch("sideways", true) is null, "Mute switches were misread.");
		Expect(LiveControls.TryParseControlKind("system-mute", out var kind) && kind == RemControlKind.SystemMuteToggle,
			"A remote-control command name was misread.");
		Expect(!LiveControls.TryParseControlKind("format-disk", out _), "An unknown remote-control command was accepted.");

		var seen = "";
		using (LiveControls.Register((command, argument) =>
		{
			seen = command + "=" + argument;
			return true;
		}))
		{
			Expect(LiveControls.Dispatch("!volume-step -5") && seen == "volume-step=-5", "A live command was not dispatched.");
		}
		Expect(!LiveControls.Dispatch("!volume 50"), "A live command reached a handler that was removed.");
	}

	private static void ExpectRejected(string[] args, string what)
	{
		try
		{
			HelperOptions.Parse(args);
		}
		catch (ArgumentException)
		{
			return;
		}
		throw new InvalidOperationException($"The helper accepted {what}.");
	}
}
