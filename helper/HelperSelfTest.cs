namespace NVDARemoteAudioHelper;

internal static class HelperSelfTest
{
	public static void Run()
	{
		var sample = Enumerable.Range(0, 128).Select(index => (byte)index).ToArray();
		var encoded = new byte[512];
		var decoded = new byte[512];
		const ulong sequence = 42;
		const ulong timestamp = 1234;

		using (var sender = new AudioPayloadProtocol("correct horse battery staple", "room"))
		using (var receiver = new AudioPayloadProtocol("correct horse battery staple", "room"))
		{
			var length = sender.Encode(AudioPayloadCodec.Opus, 5, sequence, timestamp, sample, encoded);
			if (!receiver.TryDecode(encoded.AsSpan(0, length), sequence, timestamp, 10, decoded, out var result) ||
				result.Codec != AudioPayloadCodec.Opus || result.FrameMilliseconds != 5 ||
				!decoded.AsSpan(0, result.Length).SequenceEqual(sample))
			{
				throw new InvalidOperationException("Encrypted payload round-trip failed.");
			}

			var tampered = encoded.AsSpan(0, length).ToArray();
			tampered[20] ^= 0x40;
			if (receiver.TryDecode(tampered, sequence, timestamp, 10, decoded, out _))
			{
				throw new InvalidOperationException("Tampered encrypted payload unexpectedly authenticated.");
			}

			var secondEncoded = new byte[512];
			var secondLength = sender.Encode(AudioPayloadCodec.Opus, 5, sequence + 1, timestamp + 5, sample, secondEncoded);
			if (encoded.AsSpan(0, length).SequenceEqual(secondEncoded.AsSpan(0, secondLength)))
			{
				throw new InvalidOperationException("Different packet sequences produced identical encrypted payloads.");
			}
		}

		using (var sender = new AudioPayloadProtocol("password one", "room"))
		using (var receiver = new AudioPayloadProtocol("password two", "room"))
		{
			var length = sender.Encode(AudioPayloadCodec.Pcm16, 5, sequence, timestamp, sample, encoded);
			if (receiver.TryDecode(encoded.AsSpan(0, length), sequence, timestamp, 5, decoded, out _))
			{
				throw new InvalidOperationException("Wrong password unexpectedly decrypted a payload.");
			}
		}

		using (var plain = new AudioPayloadProtocol("", "room"))
		{
			var length = plain.Encode(AudioPayloadCodec.Pcm16, 5, sequence, timestamp, sample, encoded);
			if (!plain.TryDecode(encoded.AsSpan(0, length), sequence, timestamp, 5, decoded, out var result) ||
				result.Codec != AudioPayloadCodec.Pcm16 || !decoded.AsSpan(0, result.Length).SequenceEqual(sample))
			{
				throw new InvalidOperationException("Plain payload round-trip failed.");
			}
			if (!plain.TryDecode(sample, sequence, timestamp, 10, decoded, out var legacy) ||
				!legacy.Legacy || legacy.Codec != AudioPayloadCodec.Opus ||
				!decoded.AsSpan(0, legacy.Length).SequenceEqual(sample))
			{
				throw new InvalidOperationException("Legacy Opus compatibility decode failed.");
			}
		}

		// A version byte the peer and we disagree on has to be told apart from a
		// wrong password, because the two send the user to different machines.
		using (var plain = new AudioPayloadProtocol("", "room"))
		{
			var length = plain.Encode(AudioPayloadCodec.Opus, 5, sequence, timestamp, sample, encoded);

			var newer = encoded.AsSpan(0, length).ToArray();
			newer[4] = (byte)(AudioPayloadProtocol.CurrentVersion + 1);
			if (plain.Decode(newer, sequence, timestamp, 5, decoded, out _) != AudioPayloadDecodeStatus.PeerTooNew)
			{
				throw new InvalidOperationException("A newer payload version was not reported as a newer publisher.");
			}
			if (AudioPayloadProtocol.PeerVersion(newer) != AudioPayloadProtocol.CurrentVersion + 1)
			{
				throw new InvalidOperationException("The peer payload version was misread.");
			}

			if (AudioPayloadProtocol.OldestSupportedVersion > 1)
			{
				var older = encoded.AsSpan(0, length).ToArray();
				older[4] = (byte)(AudioPayloadProtocol.OldestSupportedVersion - 1);
				if (plain.Decode(older, sequence, timestamp, 5, decoded, out _) != AudioPayloadDecodeStatus.PeerTooOld)
				{
					throw new InvalidOperationException("An older payload version was not reported as an older publisher.");
				}
			}

			// An unknown codec byte can only come from a newer publisher, and must not
			// be blamed on the network.
			var unknownCodec = encoded.AsSpan(0, length).ToArray();
			unknownCodec[6] = 0xFE;
			if (plain.Decode(unknownCodec, sequence, timestamp, 5, decoded, out _) != AudioPayloadDecodeStatus.PeerTooNew)
			{
				throw new InvalidOperationException("An unknown codec was not reported as a newer publisher.");
			}

			// A packet cut short is corruption, not a version problem.
			if (plain.Decode(encoded.AsSpan(0, 8), sequence, timestamp, 5, decoded, out _) != AudioPayloadDecodeStatus.Malformed)
			{
				throw new InvalidOperationException("A truncated envelope was not reported as malformed.");
			}

			// An encrypted publisher reaching a receiver with no password is a password
			// disagreement, not a version one.
			using var encrypting = new AudioPayloadProtocol("a password", "room");
			var encryptedLength = encrypting.Encode(AudioPayloadCodec.Opus, 5, sequence, timestamp, sample, encoded);
			if (plain.Decode(encoded.AsSpan(0, encryptedLength), sequence, timestamp, 5, decoded, out _)
				!= AudioPayloadDecodeStatus.AuthenticationFailed)
			{
				throw new InvalidOperationException("An encrypted payload reaching an unencrypted receiver was misreported.");
			}
		}

		using (var receiver = new AudioPayloadProtocol("password one", "room"))
		using (var sender = new AudioPayloadProtocol("password two", "room"))
		{
			var length = sender.Encode(AudioPayloadCodec.Opus, 5, sequence, timestamp, sample, encoded);
			if (receiver.Decode(encoded.AsSpan(0, length), sequence, timestamp, 5, decoded, out _)
				!= AudioPayloadDecodeStatus.AuthenticationFailed)
			{
				throw new InvalidOperationException("A wrong password was not reported as an authentication failure.");
			}
		}

		var muted = Enumerable.Repeat(0.25f, 32).ToArray();
		new AudioShaper(48000, 2, 0, 0, 0, 0, 0).Process(muted);
		if (muted.Any(value => value != 0f))
		{
			throw new InvalidOperationException("Zero receive volume did not mute shaped audio.");
		}

		var hardLeft = Enumerable.Repeat(0.25f, 32).ToArray();
		new AudioShaper(48000, 2, 100, -100, 2, -1, 1).Process(hardLeft);
		if (hardLeft.Where((_, index) => index % 2 == 1).Any(value => value != 0f) ||
			hardLeft.Any(value => !float.IsFinite(value) || value < -1f || value > 1f))
		{
			throw new InvalidOperationException("Pan or EQ shaping produced invalid output.");
		}

		HelperSelfTestCases.RunAll();

		var endpointFollowing = RunEndpointFollowingTest();

		JsonLog.Write("self_test", "All helper self-tests passed.", new Dictionary<string, object?>
		{
			["encryption"] = "passed",
			["wrong_password_rejection"] = "passed",
			["tamper_rejection"] = "passed",
			["unique_packet_ciphertext"] = "passed",
			["pcm_payload"] = "passed",
			["legacy_opus"] = "passed",
			["payload_version_negotiation"] = "passed",
			["payload_version"] = AudioPayloadProtocol.CurrentVersion,
			["payload_version_minimum"] = AudioPayloadProtocol.OldestSupportedVersion,
			["audio_shaping"] = "passed",
			["udp_framing"] = "passed",
			["ring_buffer"] = "passed",
			["frame_queue"] = "passed",
			["option_parsing"] = "passed",
			["payload_edge_cases"] = "passed",
			["endpoint_following"] = endpointFollowing,
		});
	}

	/// <summary>
	/// Opens a real playback device, plays into it, moves playback to the current
	/// endpoint as a device change would, and checks that audio still reaches the
	/// new one. Skipped on a machine with no output device, which is a legitimate
	/// state for a sending-only computer or a build agent.
	/// </summary>
	private static string RunEndpointFollowingTest()
	{
		using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
		{
			if (!enumerator.HasDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console))
			{
				return "skipped: no playback device";
			}
		}

		const int SampleRate = 48000;
		const int Channels = 2;
		// Silence: the self-test must not make noise on the user's speakers.
		var silence = new float[SampleRate / 100 * Channels];

		using var playback = new PlaybackSink(
			SampleRate, Channels,
			prebufferMilliseconds: 15,
			outputLatencyMilliseconds: 15,
			bufferMilliseconds: 120,
			outputDeviceId: "",
			receiveVolume: 0,
			receivePan: 0,
			bassDb: 0,
			midDb: 0,
			trebleDb: 0);

		for (var i = 0; i < 5; i++)
		{
			playback.AddSamples(silence);
		}
		if (playback.EndpointRebuilds != 0)
		{
			throw new InvalidOperationException("Playback moved endpoint without a device change.");
		}

		playback.SimulateEndpointChangeForSelfTest();
		// The flag is acted on by the next buffer, exactly as in the receive loop.
		playback.AddSamples(silence);
		if (playback.EndpointRebuilds != 1)
		{
			throw new InvalidOperationException("Playback did not follow the output device after a change.");
		}

		// Re-arming is the part that actually matters: a sink that re-opened but
		// never played again looks healthy on every other counter and is silent.
		// (Buffer drops are not checked here -- feeding a burst of audio faster
		// than real time makes the backlog trimmer discard some, by design.)
		for (var i = 0; i < 5; i++)
		{
			playback.AddSamples(silence);
		}
		if (!playback.IsPlaying)
		{
			throw new InvalidOperationException("Playback did not restart after following the output device.");
		}
		if (playback.EndpointRebuilds != 1)
		{
			throw new InvalidOperationException("Playback moved endpoint more than the one change asked for.");
		}

		return "passed";
	}
}
