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

		JsonLog.Write("self_test", "All helper self-tests passed.", new Dictionary<string, object?>
		{
			["encryption"] = "passed",
			["wrong_password_rejection"] = "passed",
			["tamper_rejection"] = "passed",
			["unique_packet_ciphertext"] = "passed",
			["pcm_payload"] = "passed",
			["legacy_opus"] = "passed",
			["audio_shaping"] = "passed",
		});
	}
}
