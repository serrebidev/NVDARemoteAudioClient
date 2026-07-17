using System.Diagnostics;
using System.Runtime.InteropServices;
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
		string outputDeviceId,
		int receiveVolume,
		int receivePan,
		int bassDb,
		int midDb,
		int trebleDb,
		string password,
		string roomKey,
		string recordFolder,
		CancellationToken cancellationToken)
	{
		using var playback = new PlaybackSink(
			SampleRate,
			Channels,
			prebufferMs,
			outputLatencyMs,
			playbackBufferMs,
			outputDeviceId,
			receiveVolume,
			receivePan,
			bassDb,
			midDb,
			trebleDb);
		using var payloadProtocol = new AudioPayloadProtocol(password, roomKey);
		using var recorder = new ReceivedAudioRecorder(recordFolder, SampleRate, Channels);
		var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels, TextWriter.Null);
		var decoded = new float[MaxDecodedSamplesPerChannel * Channels];
		var plaintext = new byte[session.MaxPayloadBytes];
		var lastSequence = ulong.MaxValue;
		var fallbackSamplesPerChannel = SampleRate * Math.Clamp(opusFrameMilliseconds, 5, 20) / 1000;
		var lastSamplesPerChannel = fallbackSamplesPerChannel;
		var packetsReceived = 0UL;
		var duplicateOrLatePackets = 0UL;
		var fecRecoveries = 0UL;
		var plcFrames = 0UL;
		var unrecoveredGaps = 0UL;
		var authenticationFailures = 0UL;
		var pcmPackets = 0UL;
		var start = Stopwatch.StartNew();
		var nextDiagnosticAt = TimeSpan.FromSeconds(5);

		JsonLog.Write("status", "Listening for remote audio.", new Dictionary<string, object?>
		{
			["prebuffer_ms"] = prebufferMs,
			["output_latency_ms"] = outputLatencyMs,
			["buffer_ms"] = playbackBufferMs,
			["opus_frame_ms"] = opusFrameMilliseconds,
			["output_device_id"] = outputDeviceId,
			["receive_volume"] = receiveVolume,
			["receive_pan"] = receivePan,
			["bass_db"] = bassDb,
			["mid_db"] = midDb,
			["treble_db"] = trebleDb,
			["encrypted"] = !string.IsNullOrEmpty(password),
			["recording"] = recorder.IsRecording,
		});

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var frame = await session.ReceiveAudioAsync(cancellationToken);
				if (!payloadProtocol.TryDecode(
					frame.Payload,
					frame.Sequence,
					frame.TimestampMs,
					opusFrameMilliseconds,
					plaintext,
					out var payload))
				{
					authenticationFailures++;
					if (authenticationFailures >= 3)
					{
						throw new InvalidOperationException("Unable to authenticate remote audio. Check that both machines use the same end-to-end password and updated add-on version.");
					}
					continue;
				}
				if (!string.IsNullOrEmpty(password) && !payload.Encrypted)
				{
					throw new InvalidOperationException("The publisher is sending unencrypted audio while this receiver requires an end-to-end password.");
				}
				authenticationFailures = 0;
				if (lastSequence != ulong.MaxValue && frame.Sequence <= lastSequence)
				{
					duplicateOrLatePackets++;
					continue;
				}

				packetsReceived++;
				var encodedPayload = plaintext.AsSpan(0, payload.Length);
				if (payload.Codec == AudioPayloadCodec.Opus && lastSequence != ulong.MaxValue && frame.Sequence > lastSequence + 1)
				{
					var gap = frame.Sequence - lastSequence - 1;
					if (gap == 1 && TryDecodeFec(decoder, encodedPayload, decoded, lastSamplesPerChannel, playback, recorder))
					{
						fecRecoveries++;
					}
					else
					{
						unrecoveredGaps += gap;
						var missingPackets = Math.Min(gap, 5UL);
						for (var i = 0UL; i < missingPackets; i++)
						{
							if (TryDecodePacketLossConcealment(decoder, decoded, lastSamplesPerChannel, playback, recorder))
							{
								plcFrames++;
							}
						}
					}
				}

				lastSequence = frame.Sequence;
				int samplesPerChannel;
				if (payload.Codec == AudioPayloadCodec.Opus)
				{
					samplesPerChannel = decoder.Decode(encodedPayload, decoded.AsSpan(), MaxDecodedSamplesPerChannel, false);
				}
				else
				{
					if (encodedPayload.Length % (Channels * sizeof(short)) != 0)
					{
						continue;
					}
					var pcm = MemoryMarshal.Cast<byte, short>(encodedPayload);
					samplesPerChannel = pcm.Length / Channels;
					if (samplesPerChannel > MaxDecodedSamplesPerChannel)
					{
						continue;
					}
					for (var index = 0; index < pcm.Length; index++)
					{
						decoded[index] = pcm[index] / 32768f;
					}
					pcmPackets++;
				}
				if (samplesPerChannel > 0)
				{
					lastSamplesPerChannel = samplesPerChannel;
					var sampleCount = samplesPerChannel * Channels;
					recorder.Write(decoded, sampleCount);
					playback.AddSamples(decoded.AsSpan(0, sampleCount));
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
						["authentication_failures"] = authenticationFailures,
						["pcm_packets"] = pcmPackets,
						["buffer_ms"] = playback.CurrentBufferMs,
						["underruns"] = playback.Underruns,
						["partial_reads"] = playback.PartialReads,
						["drops"] = playback.Drops,
						["trim_drops"] = playback.TrimDrops,
						["drift_drops"] = playback.DriftDrops,
						["drift_repeats"] = playback.DriftRepeats,
						["drift_resampler_ratio"] = playback.DriftResamplerRatio,
						["drift_resampler_updates"] = playback.DriftResamplerUpdates,
						["udp_max_receive_gap_ms"] = session.TakeMaxReceiveGapMilliseconds(),
					});
					nextDiagnosticAt += TimeSpan.FromSeconds(5);
				}
			}
		}
		finally
		{
			(decoder as IDisposable)?.Dispose();
		}
	}

	private static bool TryDecodeFec(
		IOpusDecoder decoder,
		ReadOnlySpan<byte> payload,
		float[] decoded,
		int samplesPerChannel,
		PlaybackSink playback,
		ReceivedAudioRecorder recorder)
	{
		try
		{
			var fecSamplesPerChannel = decoder.Decode(payload, decoded.AsSpan(), samplesPerChannel, true);
			if (fecSamplesPerChannel <= 0)
			{
				return false;
			}

			var sampleCount = fecSamplesPerChannel * Channels;
			recorder.Write(decoded, sampleCount);
			playback.AddSamples(decoded.AsSpan(0, sampleCount));
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
		PlaybackSink playback,
		ReceivedAudioRecorder recorder)
	{
		try
		{
			var concealedSamplesPerChannel = decoder.Decode(ReadOnlySpan<byte>.Empty, decoded.AsSpan(), samplesPerChannel, false);
			if (concealedSamplesPerChannel <= 0)
			{
				return false;
			}

			var sampleCount = concealedSamplesPerChannel * Channels;
			recorder.Write(decoded, sampleCount);
			playback.AddSamples(decoded.AsSpan(0, sampleCount));
			return true;
		}
		catch
		{
			return false;
		}
	}
}
