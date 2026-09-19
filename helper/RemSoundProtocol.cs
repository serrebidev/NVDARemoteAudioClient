using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NVDARemoteAudioHelper;

/// <summary>
/// The RemSound v3 wire protocol, spoken by the Windows RemSound app, its send-only
/// service, the iPhone and Mac app, and the Android receiver. The Windows sources in
/// https://github.com/Ednunp/RemSound (src/RemSound.Core) are the specification;
/// everything here is a byte-for-byte port of the parts a peer has to agree on.
///
/// Every packet starts with a 12-byte little-endian header: magic 'RMND', version 1,
/// type, stream ID, sequence. Audio, heartbeats and remote control share one UDP port
/// (47830); discovery announcements travel as JSON on 47821.
/// </summary>
internal enum RemPacketType : byte
{
	Format = 1,
	Audio = 2,
	KeepAlive = 3,
	Heartbeat = 4,
	Control = 5,
	// 6-9 are the relay's v2 lobby messages, which no client port emits.
	AddrCheck = 10,
}

internal enum RemHeartbeatKind : byte
{
	Ping = 0,
	Pong = 1,
}

/// <summary>What a remote-control packet asks the receiving computer to do.</summary>
internal enum RemControlKind : byte
{
	VolumeUp = 0,
	VolumeDown = 1,
	MuteToggle = 2,
	SystemVolumeUp = 3,
	SystemVolumeDown = 4,
	SystemMuteToggle = 5,
}

internal enum RemCodec
{
	Pcm = 1,
	Opus = 2,
}

internal sealed record RemFormat(
	int SampleRate,
	int Channels,
	int BitsPerSample,
	int Encoding,
	int BlockAlign,
	int AverageBytesPerSecond,
	int Codec,
	int FrameSamplesPerChannel,
	byte Lane = 0,
	double CaptureLatencyMs = 0)
{
	/// <summary>Largest frame Opus can carry at 48 kHz (60 ms).</summary>
	public const int MaxFrameSamplesPerChannel = 2880;

	/// <summary>
	/// A Format packet is the one packet that is not authenticated, so every field
	/// is checked before anything is sized from it. Mirrors RemSound's own
	/// AudioFormatInfo.IsUsable: it rejects only values no port has ever sent.
	/// </summary>
	public bool IsUsable(out string reason)
	{
		reason = "";
		if (Channels is < 1 or > 2)
		{
			reason = $"channel count {Channels}";
			return false;
		}
		if (SampleRate is < 8000 or > 192000)
		{
			reason = $"sample rate {SampleRate}";
			return false;
		}
		if (FrameSamplesPerChannel is < 1 or > MaxFrameSamplesPerChannel)
		{
			reason = $"frame size {FrameSamplesPerChannel}";
			return false;
		}
		if (Codec is not ((int)RemCodec.Pcm or (int)RemCodec.Opus))
		{
			reason = $"unknown codec {Codec}";
			return false;
		}
		if (Codec == (int)RemCodec.Opus && SampleRate is not (8000 or 12000 or 16000 or 24000 or 48000))
		{
			reason = $"Opus at {SampleRate} Hz";
			return false;
		}
		return true;
	}
}

internal static class RemPacket
{
	public const int HeaderSize = 12;
	public const int Magic = 0x444E4D52; // 'RMND' little-endian
	public const byte Version = 1;
	public const int DefaultPort = 47830;
	public const int DiscoveryPort = 47821;
	public const int FormatPayloadSize = 32;
	public const int FormatPayloadExtendedSize = 36;
	public const int FormatPayloadWithFingerprintSize = 44;
	public const int FormatPayloadWithCaptureSize = 46;
	public const int PasswordFingerprintSize = 8;
	public const int HeartbeatPayloadSize = 9;
	public const int ControlPayloadSize = 2;
	/// <summary>Largest audio payload that fits one Ethernet datagram without IP fragmentation.</summary>
	public const int MaxAudioPayloadBytes = 1454;
	/// <summary>RemSound marks its heartbeats with this stream ID.</summary>
	public const ushort HeartbeatStreamId = 0xFFFF;

	public static int WriteHeader(Span<byte> destination, RemPacketType type, ushort streamId, uint sequence)
	{
		BinaryPrimitives.WriteInt32LittleEndian(destination, Magic);
		destination[4] = Version;
		destination[5] = (byte)type;
		BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], streamId == 0 ? (ushort)1 : streamId);
		BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], sequence);
		return HeaderSize;
	}

	public static bool TryReadHeader(ReadOnlySpan<byte> packet, out RemPacketType type, out ushort streamId, out uint sequence)
	{
		type = default;
		streamId = 0;
		sequence = 0;
		if (packet.Length < HeaderSize || BinaryPrimitives.ReadInt32LittleEndian(packet) != Magic || packet[4] != Version)
		{
			return false;
		}
		type = (RemPacketType)packet[5];
		streamId = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
		if (streamId == 0)
		{
			streamId = 1;
		}
		sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
		return true;
	}

	/// <summary>
	/// Writes the 46-byte format payload: the 32 original fields, the lane byte and
	/// three reserved zeros, the password fingerprint, and the capture latency in
	/// tenths of a millisecond. Readers take a minimum length, so every port parses it.
	/// </summary>
	public static int WriteFormatPayload(Span<byte> destination, RemFormat format, ReadOnlySpan<byte> fingerprint)
	{
		BinaryPrimitives.WriteInt32LittleEndian(destination, format.SampleRate);
		BinaryPrimitives.WriteInt32LittleEndian(destination[4..], format.Channels);
		BinaryPrimitives.WriteInt32LittleEndian(destination[8..], format.BitsPerSample);
		BinaryPrimitives.WriteInt32LittleEndian(destination[12..], format.Encoding);
		BinaryPrimitives.WriteInt32LittleEndian(destination[16..], format.BlockAlign);
		BinaryPrimitives.WriteInt32LittleEndian(destination[20..], format.AverageBytesPerSecond);
		BinaryPrimitives.WriteInt32LittleEndian(destination[24..], format.Codec);
		BinaryPrimitives.WriteInt32LittleEndian(destination[28..], format.FrameSamplesPerChannel);
		destination[32] = format.Lane;
		destination[33] = 0;
		destination[34] = 0;
		destination[35] = 0;
		if (fingerprint.Length != PasswordFingerprintSize)
		{
			return FormatPayloadExtendedSize;
		}
		fingerprint.CopyTo(destination.Slice(36, PasswordFingerprintSize));
		var ticks = format.CaptureLatencyMs * 10.0;
		var clamped = ticks <= 0 ? (ushort)0 : ticks >= ushort.MaxValue ? ushort.MaxValue : (ushort)Math.Round(ticks);
		BinaryPrimitives.WriteUInt16LittleEndian(destination[44..], clamped);
		return FormatPayloadWithCaptureSize;
	}

	/// <summary>
	/// Reads a format payload of any length from 32 bytes up. The fingerprint is null
	/// when the sender predates encryption, which RemSound reads as "that peer needs
	/// to update".
	/// </summary>
	public static bool TryReadFormat(ReadOnlySpan<byte> payload, out RemFormat format, out byte[]? fingerprint)
	{
		format = new RemFormat(48000, 2, 16, 1, 4, 192000, (int)RemCodec.Opus, 480);
		fingerprint = null;
		if (payload.Length < FormatPayloadSize)
		{
			return false;
		}
		if (payload.Length >= FormatPayloadWithFingerprintSize)
		{
			fingerprint = payload.Slice(36, PasswordFingerprintSize).ToArray();
		}
		var captureLatencyMs = payload.Length >= FormatPayloadWithCaptureSize
			? BinaryPrimitives.ReadUInt16LittleEndian(payload[44..]) / 10.0
			: 0.0;
		// Unknown lanes play in the default route rather than being dropped.
		var lane = payload.Length >= FormatPayloadExtendedSize && payload[32] <= 2 ? payload[32] : (byte)0;
		format = new RemFormat(
			BinaryPrimitives.ReadInt32LittleEndian(payload),
			BinaryPrimitives.ReadInt32LittleEndian(payload[4..]),
			BinaryPrimitives.ReadInt32LittleEndian(payload[8..]),
			BinaryPrimitives.ReadInt32LittleEndian(payload[12..]),
			BinaryPrimitives.ReadInt32LittleEndian(payload[16..]),
			BinaryPrimitives.ReadInt32LittleEndian(payload[20..]),
			BinaryPrimitives.ReadInt32LittleEndian(payload[24..]),
			BinaryPrimitives.ReadInt32LittleEndian(payload[28..]),
			lane,
			captureLatencyMs);
		return true;
	}

	public static byte[] BuildHeartbeat(RemHeartbeatKind kind, uint sequence, long originatorTickMs)
	{
		var packet = new byte[HeaderSize + HeartbeatPayloadSize];
		WriteHeader(packet, RemPacketType.Heartbeat, HeartbeatStreamId, sequence);
		packet[HeaderSize] = (byte)kind;
		BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(HeaderSize + 1), originatorTickMs);
		return packet;
	}

	public static bool TryReadHeartbeat(ReadOnlySpan<byte> payload, out RemHeartbeatKind kind, out long originatorTickMs)
	{
		kind = RemHeartbeatKind.Ping;
		originatorTickMs = 0;
		if (payload.Length < HeartbeatPayloadSize || payload[0] > (byte)RemHeartbeatKind.Pong)
		{
			return false;
		}
		kind = (RemHeartbeatKind)payload[0];
		originatorTickMs = BinaryPrimitives.ReadInt64LittleEndian(payload[1..]);
		return true;
	}

	public static bool TryReadControl(ReadOnlySpan<byte> plaintext, out RemControlKind kind, out sbyte delta)
	{
		kind = RemControlKind.VolumeUp;
		delta = 0;
		// Unknown commands are refused, not guessed at, so a newer peer's command
		// can never be misread as one of ours.
		if (plaintext.Length < ControlPayloadSize || plaintext[0] > (byte)RemControlKind.SystemMuteToggle)
		{
			return false;
		}
		kind = (RemControlKind)plaintext[0];
		delta = unchecked((sbyte)plaintext[1]);
		return true;
	}
}

/// <summary>
/// PCM frames are encrypted whole and then split into parts of at most
/// <see cref="RemPacket.MaxAudioPayloadBytes"/>, each carrying this 6-byte
/// sub-header: uint32 frame ID, part index, part count.
/// </summary>
internal static class RemPcmFrame
{
	public const int SubHeaderSize = 6;

	public static void WriteSubHeader(Span<byte> destination, uint frameId, byte partIndex, byte totalParts)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(destination, frameId);
		destination[4] = partIndex;
		destination[5] = totalParts;
	}

	public static bool TryReadSubHeader(ReadOnlySpan<byte> source, out uint frameId, out byte partIndex, out byte totalParts)
	{
		frameId = 0;
		partIndex = 0;
		totalParts = 0;
		if (source.Length < SubHeaderSize)
		{
			return false;
		}
		frameId = BinaryPrimitives.ReadUInt32LittleEndian(source);
		partIndex = source[4];
		totalParts = source[5];
		return totalParts > 0 && partIndex < totalParts;
	}

	/// <summary>Packs float samples into signed 24-bit little-endian PCM, RemSound's wire format.</summary>
	public static void FloatToInt24(ReadOnlySpan<float> source, Span<byte> destination)
	{
		for (int i = 0, j = 0; i < source.Length; i++, j += 3)
		{
			var sample = (int)(Math.Clamp(source[i], -1f, 1f) * 8388607f);
			destination[j] = (byte)sample;
			destination[j + 1] = (byte)(sample >> 8);
			destination[j + 2] = (byte)(sample >> 16);
		}
	}

	public static void Int24ToFloat(ReadOnlySpan<byte> source, Span<float> destination)
	{
		var count = source.Length / 3;
		for (int i = 0, j = 0; i < count; i++, j += 3)
		{
			var packed = source[j] | (source[j + 1] << 8) | (source[j + 2] << 16);
			destination[i] = ((packed << 8) >> 8) / 8388607f;
		}
	}
}

/// <summary>
/// Reassembles a PCM frame from its parts. Parts arrive in order on a healthy path;
/// a missing or out-of-order part drops the whole frame, exactly as every other port
/// does, because a PCM frame is all-or-nothing once encrypted.
/// </summary>
internal sealed class RemPcmAssembler
{
	private readonly byte[] _buffer = new byte[64 * 1024];
	private uint _frameId;
	private int _nextPart;
	private int _totalParts;
	private int _written;

	public bool TryAdd(ReadOnlySpan<byte> part, uint frameId, byte partIndex, byte totalParts, out ReadOnlySpan<byte> frame)
	{
		frame = default;
		if (partIndex == 0)
		{
			_frameId = frameId;
			_nextPart = 0;
			_totalParts = totalParts;
			_written = 0;
		}
		else if (frameId != _frameId || partIndex != _nextPart || totalParts != _totalParts)
		{
			_totalParts = 0;
			_written = 0;
			return false;
		}
		if (_totalParts == 0 || _written + part.Length > _buffer.Length)
		{
			_totalParts = 0;
			_written = 0;
			return false;
		}
		part.CopyTo(_buffer.AsSpan(_written));
		_written += part.Length;
		_nextPart++;
		if (_nextPart < _totalParts)
		{
			return false;
		}
		frame = _buffer.AsSpan(0, _written);
		_totalParts = 0;
		_written = 0;
		return true;
	}
}

/// <summary>
/// RemSound's always-on encryption. The key and the fingerprint both come from the
/// shared password through PBKDF2-HMAC-SHA256 at 100 000 iterations with fixed salts.
/// The iteration count is a cross-port contract: RemSound 5.6 briefly raised it and
/// every iPhone went silent, so it must never change on its own.
/// Packets are laid out nonce(12) || tag(16) || ciphertext.
/// </summary>
internal static class RemCrypto
{
	public const int Pbkdf2Iterations = 100_000;
	public const int KeyBytes = 32;
	public const int NonceBytes = 12;
	public const int TagBytes = 16;
	public const int OverheadBytes = NonceBytes + TagBytes;
	private static readonly byte[] KeySalt = Encoding.UTF8.GetBytes("RemSound.v1.audio-key");
	private static readonly byte[] FingerprintSalt = Encoding.UTF8.GetBytes("RemSound.v1.fingerprint");

	public static byte[] DeriveKey(string password) =>
		Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), KeySalt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);

	public static byte[] Fingerprint(string password) =>
		Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), FingerprintSalt, Pbkdf2Iterations, HashAlgorithmName.SHA256, RemPacket.PasswordFingerprintSize);

	public static bool FingerprintsEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
		CryptographicOperations.FixedTimeEquals(a, b);

	public static int EncryptInto(AesGcm gcm, RemNonceSequence nonces, ReadOnlySpan<byte> plaintext, Span<byte> destination)
	{
		var total = plaintext.Length + OverheadBytes;
		nonces.FillNext(destination[..NonceBytes]);
		gcm.Encrypt(
			destination[..NonceBytes],
			plaintext,
			destination.Slice(OverheadBytes, plaintext.Length),
			destination.Slice(NonceBytes, TagBytes));
		return total;
	}

	public static bool TryDecryptInto(AesGcm gcm, ReadOnlySpan<byte> packet, Span<byte> destination, out int written)
	{
		written = 0;
		if (packet.Length < OverheadBytes || destination.Length < packet.Length - OverheadBytes)
		{
			return false;
		}
		var length = packet.Length - OverheadBytes;
		try
		{
			gcm.Decrypt(packet[..NonceBytes], packet.Slice(OverheadBytes, length), packet.Slice(NonceBytes, TagBytes), destination[..length]);
			written = length;
			return true;
		}
		catch (CryptographicException)
		{
			return false;
		}
	}
}

/// <summary>
/// A random 48-bit prefix per instance and a 48-bit counter: nonces cannot repeat
/// within one stream by arithmetic, and the prefix keeps separate streams under the
/// same long-lived key apart. The same scheme RemSound's sender uses.
/// </summary>
internal sealed class RemNonceSequence
{
	private const int PrefixBytes = 6;
	private readonly byte[] _prefix = new byte[PrefixBytes];
	private ulong _counter;

	public RemNonceSequence() => RandomNumberGenerator.Fill(_prefix);

	public void FillNext(Span<byte> nonce)
	{
		_prefix.CopyTo(nonce);
		var value = _counter++;
		for (var i = 0; i < RemCrypto.NonceBytes - PrefixBytes; i++)
		{
			nonce[PrefixBytes + i] = (byte)(value >> (8 * i));
		}
	}
}

/// <summary>
/// Remote-control commands are sealed with the audio key, so only someone who knows
/// the password can change this computer's volume. The sealed plaintext is the
/// command, the delta, and the sender's Unix time; the receiver refuses anything
/// outside a ten-minute window and anything it has already accepted.
/// </summary>
internal static class RemControlSealing
{
	private const int PlainBytes = 10;
	public const int SealedPayloadBytes = PlainBytes + RemCrypto.OverheadBytes;

	public static byte[] Seal(byte[] key, RemControlKind kind, sbyte delta, long unixSeconds)
	{
		Span<byte> plain = stackalloc byte[PlainBytes];
		plain[0] = (byte)kind;
		plain[1] = unchecked((byte)delta);
		BinaryPrimitives.WriteInt64LittleEndian(plain[2..], unixSeconds);
		var sealedPayload = new byte[SealedPayloadBytes];
		var nonce = sealedPayload.AsSpan(0, RemCrypto.NonceBytes);
		RandomNumberGenerator.Fill(nonce);
		using var gcm = new AesGcm(key, RemCrypto.TagBytes);
		gcm.Encrypt(nonce, plain, sealedPayload.AsSpan(RemCrypto.OverheadBytes), sealedPayload.AsSpan(RemCrypto.NonceBytes, RemCrypto.TagBytes));
		return sealedPayload;
	}

	public static bool TryUnseal(AesGcm gcm, ReadOnlySpan<byte> payload, out RemControlKind kind, out sbyte delta, out long unixSeconds, out ulong nonceId)
	{
		kind = RemControlKind.VolumeUp;
		delta = 0;
		unixSeconds = 0;
		nonceId = 0;
		if (payload.Length != SealedPayloadBytes)
		{
			return false;
		}
		Span<byte> plain = stackalloc byte[PlainBytes];
		if (!RemCrypto.TryDecryptInto(gcm, payload, plain, out var written) || written != PlainBytes ||
			!RemPacket.TryReadControl(plain, out kind, out delta))
		{
			return false;
		}
		unixSeconds = BinaryPrimitives.ReadInt64LittleEndian(plain[2..]);
		nonceId = BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]);
		return true;
	}
}

internal sealed class RemControlGuard
{
	public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(10);
	private readonly Dictionary<ulong, DateTime> _seen = [];

	public bool TryAccept(AesGcm gcm, ReadOnlySpan<byte> payload, DateTime utcNow, out RemControlKind kind, out sbyte delta, out string rejectReason)
	{
		if (!RemControlSealing.TryUnseal(gcm, payload, out kind, out delta, out var unixSeconds, out var nonceId))
		{
			rejectReason = "not sealed with this password";
			return false;
		}
		var age = utcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
		if (age > MaxSkew || age < -MaxSkew)
		{
			rejectReason = "stale";
			return false;
		}
		if (_seen.ContainsKey(nonceId))
		{
			rejectReason = "replayed";
			return false;
		}
		if (_seen.Count >= 256)
		{
			foreach (var stale in _seen.Where(pair => utcNow - pair.Value > MaxSkew + MaxSkew).Select(pair => pair.Key).ToList())
			{
				_seen.Remove(stale);
			}
		}
		_seen[nonceId] = utcNow;
		rejectReason = "";
		return true;
	}
}
