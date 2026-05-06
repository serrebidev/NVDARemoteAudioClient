using Concentus.Structs;

namespace NVDARemoteAudioHelper;

internal static class AudioSubscriber
{
	private const int SampleRate = 48000;
	private const int Channels = 2;
	private const int PacketSamplesPerChannel = 720;
	private const int MaxDecodedSamplesPerChannel = 5760;

	public static async Task RunAsync(
		RemoteAudioSession session,
		int prebufferMs,
		int outputLatencyMs,
		int playbackBufferMs,
		CancellationToken cancellationToken)
	{
		using var playback = new PlaybackSink(SampleRate, Channels, prebufferMs, outputLatencyMs, playbackBufferMs);
		var decoder = new OpusDecoder(SampleRate, Channels);
		var decoded = new short[MaxDecodedSamplesPerChannel * Channels];
		var lastSequence = ulong.MaxValue;

		JsonLog.Write("status", "Listening for remote audio.", new Dictionary<string, object?>
		{
			["prebuffer_ms"] = prebufferMs,
			["output_latency_ms"] = outputLatencyMs,
			["buffer_ms"] = playbackBufferMs,
		});

		while (!cancellationToken.IsCancellationRequested)
		{
			var frame = await session.ReceiveAudioAsync(cancellationToken);
			if (lastSequence != ulong.MaxValue && frame.Sequence <= lastSequence)
			{
				continue;
			}

			if (lastSequence != ulong.MaxValue && frame.Sequence > lastSequence + 1)
			{
				var missingPackets = Math.Min(frame.Sequence - lastSequence - 1, 5UL);
				for (var i = 0UL; i < missingPackets; i++)
				{
					var concealedSamplesPerChannel = decoder.Decode(ReadOnlySpan<byte>.Empty, decoded.AsSpan(), PacketSamplesPerChannel, false);
					if (concealedSamplesPerChannel > 0)
					{
						playback.AddSamples(decoded.AsSpan(0, concealedSamplesPerChannel * Channels));
					}
				}
			}

			lastSequence = frame.Sequence;
			var samplesPerChannel = decoder.Decode(frame.Payload.AsSpan(), decoded.AsSpan(), MaxDecodedSamplesPerChannel, false);
			if (samplesPerChannel <= 0)
			{
				continue;
			}

			playback.AddSamples(decoded.AsSpan(0, samplesPerChannel * Channels));
		}
	}
}
