using System.Diagnostics;
using System.Runtime.InteropServices;
using Concentus;

namespace NVDARemoteAudioHelper;

internal static class AudioSubscriber
{
	private const int SampleRate = 48000;
	private const int Channels = 2;
	private const int MaxDecodedSamplesPerChannel = 5760;
	// A single odd packet can be corruption in transit, so neither a rejected
	// password nor a mismatched version ends the session on its own.
	private const int FailuresBeforeGivingUp = 3;

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
		var versionMismatches = 0UL;
		var legacyPublisherPackets = 0UL;
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
			["payload_version"] = AudioPayloadProtocol.CurrentVersion,
			["payload_version_minimum"] = AudioPayloadProtocol.OldestSupportedVersion,
		});

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var frame = await session.ReceiveAudioAsync(cancellationToken);
				var status = payloadProtocol.Decode(
					frame.Payload,
					frame.Sequence,
					frame.TimestampMs,
					opusFrameMilliseconds,
					plaintext,
					out var payload);
				if (status != AudioPayloadDecodeStatus.Ok)
				{
					if (status is AudioPayloadDecodeStatus.PeerTooNew or AudioPayloadDecodeStatus.PeerTooOld)
					{
						versionMismatches++;
						if (versionMismatches >= FailuresBeforeGivingUp)
						{
							throw new InvalidOperationException(
								VersionMismatchMessage(status, AudioPayloadProtocol.PeerVersion(frame.Payload)));
						}
						continue;
					}
					authenticationFailures++;
					if (authenticationFailures >= FailuresBeforeGivingUp)
					{
						throw new InvalidOperationException(
							status == AudioPayloadDecodeStatus.AuthenticationFailed
								? "Remote audio was rejected. Both computers must use the same end-to-end encryption password."
								: "Remote audio arrived damaged and could not be decoded. Check the network path between the two computers.");
					}
					continue;
				}
				if (!string.IsNullOrEmpty(password) && !payload.Encrypted)
				{
					// A stream with no envelope at all comes from a publisher older than
					// 0.2.0, which cannot encrypt: name the machine that needs updating
					// rather than only what is wrong with the audio.
					throw new InvalidOperationException(payload.Legacy
						? "The sending computer is running a version of NVDA Remote Audio Client older than 0.2.0, which cannot encrypt audio. Update the add-on on the sending computer, or clear the end-to-end encryption password on both computers."
						: "The publisher is sending unencrypted audio while this receiver requires an end-to-end password.");
				}
				if (payload.Legacy)
				{
					legacyPublisherPackets++;
				}
				authenticationFailures = 0;
				versionMismatches = 0;
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
						["version_mismatches"] = versionMismatches,
						["legacy_publisher_packets"] = legacyPublisherPackets,
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
						["endpoint_rebuilds"] = playback.EndpointRebuilds,
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

	/// <summary>
	/// Names the computer whose add-on is behind. That is the only actionable part,
	/// and the user cannot see the other machine's screen to work it out.
	/// </summary>
	private static string VersionMismatchMessage(AudioPayloadDecodeStatus status, int peerVersion) =>
		status == AudioPayloadDecodeStatus.PeerTooNew
			? $"The sending computer has a newer version of NVDA Remote Audio Client than this one. Update the add-on on this computer. It sent audio format {peerVersion}; this computer understands up to {AudioPayloadProtocol.CurrentVersion}."
			: $"The sending computer has an older version of NVDA Remote Audio Client than this one. Update the add-on on the sending computer. It sent audio format {peerVersion}; this computer needs at least {AudioPayloadProtocol.OldestSupportedVersion}.";

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
