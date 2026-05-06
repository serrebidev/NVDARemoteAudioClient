using System.Diagnostics;
using System.Threading.Channels;
using Concentus.Enums;
using Concentus.Structs;

namespace NVDARemoteAudioHelper;

internal static class AudioPublisher
{
	private const int SampleRate = 48000;
	private const int Channels = 2;
	private const int OpusFrameSamplesPerChannel = 240;
	private const int OpusFramesPerPacket = 3;
	private const int PacketSamplesPerChannel = OpusFrameSamplesPerChannel * OpusFramesPerPacket;
	private const int PacketShorts = PacketSamplesPerChannel * Channels;
	private const int PacketMilliseconds = 15;

	public static async Task RunCaptureAsync(RemoteAudioSession session, int excludePid, int bitrate, CancellationToken cancellationToken)
	{
		JsonLog.Write("status", "Starting system capture with NVDA audio excluded.", new Dictionary<string, object?>
		{
			["excluded_pid"] = excludePid,
			["bitrate"] = bitrate,
		});

		var channel = Channel.CreateBounded<short[]>(new BoundedChannelOptions(16)
		{
			SingleReader = true,
			SingleWriter = true,
			FullMode = BoundedChannelFullMode.DropOldest,
		});

		var capture = new ProcessLoopbackCapture(excludePid);
		var captureTask = capture.RunAsync(channel.Writer, cancellationToken);
		var encodeTask = EncodeAndSendLoopAsync(channel.Reader, session, bitrate, cancellationToken);

		var completed = await Task.WhenAny(captureTask, encodeTask);
		await completed;
	}

	public static async Task RunTestToneAsync(RemoteAudioSession session, int bitrate, CancellationToken cancellationToken)
	{
		JsonLog.Write("status", "Sending generated test tone.", new Dictionary<string, object?>
		{
			["bitrate"] = bitrate,
		});

		var encoder = CreateEncoder(bitrate);
		var packetizer = new OpusRepacketizer();
		var opusBuffer = new byte[session.MaxPayloadBytes];
		var encodedFrames = CreateEncodedFrameBuffers(session.MaxPayloadBytes);
		var frame = new short[PacketShorts];
		var sequence = 0UL;
		var phase = 0.0;
		var phaseStep = 2.0 * Math.PI * 440.0 / SampleRate;
		var start = Stopwatch.StartNew();
		var nextFrameAt = TimeSpan.Zero;

		while (!cancellationToken.IsCancellationRequested)
		{
			for (var i = 0; i < PacketSamplesPerChannel; i++)
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

			var encodedLength = EncodePacket(encoder, packetizer, frame, encodedFrames, opusBuffer);
			await session.SendAudioAsync(sequence++, (ulong)start.ElapsedMilliseconds, opusBuffer.AsMemory(0, encodedLength), cancellationToken);

			nextFrameAt += TimeSpan.FromMilliseconds(PacketMilliseconds);
			var delay = nextFrameAt - start.Elapsed;
			if (delay > TimeSpan.Zero)
			{
				await Task.Delay(delay, cancellationToken);
			}
		}
	}

	private static async Task EncodeAndSendLoopAsync(
		ChannelReader<short[]> frames,
		RemoteAudioSession session,
		int bitrate,
		CancellationToken cancellationToken)
	{
		var encoder = CreateEncoder(bitrate);
		var packetizer = new OpusRepacketizer();
		var opusBuffer = new byte[session.MaxPayloadBytes];
		var encodedFrames = CreateEncodedFrameBuffers(session.MaxPayloadBytes);
		var sequence = 0UL;
		var start = Stopwatch.StartNew();

		await foreach (var frame in frames.ReadAllAsync(cancellationToken))
		{
			var encodedLength = EncodePacket(encoder, packetizer, frame, encodedFrames, opusBuffer);
			await session.SendAudioAsync(sequence++, (ulong)start.ElapsedMilliseconds, opusBuffer.AsMemory(0, encodedLength), cancellationToken);
		}
	}

	private static OpusEncoder CreateEncoder(int bitrate)
	{
		return new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO)
		{
			Bitrate = bitrate,
			Complexity = 5,
			UseVBR = true,
			UseConstrainedVBR = true,
		};
	}

	private static byte[][] CreateEncodedFrameBuffers(int maxPayloadBytes)
	{
		var buffers = new byte[OpusFramesPerPacket][];
		for (var i = 0; i < buffers.Length; i++)
		{
			buffers[i] = new byte[maxPayloadBytes];
		}

		return buffers;
	}

	private static int EncodePacket(
		OpusEncoder encoder,
		OpusRepacketizer packetizer,
		short[] pcm,
		byte[][] encodedFrames,
		byte[] packetBuffer)
	{
		packetizer.Reset();
		for (var i = 0; i < OpusFramesPerPacket; i++)
		{
			var frame = pcm.AsSpan(i * OpusFrameSamplesPerChannel * Channels, OpusFrameSamplesPerChannel * Channels);
			var encodedLength = encoder.Encode(frame, OpusFrameSamplesPerChannel, encodedFrames[i].AsSpan(), encodedFrames[i].Length);
			var result = packetizer.AddPacket(encodedFrames[i].AsSpan(), 0, encodedLength);
			if (result < 0)
			{
				throw new InvalidOperationException($"Opus repacketizer rejected a 5 ms frame with code {result}.");
			}
		}

		var packetLength = packetizer.CreatePacket(packetBuffer, 0, packetBuffer.Length);
		if (packetLength < 0)
		{
			throw new InvalidOperationException($"Opus repacketizer failed with code {packetLength}.");
		}

		return packetLength;
	}
}
