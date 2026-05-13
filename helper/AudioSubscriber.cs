using System.Diagnostics;
using Concentus;

namespace NVDARemoteAudioHelper;

internal static class AudioSubscriber
{
	private const int SampleRate = 48000;
	private const int Channels = 2;
	private const int MaxDecodedSamplesPerChannel = 5760;

	public static async Task RunAsync(
		RemoteAudioSession session,
		int prebufferMs,
		int outputLatencyMs,
		int playbackBufferMs,
		int opusFrameMilliseconds,
		CancellationToken cancellationToken)
	{
		using var playback = new PlaybackSink(SampleRate, Channels, prebufferMs, outputLatencyMs, playbackBufferMs);
		var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels, TextWriter.Null);
		var decoded = new float[MaxDecodedSamplesPerChannel * Channels];
		var lastSequence = ulong.MaxValue;
		var fallbackSamplesPerChannel = SampleRate * Math.Clamp(opusFrameMilliseconds, 5, 20) / 1000;
		var lastSamplesPerChannel = fallbackSamplesPerChannel;
		var packetsReceived = 0UL;
		var duplicateOrLatePackets = 0UL;
		var fecRecoveries = 0UL;
		var plcFrames = 0UL;
		var unrecoveredGaps = 0UL;
		var start = Stopwatch.StartNew();
		var nextDiagnosticAt = TimeSpan.FromSeconds(5);

		JsonLog.Write("status", "Listening for remote audio.", new Dictionary<string, object?>
		{
			["prebuffer_ms"] = prebufferMs,
			["output_latency_ms"] = outputLatencyMs,
			["buffer_ms"] = playbackBufferMs,
			["opus_frame_ms"] = opusFrameMilliseconds,
		});

		while (!cancellationToken.IsCancellationRequested)
		{
			var frame = await session.ReceiveAudioAsync(cancellationToken);
			if (lastSequence != ulong.MaxValue && frame.Sequence <= lastSequence)
			{
				duplicateOrLatePackets++;
				continue;
			}

			packetsReceived++;
			if (lastSequence != ulong.MaxValue && frame.Sequence > lastSequence + 1)
			{
				var gap = frame.Sequence - lastSequence - 1;
				if (gap == 1 && TryDecodeFec(decoder, frame.Payload.AsSpan(), decoded, lastSamplesPerChannel, playback))
				{
					fecRecoveries++;
				}
				else
				{
					unrecoveredGaps += gap;
					var missingPackets = Math.Min(gap, 5UL);
					for (var i = 0UL; i < missingPackets; i++)
					{
						if (TryDecodePacketLossConcealment(decoder, decoded, lastSamplesPerChannel, playback))
						{
							plcFrames++;
						}
					}
				}
			}

			lastSequence = frame.Sequence;
			var samplesPerChannel = decoder.Decode(frame.Payload.AsSpan(), decoded.AsSpan(), MaxDecodedSamplesPerChannel, false);
			if (samplesPerChannel > 0)
			{
				lastSamplesPerChannel = samplesPerChannel;
				playback.AddSamples(decoded.AsSpan(0, samplesPerChannel * Channels));
			}

			if (start.Elapsed >= nextDiagnosticAt)
			{
				JsonLog.Write("diagnostic", "Subscriber audio statistics.", new Dictionary<string, object?>
				{
					["packets_received"] = packetsReceived,
					["duplicate_or_late_packets"] = duplicateOrLatePackets,
					["fec_recoveries"] = fecRecoveries,
					["plc_frames"] = plcFrames,
					["unrecovered_gaps"] = unrecoveredGaps,
					["buffer_ms"] = playback.CurrentBufferMs,
					["underruns"] = playback.Underruns,
					["drops"] = playback.Drops,
					["trim_drops"] = playback.TrimDrops,
					["drift_drops"] = playback.DriftDrops,
					["drift_repeats"] = playback.DriftRepeats,
				});
				nextDiagnosticAt += TimeSpan.FromSeconds(5);
			}
		}
	}

	private static bool TryDecodeFec(
		IOpusDecoder decoder,
		ReadOnlySpan<byte> payload,
		float[] decoded,
		int samplesPerChannel,
		PlaybackSink playback)
	{
		try
		{
			var fecSamplesPerChannel = decoder.Decode(payload, decoded.AsSpan(), samplesPerChannel, true);
			if (fecSamplesPerChannel <= 0)
			{
				return false;
			}

			playback.AddSamples(decoded.AsSpan(0, fecSamplesPerChannel * Channels));
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryDecodePacketLossConcealment(
		IOpusDecoder decoder,
		float[] decoded,
		int samplesPerChannel,
		PlaybackSink playback)
	{
		try
		{
			var concealedSamplesPerChannel = decoder.Decode(ReadOnlySpan<byte>.Empty, decoded.AsSpan(), samplesPerChannel, false);
			if (concealedSamplesPerChannel <= 0)
			{
				return false;
			}

			playback.AddSamples(decoded.AsSpan(0, concealedSamplesPerChannel * Channels));
			return true;
		}
		catch
		{
			return false;
		}
	}
}
