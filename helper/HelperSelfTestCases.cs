using System.Buffers.Binary;

namespace NVDARemoteAudioHelper;

/// <summary>
/// The parts of the helper that can be checked without a relay, a network, or an
/// audio device: UDP framing, the playback ring buffer, audio shaping, the frame
/// queue, and command-line parsing. Run from <see cref="HelperSelfTest"/>, so
/// `--self-test` covers them and `run-tests.ps1` runs them on every build.
/// </summary>
internal static class HelperSelfTestCases
{
	public static void RunAll()
	{
		TestUdpFraming();
		TestRingBuffer();
		TestAudioShaping();
		TestFrameQueue();
		TestOptionParsing();
		TestPayloadEdgeCases();
	}

	private static void Fail(string what) => throw new InvalidOperationException(what);

	private static void Expect(bool condition, string what)
	{
		if (!condition)
		{
			Fail(what);
		}
	}

	/// <summary>
	/// Rejecting a malformed packet matters more than accepting a good one: these
	/// arrive from the network, from any host that can reach the relay port.
	/// </summary>
	private static void TestUdpFraming()
	{
		var sessionId = Enumerable.Range(0, 16).Select(i => (byte)(i * 7)).ToArray();
		var payload = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();

		var audio = UdpPacket.CreateAudio(sessionId, 0xDEADBEEFCAFEUL, 1234567890UL, payload);
		Expect(UdpPacket.TryParse(audio, out var parsedAudio), "A well-formed audio packet was rejected.");
		Expect(parsedAudio.Kind == UdpPacketKind.AudioData, "An audio packet parsed as the wrong kind.");
		Expect(parsedAudio.SessionId.SequenceEqual(sessionId), "The session ID did not survive framing.");
		Expect(parsedAudio.Sequence == 0xDEADBEEFCAFEUL, "The sequence number did not survive framing.");
		Expect(parsedAudio.TimestampMs == 1234567890UL, "The timestamp did not survive framing.");
		Expect(parsedAudio.Payload.SequenceEqual(payload), "The payload did not survive framing.");

		// A sequence at the top of the range must not wrap or sign-extend.
		var maxSeq = UdpPacket.CreateAudio(sessionId, ulong.MaxValue, ulong.MaxValue, payload);
		Expect(UdpPacket.TryParse(maxSeq, out var parsedMax), "A maximum-sequence packet was rejected.");
		Expect(parsedMax.Sequence == ulong.MaxValue, "A maximum sequence number was corrupted.");
		Expect(parsedMax.TimestampMs == ulong.MaxValue, "A maximum timestamp was corrupted.");

		var control = UdpPacket.CreateControl(UdpPacketKind.Register, sessionId);
		Expect(UdpPacket.TryParse(control, out var parsedControl), "A control packet was rejected.");
		Expect(parsedControl.Kind == UdpPacketKind.Register, "A control packet parsed as the wrong kind.");
		Expect(parsedControl.Payload.Length == 0, "A control packet carried a payload.");

		// An empty audio payload is legal framing; it simply carries no audio.
		var emptyAudio = UdpPacket.CreateAudio(sessionId, 1, 1, []);
		Expect(UdpPacket.TryParse(emptyAudio, out var parsedEmpty), "An empty audio packet was rejected.");
		Expect(parsedEmpty.Payload.Length == 0, "An empty audio packet gained a payload.");

		Expect(!UdpPacket.TryParse([], out _), "An empty packet was accepted.");
		Expect(!UdpPacket.TryParse(new byte[21], out _), "A packet shorter than the header was accepted.");

		var badMagic = (byte[])audio.Clone();
		badMagic[0] = (byte)'X';
		Expect(!UdpPacket.TryParse(badMagic, out _), "A packet with the wrong magic was accepted.");

		var badVersion = (byte[])audio.Clone();
		badVersion[4] = 2;
		Expect(!UdpPacket.TryParse(badVersion, out _), "A packet with an unknown version was accepted.");

		// An audio packet truncated inside its sequence/timestamp fields must be
		// refused rather than read past the end of what arrived.
		for (var length = 22; length < UdpPacket.AudioHeaderLength; length++)
		{
			var truncated = audio.Take(length).ToArray();
			Expect(!UdpPacket.TryParse(truncated, out _),
				$"A {length}-byte audio packet was accepted despite a short header.");
		}

		// A truncated *control* packet is a different case: the header is complete.
		var shortControl = audio.Take(22).ToArray();
		shortControl[5] = (byte)UdpPacketKind.Heartbeat;
		Expect(UdpPacket.TryParse(shortControl, out _), "A header-only heartbeat was rejected.");

		var writeBuffer = new byte[UdpPacket.AudioHeaderLength + payload.Length];
		var written = UdpPacket.WriteAudio(writeBuffer, sessionId, 9, 8, payload);
		Expect(written == writeBuffer.Length, "WriteAudio reported the wrong length.");
		Expect(writeBuffer.SequenceEqual(UdpPacket.CreateAudio(sessionId, 9, 8, payload)),
			"WriteAudio and CreateAudio disagree.");

		var tooSmall = new byte[UdpPacket.AudioHeaderLength + payload.Length - 1];
		try
		{
			UdpPacket.WriteAudio(tooSmall, sessionId, 1, 1, payload);
			Fail("WriteAudio accepted a destination that was too small.");
		}
		catch (ArgumentException)
		{
		}

		try
		{
			UdpPacket.CreateControl(UdpPacketKind.Register, new byte[15]);
			Fail("A 15-byte session ID was accepted.");
		}
		catch (ArgumentException)
		{
		}
	}

	/// <summary>
	/// The ring buffer decides what happens when audio arrives faster or slower
	/// than it is played, which is every real network. Its wrap-around and
	/// overflow paths only run after minutes of streaming, so they are exactly
	/// what a test has to reach directly.
	/// </summary>
	private static void TestRingBuffer()
	{
		var ring = new AudioRingBuffer(64);
		Expect(ring.BufferedBytes == 0, "A new ring buffer was not empty.");

		ring.Write([1, 2, 3, 4]);
		Expect(ring.BufferedBytes == 4, "A write did not show up as buffered bytes.");

		var destination = new byte[4];
		Expect(ring.Read(destination) == 4, "A read returned the wrong count.");
		Expect(destination.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "A read returned the wrong bytes.");
		Expect(ring.BufferedBytes == 0, "The buffer was not drained by a full read.");
		Expect(ring.Underruns == 0, "An exact read was counted as an underrun.");

		// Reading more than is buffered must zero-fill the rest, not repeat stale
		// audio, and must be counted so the diagnostics can show it.
		ring.Write([9, 9]);
		var overRead = new byte[6];
		Array.Fill(overRead, (byte)0x55);
		Expect(ring.Read(overRead) == 2, "A short read returned the wrong count.");
		Expect(overRead.Skip(2).All(b => b == 0), "A short read did not zero-fill the remainder.");
		Expect(ring.Underruns == 1, "A short read was not counted as an underrun.");

		// Wrap-around: write and read repeatedly so head and tail pass the end of
		// the storage, and check the bytes still come back in order.
		var wrapping = new AudioRingBuffer(64);
		var value = 0;
		var readBack = new byte[24];
		for (var round = 0; round < 20; round++)
		{
			var chunk = Enumerable.Range(0, 24).Select(_ => (byte)(value++ & 0xFF)).ToArray();
			wrapping.Write(chunk);
			Expect(wrapping.Read(readBack) == 24, "A wrapped read returned the wrong count.");
			Expect(readBack.SequenceEqual(chunk), $"Data was corrupted wrapping around on round {round}.");
		}
		Expect(wrapping.Drops == 0, "Wrapping around was counted as dropped audio.");

		// Overflow drops the oldest audio, which is right for a live stream: the
		// newest is what the listener is waiting for.
		var small = new AudioRingBuffer(64);
		var capacity = 64;
		small.Write(Enumerable.Repeat((byte)1, capacity).ToArray());
		small.Write([2, 2, 2, 2]);
		Expect(small.Drops == 4, "Overflow did not drop exactly the excess.");
		Expect(small.BufferedBytes == capacity, "Overflow left the buffer over capacity.");
		var afterOverflow = new byte[capacity];
		small.Read(afterOverflow);
		Expect(afterOverflow[^1] == 2, "Overflow discarded the newest audio instead of the oldest.");

		var trimming = new AudioRingBuffer(64);
		trimming.Write(Enumerable.Repeat((byte)7, 32).ToArray());
		trimming.DropOldest(8);
		Expect(trimming.BufferedBytes == 24, "DropOldest removed the wrong amount.");
		Expect(trimming.Drops == 8, "DropOldest was not counted against drops.");
		trimming.DropOldest(1000);
		Expect(trimming.BufferedBytes == 0, "DropOldest past the end left bytes behind.");
		trimming.DropOldest(-5);
		Expect(trimming.BufferedBytes == 0, "A negative DropOldest changed the buffer.");

		// Clear is for moving to another playback device: it must empty the buffer
		// without inflating the drop counter, or a healthy device switch reads as
		// buffer overflow in the diagnostics.
		var clearing = new AudioRingBuffer(64);
		clearing.Write(Enumerable.Repeat((byte)3, 40).ToArray());
		var dropsBeforeClear = clearing.Drops;
		clearing.Clear();
		Expect(clearing.BufferedBytes == 0, "Clear did not empty the buffer.");
		Expect(clearing.Drops == dropsBeforeClear, "Clear was counted as dropped audio.");
		clearing.Write([5, 5, 5, 5]);
		var afterClear = new byte[4];
		Expect(clearing.Read(afterClear) == 4, "The buffer was unusable after Clear.");
		Expect(afterClear.All(b => b == 5), "Stale audio survived Clear.");
	}

	private static void TestAudioShaping()
	{
		const int SampleRate = 48000;

		// Neutral settings must leave audio alone; a "no change" path that quietly
		// colours the sound would be invisible until someone A/B'd it.
		var flat = new[] { 0.5f, -0.5f, 0.25f, -0.25f };
		var expectedFlat = (float[])flat.Clone();
		new AudioShaper(SampleRate, 2, 100, 0, 0, 0, 0).Process(flat);
		for (var i = 0; i < flat.Length; i++)
		{
			Expect(Math.Abs(flat[i] - expectedFlat[i]) < 0.01f, "Neutral shaping changed the audio.");
		}

		var hardRight = Enumerable.Repeat(0.25f, 32).ToArray();
		new AudioShaper(SampleRate, 2, 100, 100, 0, 0, 0).Process(hardRight);
		Expect(hardRight.Where((_, i) => i % 2 == 0).All(v => v == 0f), "Panning hard right left audio on the left.");
		Expect(hardRight.Where((_, i) => i % 2 == 1).Any(v => v != 0f), "Panning hard right silenced the right.");

		// Amplification must clip to the valid range rather than wrap or produce
		// values a WASAPI render pass would turn into noise.
		var loud = Enumerable.Repeat(0.9f, 64).Concat(Enumerable.Repeat(-0.9f, 64)).ToArray();
		new AudioShaper(SampleRate, 2, 200, 0, 12, 12, 12).Process(loud);
		Expect(loud.All(v => float.IsFinite(v) && v >= -1f && v <= 1f), "Boosted audio left the valid range.");

		// Silence in, silence out, whatever the EQ: an EQ that rings on silence
		// would add a hiss to every quiet passage.
		var silence = new float[128];
		new AudioShaper(SampleRate, 2, 200, 0, -12, 12, -12).Process(silence);
		Expect(silence.All(v => v == 0f), "Shaping generated audio from silence.");

		// Out-of-range settings are clamped rather than producing invalid filters.
		var extreme = Enumerable.Repeat(0.3f, 64).ToArray();
		new AudioShaper(SampleRate, 2, 10000, -10000, 500, -500, 500).Process(extreme);
		Expect(extreme.All(float.IsFinite), "Out-of-range shaping settings produced invalid audio.");

		// Finiteness alone proves nothing here: the final clamp to [-1, 1] hides an
		// unclamped gain on loud audio. Quiet audio is where the difference shows,
		// so check a volume past the maximum behaves exactly like the maximum.
		var quietAtMaximum = Enumerable.Repeat(0.001f, 64).ToArray();
		var quietPastMaximum = (float[])quietAtMaximum.Clone();
		new AudioShaper(SampleRate, 2, 200, 0, 0, 0, 0).Process(quietAtMaximum);
		new AudioShaper(SampleRate, 2, 10000, 0, 0, 0, 0).Process(quietPastMaximum);
		for (var i = 0; i < quietAtMaximum.Length; i++)
		{
			Expect(Math.Abs(quietAtMaximum[i] - quietPastMaximum[i]) < 1e-6f,
				"A receive volume past the maximum was not clamped to it.");
		}
		// And the maximum really is a boost, so the comparison above is not two
		// identical no-ops agreeing with each other.
		Expect(quietAtMaximum[8] > 0.0015f, "The maximum receive volume did not amplify quiet audio.");

		// Zero volume must be silence, whatever the EQ is set to.
		var silenced = Enumerable.Repeat(0.5f, 64).ToArray();
		new AudioShaper(SampleRate, 2, 0, 0, 12, 12, 12).Process(silenced);
		Expect(silenced.All(v => v == 0f), "Zero receive volume did not silence boosted audio.");

		// A pan past the extremes behaves like the extreme, for the same reason.
		var panPast = Enumerable.Repeat(0.25f, 32).ToArray();
		new AudioShaper(SampleRate, 2, 100, 999, 0, 0, 0).Process(panPast);
		Expect(panPast.Where((_, i) => i % 2 == 0).All(v => v == 0f), "A pan past hard right was not clamped.");

		// Mono must not index a right-channel pan gain that does not exist.
		var mono = Enumerable.Repeat(0.4f, 16).ToArray();
		new AudioShaper(SampleRate, 1, 100, -100, 0, 0, 0).Process(mono);
		Expect(mono.All(float.IsFinite), "Mono shaping produced invalid audio.");
	}

	private static void TestFrameQueue()
	{
		using (var queue = new AudioFrameQueue(2))
		{
			Expect(queue.TryWrite(new PooledAudioFrame(4)), "The queue refused the first frame.");
			Expect(queue.TryWrite(new PooledAudioFrame(4)), "The queue refused the second frame.");
			Expect(queue.DroppedFrames == 0, "The queue dropped a frame while under capacity.");

			// Over capacity the oldest frame goes, so the listener stays current
			// rather than falling further behind.
			Expect(queue.TryWrite(new PooledAudioFrame(4)), "The queue refused a frame at capacity.");
			Expect(queue.DroppedFrames == 1, "The queue did not report dropping the oldest frame.");
		}

		using (var completed = new AudioFrameQueue(4))
		{
			completed.Complete();
			Expect(!completed.TryWrite(new PooledAudioFrame(4)), "A completed queue accepted a frame.");
		}

		var frame = new PooledAudioFrame(8);
		Expect(frame.Length == 8, "A pooled frame reported the wrong length.");
		Expect(frame.Span.Length == 8, "A pooled frame exposed the wrong span length.");
		frame.Span[0] = 42;
		Expect(frame.ReadOnlySpan[0] == 42, "A pooled frame lost its contents.");
		frame.Dispose();
		// Double dispose must not return the same array to the pool twice, which
		// would hand the same memory to two owners later on.
		frame.Dispose();
		try
		{
			_ = frame.Span;
			Fail("A disposed frame still exposed its buffer.");
		}
		catch (ObjectDisposedException)
		{
		}
	}

	/// <summary>
	/// Every one of these arguments is built by the add-on from user settings, so a
	/// parser that accepts nonsense turns a typo in a settings field into a helper
	/// that starts and then misbehaves.
	/// </summary>
	private static void TestOptionParsing()
	{
		var subscriber = HelperOptions.Parse(["--role", "subscriber", "--host", "pc", "--key", "room"]);
		Expect(subscriber.Role == ConnectionRole.Subscriber, "The subscriber role was misparsed.");
		Expect(subscriber.Port == 6838, "The default port changed.");
		Expect(subscriber.Codec == AudioPayloadCodec.Opus, "The default codec changed.");
		Expect(subscriber.OpusFec, "Opus FEC was not on by default.");
		Expect(subscriber.ReceiveVolume == 100, "The default receive volume changed.");

		var publisher = HelperOptions.Parse([
			"--role", "publisher", "--host", "pc", "--key", "room",
			"--exclude-pid", "1234", "--codec", "pcm", "--opus-frame-ms", "5", "--disable-fec",
		]);
		Expect(publisher.Role == ConnectionRole.Publisher, "The publisher role was misparsed.");
		Expect(publisher.Codec == AudioPayloadCodec.Pcm16, "The PCM codec was misparsed.");
		Expect(!publisher.OpusFec, "--disable-fec was ignored.");

		// A process name is used to find a PID, so it must never be able to carry a
		// path out of that lookup.
		var named = HelperOptions.Parse([
			"--role", "publisher", "--host", "pc", "--key", "room", "--include-process-name", "Spotify.exe",
		]);
		Expect(named.CaptureProcessName == "Spotify", "The .exe suffix was not trimmed.");

		ExpectRejected(["--role", "publisher", "--host", "pc", "--key", "room", "--include-process-name", @"..\evil"],
			"a process name containing a path");
		ExpectRejected(["--role", "publisher", "--host", "pc", "--key", "room", "--include-process-name", "a/b"],
			"a process name containing a separator");
		ExpectRejected(["--role", "nonsense", "--host", "pc", "--key", "room"], "an unknown role");
		ExpectRejected(["--host", "pc", "--key", "room"], "a missing role");
		ExpectRejected(["--role", "subscriber", "--key", "room"], "a missing host");
		ExpectRejected(["--role", "subscriber", "--host", "pc"], "a missing key");
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "   "], "a whitespace key");
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "room", "--port", "0"], "port zero");
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "room", "--port", "65536"], "a port over the maximum");
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "room", "--port", "http"], "a non-numeric port");
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "room", "--receive-volume", "201"], "a volume over 200");
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "room", "--receive-pan", "-101"], "a pan below -100");
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "room", "--bass-db", "13"], "an EQ gain over 12 dB");
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "room", "--opus-frame-ms", "7"], "an unsupported frame duration");
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "room", "--codec", "mp3"], "an unknown codec");
		ExpectRejected(["--role", "publisher", "--host", "pc", "--key", "room"], "a publisher with no capture source");
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "room", "--port"], "a trailing option with no value");
		ExpectRejected(["stray", "--role", "subscriber"], "a positional argument");
		// PCM at anything but 5 ms exceeds the relay's UDP payload limit.
		ExpectRejected(["--role", "publisher", "--host", "pc", "--key", "room", "--exclude-pid", "1",
			"--codec", "pcm", "--opus-frame-ms", "10"], "PCM at 10 ms");

		// The password must be readable from the environment, so it never appears in
		// a command line any other process on the machine can read.
		const string variable = "NVDA_REMOTE_AUDIO_SELFTEST_PASSWORD";
		Environment.SetEnvironmentVariable(variable, "from the environment");
		try
		{
			var withPassword = HelperOptions.Parse([
				"--role", "subscriber", "--host", "pc", "--key", "room", "--password-env", variable,
			]);
			Expect(withPassword.Password == "from the environment", "The password was not read from the environment.");
		}
		finally
		{
			Environment.SetEnvironmentVariable(variable, null);
		}
		// Naming a variable that is not set is a mistake worth reporting, not a
		// silent fall back to sending unencrypted audio.
		ExpectRejected(["--role", "subscriber", "--host", "pc", "--key", "room",
			"--password-env", "NVDA_REMOTE_AUDIO_SELFTEST_MISSING"], "an unset password variable");

		Expect(HelperOptions.Parse(["--help"]).ShowHelp, "--help was not recognised.");
		Expect(HelperOptions.Parse(["--self-test"]).SelfTest, "--self-test was not recognised.");
		Expect(HelperOptions.Parse(["--list-output-devices"]).ListOutputDevices, "--list-output-devices was not recognised.");
		Expect(HelperOptions.Parse(["--list-audio-apps"]).ListAudioApps, "--list-audio-apps was not recognised.");
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
		Fail($"The helper accepted {what}.");
	}

	private static void TestPayloadEdgeCases()
	{
		const ulong Sequence = 7;
		const ulong Timestamp = 99;
		var destination = new byte[4096];
		var plaintext = new byte[4096];

		using (var sender = new AudioPayloadProtocol("shared", "room"))
		using (var receiver = new AudioPayloadProtocol("shared", "room"))
		{
			// An empty payload is what a codec produces for pure silence.
			var length = sender.Encode(AudioPayloadCodec.Opus, 10, Sequence, Timestamp, [], destination);
			Expect(receiver.TryDecode(destination.AsSpan(0, length), Sequence, Timestamp, 10, plaintext, out var empty),
				"An empty encrypted payload was rejected.");
			Expect(empty.Length == 0, "An empty payload gained bytes.");

			// A full-size PCM packet is the largest thing that ever goes on the wire.
			var large = Enumerable.Range(0, 1200 - sender.OverheadBytes).Select(i => (byte)i).ToArray();
			length = sender.Encode(AudioPayloadCodec.Pcm16, 5, Sequence, Timestamp, large, destination);
			Expect(length <= 1200, "A maximum-size payload exceeded the relay's UDP limit.");
			Expect(receiver.TryDecode(destination.AsSpan(0, length), Sequence, Timestamp, 5, plaintext, out var big),
				"A maximum-size payload was rejected.");
			Expect(plaintext.AsSpan(0, big.Length).SequenceEqual(large), "A maximum-size payload was corrupted.");

			// The sequence and timestamp are authenticated, not just carried: replaying
			// a packet under a different sequence must not decrypt.
			length = sender.Encode(AudioPayloadCodec.Opus, 10, Sequence, Timestamp, [1, 2, 3], destination);
			Expect(!receiver.TryDecode(destination.AsSpan(0, length), Sequence + 1, Timestamp, 10, plaintext, out _),
				"A packet decrypted under the wrong sequence number.");
			Expect(!receiver.TryDecode(destination.AsSpan(0, length), Sequence, Timestamp + 1, 10, plaintext, out _),
				"A packet decrypted under the wrong timestamp.");

			// Every header byte is authenticated too, so flipping the codec or the
			// frame duration in transit must not go unnoticed.
			var tamperedCodec = destination.AsSpan(0, length).ToArray();
			tamperedCodec[6] = (byte)AudioPayloadCodec.Pcm16;
			Expect(!receiver.TryDecode(tamperedCodec, Sequence, Timestamp, 10, plaintext, out _),
				"A packet with a switched codec byte still authenticated.");
			var tamperedFrameMs = destination.AsSpan(0, length).ToArray();
			tamperedFrameMs[7] = 20;
			Expect(!receiver.TryDecode(tamperedFrameMs, Sequence, Timestamp, 10, plaintext, out _),
				"A packet with a switched frame duration still authenticated.");
		}

		// The room key is part of the key derivation, so the same password in two
		// different rooms must not cross over.
		using (var roomOne = new AudioPayloadProtocol("same password", "room one"))
		using (var roomTwo = new AudioPayloadProtocol("same password", "room two"))
		{
			var length = roomOne.Encode(AudioPayloadCodec.Opus, 10, Sequence, Timestamp, [4, 5, 6], destination);
			Expect(!roomTwo.TryDecode(destination.AsSpan(0, length), Sequence, Timestamp, 10, plaintext, out _),
				"The same password decrypted audio from a different room.");
		}

		// A repeated nonce under one key is what breaks AES-GCM outright, so both
		// halves of the nonce construction are worth pinning down.
		//
		// Within a session the nonce is the random base XOR the sequence number, so
		// distinct sequences must give distinct keystreams. Checked through the
		// ciphertext of a payload long enough that a chance collision is not a
		// realistic outcome -- a one-byte body has only 256 values and would collide
		// no matter how sound the nonce is.
		var nonceProbe = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
		using (var sender = new AudioPayloadProtocol("nonce check", "room"))
		{
			var ciphertexts = new HashSet<string>();
			for (ulong sequence = 0; sequence < 2000; sequence++)
			{
				var length = sender.Encode(AudioPayloadCodec.Opus, 10, sequence, sequence, nonceProbe, destination);
				var body = Convert.ToHexString(destination.AsSpan(20, nonceProbe.Length));
				Expect(ciphertexts.Add(body), $"The keystream repeated at sequence {sequence}.");
			}
		}

		// Across sessions the sequence number restarts at zero, so the only thing
		// keeping a reconnect from replaying the first session's nonces is that each
		// instance draws a fresh random base. Every reconnect builds a new instance.
		var bases = new HashSet<string>();
		for (var session = 0; session < 50; session++)
		{
			using var reconnected = new AudioPayloadProtocol("nonce check", "room");
			var length = reconnected.Encode(AudioPayloadCodec.Opus, 10, 0, 0, nonceProbe, destination);
			Expect(length > 20, "An encoded payload was impossibly short.");
			Expect(bases.Add(Convert.ToHexString(destination.AsSpan(8, 12))),
				"Two sessions started from the same nonce base.");
		}

		// Without a password the envelope is still framed and still version-checked;
		// only the encryption is absent.
		using (var plain = new AudioPayloadProtocol("", "room"))
		{
			Expect(!plain.EncryptionEnabled, "An empty password enabled encryption.");
			var length = plain.Encode(AudioPayloadCodec.Opus, 10, Sequence, Timestamp, [1, 2, 3], destination);
			Expect(AudioPayloadProtocol.PeerVersion(destination.AsSpan(0, length)) == AudioPayloadProtocol.CurrentVersion,
				"An unencrypted payload carried the wrong version.");
		}

		// A destination too small must throw rather than write past the end.
		using (var sender = new AudioPayloadProtocol("shared", "room"))
		{
			try
			{
				sender.Encode(AudioPayloadCodec.Opus, 10, Sequence, Timestamp, new byte[100], new byte[10]);
				Fail("Encoding into an undersized destination was allowed.");
			}
			catch (ArgumentException)
			{
			}
		}

		// A legacy packet that happens to be longer than the receive buffer must be
		// refused, not truncated into audio.
		using (var plain = new AudioPayloadProtocol("", "room"))
		{
			var small = new byte[16];
			Expect(plain.Decode(new byte[64], 1, 1, 10, small, out _) == AudioPayloadDecodeStatus.Malformed,
				"An oversized legacy payload was accepted.");
		}

		// Guard the constant itself: the version byte written must be the version
		// the code says it writes, or the two halves would disagree silently.
		using (var sender = new AudioPayloadProtocol("", "room"))
		{
			var length = sender.Encode(AudioPayloadCodec.Opus, 10, 1, 1, [1], destination);
			Expect(length > 4, "An encoded payload was impossibly short.");
			Expect(destination[4] == AudioPayloadProtocol.CurrentVersion,
				"The encoded version byte does not match CurrentVersion.");
			Expect(AudioPayloadProtocol.OldestSupportedVersion <= AudioPayloadProtocol.CurrentVersion,
				"The oldest supported version is newer than the current one.");
			Expect(BinaryPrimitives.ReadUInt32BigEndian(destination.AsSpan(0, 4)) == 0x52414532u,
				"The payload magic is not RAE2.");
		}
	}
}
