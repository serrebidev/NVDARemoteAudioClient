using System.Diagnostics;
using System.Security.Cryptography;
using Concentus;
using Concentus.Enums;

namespace NVDARemoteAudioHelper;

/// <summary>
/// Sends captured audio to RemSound peers. Opus packets are encrypted one per
/// datagram. PCM goes out as signed 24-bit, 2.5 ms frames: each encrypted frame is
/// 748 bytes and so still fits one datagram, which matters because a PCM frame with
/// one part missing is dropped whole. A Format packet repeats every 250 ms, so a
/// receiver that starts late, or loses one, picks the stream up within a quarter of
/// a second.
/// </summary>
internal sealed class RemSoundSender : IDisposable
{
	private const int SampleRate = 48000;
	private const int Channels = 2;
	/// <summary>RemSound's "Tight" PCM frame: 120 samples per channel, 2.5 ms.</summary>
	public const int PcmFrameSamples = 120;
	private static readonly TimeSpan FormatInterval = TimeSpan.FromMilliseconds(250);

	private readonly RemSoundLink _link;
	private readonly AudioPayloadCodec _codec;
	private readonly int _frameSamples;
	private readonly IOpusEncoder? _encoder;
	private readonly AesGcm _gcm;
	private readonly RemNonceSequence _nonces = new();
	private readonly ushort _streamId;
	private readonly byte[] _formatPacket;
	private readonly byte[] _plain = new byte[8192];
	private readonly byte[] _packet = new byte[8192];
	private readonly byte[] _sealed = new byte[8192];
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private TimeSpan _nextFormat = TimeSpan.Zero;
	private uint _audioSequence;
	private uint _formatSequence;
	private uint _pcmFrameId;
	private long _packetsSent;

	public RemSoundSender(RemSoundLink link, AudioPayloadCodec codec, int opusFrameMilliseconds, int bitrate, bool useInbandFec)
	{
		_link = link;
		_codec = codec;
		_frameSamples = codec == AudioPayloadCodec.Opus ? SampleRate * opusFrameMilliseconds / 1000 : PcmFrameSamples;
		if (codec == AudioPayloadCodec.Opus)
		{
			// The settings RemSound's own sender uses: restricted low delay, full
			// complexity, and in-band FEC tuned for a little loss.
			_encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY, TextWriter.Null);
			_encoder.Bitrate = bitrate;
			_encoder.Complexity = 10;
			_encoder.UseVBR = true;
			_encoder.UseInbandFEC = useInbandFec;
			_encoder.PacketLossPercent = useInbandFec ? 10 : 0;
		}
		_gcm = new AesGcm(link.Key, RemCrypto.TagBytes);
		// Receivers key a stream on its source and this ID, so every run gets a new one.
		_streamId = (ushort)RandomNumberGenerator.GetInt32(1, 0xFFFF);
		var format = Format;
		_formatPacket = new byte[RemPacket.HeaderSize + RemPacket.FormatPayloadWithCaptureSize];
		RemPacket.WriteFormatPayload(_formatPacket.AsSpan(RemPacket.HeaderSize), format, link.Fingerprint);
	}

	public RemFormat Format => _codec == AudioPayloadCodec.Opus
		? new RemFormat(SampleRate, Channels, 16, 1, 4, 192_000, (int)RemCodec.Opus, _frameSamples, CaptureLatencyMs: 10)
		: new RemFormat(SampleRate, Channels, 24, 1, 6, 288_000, (int)RemCodec.Pcm, _frameSamples, CaptureLatencyMs: 10);

	public long PacketsSent => Interlocked.Read(ref _packetsSent);

	public async Task RunAsync(AudioFrameQueue frames, CancellationToken cancellationToken)
	{
		JsonLog.Write("status", "Sending to RemSound devices.", new Dictionary<string, object?>
		{
			["codec"] = _codec == AudioPayloadCodec.Opus ? "opus" : "pcm24",
			["frame_samples"] = _frameSamples,
			["stream_id"] = _streamId,
		});
		var nextDiagnostic = TimeSpan.FromSeconds(5);
		await foreach (var frame in frames.ReadAllAsync(cancellationToken))
		{
			try
			{
				SendFrame(frame.ReadOnlySpan);
			}
			finally
			{
				frame.Dispose();
			}
			if (_clock.Elapsed >= nextDiagnostic)
			{
				nextDiagnostic += TimeSpan.FromSeconds(5);
				JsonLog.Write("diagnostic", "RemSound sender statistics.", new Dictionary<string, object?>
				{
					["packets_sent"] = PacketsSent,
					["targets"] = _link.Targets.Select(target => target.ToString()).ToList(),
					["capture_queue_drops"] = frames.DroppedFrames,
					["send_errors"] = _link.SendErrors,
					["peers"] = _link.HealthSnapshot(),
				});
			}
		}
	}

	/// <summary>Sends one captured frame of interleaved stereo 16-bit samples.</summary>
	public void SendFrame(ReadOnlySpan<short> pcm)
	{
		if (_link.Targets.Count == 0)
		{
			return;
		}
		SendFormatIfDue();
		if (_codec == AudioPayloadCodec.Opus)
		{
			SendOpus(pcm);
			return;
		}
		// Captured frames are 5 ms or longer; PCM goes out in 2.5 ms slices.
		var sliceLength = PcmFrameSamples * Channels;
		for (var offset = 0; offset + sliceLength <= pcm.Length; offset += sliceLength)
		{
			SendPcm(pcm.Slice(offset, sliceLength));
		}
	}

	private void SendFormatIfDue()
	{
		var now = _clock.Elapsed;
		if (now < _nextFormat)
		{
			return;
		}
		_nextFormat = now + FormatInterval;
		RemPacket.WriteHeader(_formatPacket, RemPacketType.Format, _streamId, _formatSequence++);
		_link.SendToTargets(_formatPacket);
	}

	private void SendOpus(ReadOnlySpan<short> pcm)
	{
		var length = _encoder!.Encode(pcm, _frameSamples, _plain.AsSpan(), _plain.Length);
		if (length <= 0)
		{
			throw new InvalidOperationException($"Opus encode failed with code {length}.");
		}
		var written = RemCrypto.EncryptInto(_gcm, _nonces, _plain.AsSpan(0, length), _packet.AsSpan(RemPacket.HeaderSize));
		RemPacket.WriteHeader(_packet, RemPacketType.Audio, _streamId, _audioSequence++);
		_link.SendToTargets(_packet.AsSpan(0, RemPacket.HeaderSize + written));
		Interlocked.Increment(ref _packetsSent);
	}

	private void SendPcm(ReadOnlySpan<short> pcm)
	{
		// 16-bit samples sit in the top two bytes of a 24-bit sample.
		var plainLength = pcm.Length * 3;
		for (var i = 0; i < pcm.Length; i++)
		{
			var sample = pcm[i];
			_plain[i * 3] = 0;
			_plain[(i * 3) + 1] = (byte)sample;
			_plain[(i * 3) + 2] = (byte)(sample >> 8);
		}
		var sealedLength = RemCrypto.EncryptInto(_gcm, _nonces, _plain.AsSpan(0, plainLength), _sealed);
		var parts = (sealedLength + RemPacket.MaxAudioPayloadBytes - 1) / RemPacket.MaxAudioPayloadBytes;
		var frameId = _pcmFrameId++;
		for (var part = 0; part < parts; part++)
		{
			var offset = part * RemPacket.MaxAudioPayloadBytes;
			var partLength = Math.Min(RemPacket.MaxAudioPayloadBytes, sealedLength - offset);
			RemPacket.WriteHeader(_packet, RemPacketType.Audio, _streamId, _audioSequence++);
			RemPcmFrame.WriteSubHeader(_packet.AsSpan(RemPacket.HeaderSize), frameId, (byte)part, (byte)parts);
			_sealed.AsSpan(offset, partLength).CopyTo(_packet.AsSpan(RemPacket.HeaderSize + RemPcmFrame.SubHeaderSize));
			_link.SendToTargets(_packet.AsSpan(0, RemPacket.HeaderSize + RemPcmFrame.SubHeaderSize + partLength));
			Interlocked.Increment(ref _packetsSent);
		}
	}

	public void Dispose()
	{
		(_encoder as IDisposable)?.Dispose();
		_gcm.Dispose();
	}
}
