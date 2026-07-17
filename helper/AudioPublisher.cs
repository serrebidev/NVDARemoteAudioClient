using System.Diagnostics;
using Concentus;
using Concentus.Enums;

namespace NVDARemoteAudioHelper;

internal static class AudioPublisher
{
	private const int SampleRate = 48000;
	private const int Channels = 2;

	public static async Task RunCaptureAsync(
		RemoteAudioSession session,
		int targetPid,
		bool includeTargetTree,
		string captureLabel,
		int bitrate,
		int opusFrameMilliseconds,
		bool useInbandFec,
		CancellationToken cancellationToken)
	{
		var packetSamplesPerChannel = FrameSamplesPerChannel(opusFrameMilliseconds);
		var channelCapacity = Math.Max(2, 40 / opusFrameMilliseconds);
		JsonLog.Write("status", includeTargetTree ? "Starting application capture." : "Starting system capture with NVDA audio excluded.", new Dictionary<string, object?>
		{
			[includeTargetTree ? "included_pid" : "excluded_pid"] = targetPid,
			["capture_source"] = captureLabel,
			["bitrate"] = bitrate,
			["opus_frame_ms"] = opusFrameMilliseconds,
			["opus_fec"] = useInbandFec,
			["queue_capacity"] = channelCapacity,
		});

		using var queue = new AudioFrameQueue(channelCapacity);

		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var capture = new ProcessLoopbackCapture(targetPid, includeTargetTree, packetSamplesPerChannel);
		var captureTask = capture.RunAsync(queue, linkedCts.Token);
		var encodeTask = EncodeAndSendLoopAsync(queue, session, bitrate, opusFrameMilliseconds, useInbandFec, linkedCts.Token);

		var completed = await Task.WhenAny(captureTask, encodeTask);
		try
		{
			await completed;
		}
		finally
		{
			linkedCts.Cancel();
			queue.Complete();
			try
			{
				await Task.WhenAll(captureTask, encodeTask).WaitAsync(TimeSpan.FromSeconds(2));
			}
			catch
			{
				// Preserve the original completion result.
			}
		}
	}

	public static async Task RunTestToneAsync(
		RemoteAudioSession session,
		int bitrate,
		int opusFrameMilliseconds,
		bool useInbandFec,
		CancellationToken cancellationToken)
	{
		var packetSamplesPerChannel = FrameSamplesPerChannel(opusFrameMilliseconds);
		var packetShorts = packetSamplesPerChannel * Channels;
		JsonLog.Write("status", "Sending generated test tone.", new Dictionary<string, object?>
		{
			["bitrate"] = bitrate,
			["opus_frame_ms"] = opusFrameMilliseconds,
			["opus_fec"] = useInbandFec,
		});

		var encoder = CreateEncoder(bitrate, useInbandFec);
		var opusBuffer = new byte[session.MaxPayloadBytes];
		var frame = new short[packetShorts];
		var sequence = 0UL;
		var phase = 0.0;
		var phaseStep = 2.0 * Math.PI * 440.0 / SampleRate;
		var start = Stopwatch.StartNew();
		var nextFrameAt = TimeSpan.Zero;

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				for (var i = 0; i < packetSamplesPerChannel; i++)
				{
					var value = (short)(Math.Sin(phase) * short.MaxValue * 0.12);
					frame[i * 2] = value;
					frame[(i * 2) + 1] = value;
					phase += phaseStep;
					if (phase >= Math.PI * 2.0)
					{
						phase -= Math.PI * 2.0;
					}
				}

				var encodedLength = EncodePacket(encoder, frame, packetSamplesPerChannel, opusBuffer);
				await session.SendAudioAsync(sequence++, (ulong)start.ElapsedMilliseconds, opusBuffer.AsMemory(0, encodedLength), cancellationToken);

				nextFrameAt += TimeSpan.FromMilliseconds(opusFrameMilliseconds);
				var delay = nextFrameAt - start.Elapsed;
				if (delay > TimeSpan.Zero)
				{
					await Task.Delay(delay, cancellationToken);
				}
			}
		}
		finally
		{
			(encoder as IDisposable)?.Dispose();
		}
	}

	private static async Task EncodeAndSendLoopAsync(
		AudioFrameQueue frames,
		RemoteAudioSession session,
		int bitrate,
		int opusFrameMilliseconds,
		bool useInbandFec,
		CancellationToken cancellationToken)
	{
		var encoder = CreateEncoder(bitrate, useInbandFec);
		var packetSamplesPerChannel = FrameSamplesPerChannel(opusFrameMilliseconds);
		var opusBuffer = new byte[session.MaxPayloadBytes];
		var sequence = 0UL;
		var start = Stopwatch.StartNew();
		var nextDiagnosticAt = TimeSpan.FromSeconds(5);
		var packetsSent = 0UL;

		try
		{
			await foreach (var frame in frames.ReadAllAsync(cancellationToken))
			{
				try
				{
					var encodedLength = EncodePacket(encoder, frame.ReadOnlySpan, packetSamplesPerChannel, opusBuffer);
					await session.SendAudioAsync(sequence++, (ulong)start.ElapsedMilliseconds, opusBuffer.AsMemory(0, encodedLength), cancellationToken);
					packetsSent++;
				}
				finally
				{
					frame.Dispose();
				}

				if (start.Elapsed >= nextDiagnosticAt)
				{
					JsonLog.Write("diagnostic", "Publisher audio statistics.", new Dictionary<string, object?>
					{
						["packets_sent"] = packetsSent,
						["opus_frame_ms"] = opusFrameMilliseconds,
						["capture_queue_drops"] = frames.DroppedFrames,
						["udp_max_send_ms"] = session.TakeMaxUdpSendMilliseconds(),
					});
					nextDiagnosticAt += TimeSpan.FromSeconds(5);
				}
			}
		}
		finally
		{
			(encoder as IDisposable)?.Dispose();
		}
	}

	private static IOpusEncoder CreateEncoder(int bitrate, bool useInbandFec)
	{
		var encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO, TextWriter.Null);
		encoder.Bitrate = bitrate;
		encoder.Complexity = 5;
		encoder.UseVBR = true;
		encoder.UseConstrainedVBR = true;
		encoder.UseInbandFEC = useInbandFec;
		encoder.PacketLossPercent = useInbandFec ? 10 : 0;
		return encoder;
	}

	private static int EncodePacket(
		IOpusEncoder encoder,
		ReadOnlySpan<short> pcm,
		int frameSamplesPerChannel,
		byte[] packetBuffer)
	{
		if (pcm.Length != frameSamplesPerChannel * Channels)
		{
			throw new InvalidOperationException($"PCM frame length {pcm.Length} does not match Opus frame size {frameSamplesPerChannel * Channels}.");
		}

		var encodedLength = encoder.Encode(pcm, frameSamplesPerChannel, packetBuffer.AsSpan(), packetBuffer.Length);
		if (encodedLength <= 0)
		{
			throw new InvalidOperationException($"Opus encode failed with code {encodedLength}.");
		}

		return encodedLength;
	}

	private static int FrameSamplesPerChannel(int opusFrameMilliseconds)
	{
		return opusFrameMilliseconds switch
		{
			5 or 10 or 20 => SampleRate * opusFrameMilliseconds / 1000,
			_ => throw new ArgumentOutOfRangeException(nameof(opusFrameMilliseconds), "Opus frame size must be 5, 10, or 20 ms."),
		};
	}
}
