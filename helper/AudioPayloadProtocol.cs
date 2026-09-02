using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NVDARemoteAudioHelper;

internal enum AudioPayloadCodec : byte
{
	Opus = 1,
	Pcm16 = 2,
}

internal readonly record struct DecodedAudioPayload(AudioPayloadCodec Codec, int FrameMilliseconds, int Length, bool Legacy, bool Encrypted);

/// <summary>
/// Why a payload could not be turned into audio. The audio path is one-way UDP
/// with no peer handshake, so the envelope's version byte is the only thing that
/// can tell an out-of-date add-on apart from a wrong password. Dropping both
/// silently leaves the user with no audio and no way to tell which machine to
/// look at, so the two are reported separately all the way to the user.
/// </summary>
internal enum AudioPayloadDecodeStatus
{
	/// <summary>Decoded into usable audio.</summary>
	Ok,

	/// <summary>Truncated or mis-sized. Assume corruption in transit.</summary>
	Malformed,

	/// <summary>
	/// AES-GCM rejected the packet, or the publisher encrypted while this
	/// receiver has no password: the two ends disagree about the password.
	/// </summary>
	AuthenticationFailed,

	/// <summary>The publisher speaks a newer payload version than this build. Update this receiver.</summary>
	PeerTooNew,

	/// <summary>The publisher speaks a version older than this build accepts. Update the publisher.</summary>
	PeerTooOld,
}

/// <summary>
/// End-to-end audio payload envelope. The relay forwards these bytes without
/// understanding them, so legacy relay servers remain compatible.
/// </summary>
internal sealed class AudioPayloadProtocol : IDisposable
{
	private static ReadOnlySpan<byte> Magic => "RAE2"u8;
	private const byte Version = 2;
	// The oldest payload version this build still decodes. Versioned independently
	// of the add-on's release number: what the two machines have to agree on is
	// this wire contract, not the version printed in the manifest.
	//
	// Move these ONLY for a breaking change -- a redefined header field, a changed
	// nonce or AAD construction, a new framing. An additive change must not move
	// them, because a receiver ignores header bits it does not recognise rather
	// than failing. The exception is the codec byte: a stream in a codec this build
	// cannot decode is unusable however tolerant the parser is, so an unknown codec
	// is reported as a newer publisher rather than as corruption.
	private const byte MinSupportedVersion = 2;
	private const byte EncryptedFlag = 0x01;
	private const int HeaderLength = 20;
	private const int NonceOffset = 8;
	private const int NonceLength = 12;
	private const int TagLength = 16;
	private const int KeyLength = 32;
	private const int Pbkdf2Iterations = 200_000;

	private readonly byte[]? _key;
	private readonly byte[] _sendNonceBase = new byte[NonceLength];
	private readonly AesGcm? _aes;

	public AudioPayloadProtocol(string password, string roomKey)
	{
		if (string.IsNullOrEmpty(password))
		{
			return;
		}

		_key = DeriveKey(password, roomKey);
		_aes = new AesGcm(_key, TagLength);
		RandomNumberGenerator.Fill(_sendNonceBase);
	}

	public bool EncryptionEnabled => _aes is not null;
	public int OverheadBytes => HeaderLength + (EncryptionEnabled ? TagLength : 0);

	public int Encode(
		AudioPayloadCodec codec,
		int frameMilliseconds,
		ulong sequence,
		ulong timestampMilliseconds,
		ReadOnlySpan<byte> plaintext,
		Span<byte> destination)
	{
		var total = HeaderLength + plaintext.Length + (EncryptionEnabled ? TagLength : 0);
		if (destination.Length < total)
		{
			throw new ArgumentException("Audio payload destination is too small.", nameof(destination));
		}

		Magic.CopyTo(destination);
		destination[4] = Version;
		destination[5] = EncryptionEnabled ? EncryptedFlag : (byte)0;
		destination[6] = (byte)codec;
		destination[7] = checked((byte)frameMilliseconds);
		_sendNonceBase.CopyTo(destination[NonceOffset..]);

		if (_aes is null)
		{
			plaintext.CopyTo(destination[HeaderLength..]);
			return total;
		}

		Span<byte> nonce = stackalloc byte[NonceLength];
		CreateNonce(_sendNonceBase, sequence, nonce);
		Span<byte> associatedData = stackalloc byte[HeaderLength + 16];
		destination[..HeaderLength].CopyTo(associatedData);
		BinaryPrimitives.WriteUInt64BigEndian(associatedData[HeaderLength..], sequence);
		BinaryPrimitives.WriteUInt64BigEndian(associatedData[(HeaderLength + 8)..], timestampMilliseconds);
		var cipher = destination.Slice(HeaderLength, plaintext.Length);
		var tag = destination.Slice(HeaderLength + plaintext.Length, TagLength);
		_aes.Encrypt(nonce, plaintext, cipher, tag, associatedData);
		return total;
	}

	/// <summary>The payload version this build writes.</summary>
	public static int CurrentVersion => Version;

	/// <summary>The oldest payload version this build still reads.</summary>
	public static int OldestSupportedVersion => MinSupportedVersion;

	/// <summary>
	/// The payload version a packet claims, or 0 when it carries no envelope at all
	/// (a pre-0.2.0 publisher sending bare Opus). Only meaningful for reporting a
	/// version mismatch back to the user; decoding reads the byte itself.
	/// </summary>
	public static int PeerVersion(ReadOnlySpan<byte> packet)
	{
		if (packet.Length < HeaderLength || !packet[..Magic.Length].SequenceEqual(Magic))
		{
			return 0;
		}
		return packet[4];
	}

	public bool TryDecode(
		ReadOnlySpan<byte> packet,
		ulong sequence,
		ulong timestampMilliseconds,
		int legacyFrameMilliseconds,
		Span<byte> plaintextDestination,
		out DecodedAudioPayload decoded) =>
		Decode(packet, sequence, timestampMilliseconds, legacyFrameMilliseconds, plaintextDestination, out decoded)
			== AudioPayloadDecodeStatus.Ok;

	public AudioPayloadDecodeStatus Decode(
		ReadOnlySpan<byte> packet,
		ulong sequence,
		ulong timestampMilliseconds,
		int legacyFrameMilliseconds,
		Span<byte> plaintextDestination,
		out DecodedAudioPayload decoded)
	{
		decoded = default;
		if (packet.Length < Magic.Length || !packet[..Magic.Length].SequenceEqual(Magic))
		{
			if (packet.Length > plaintextDestination.Length)
			{
				return AudioPayloadDecodeStatus.Malformed;
			}
			packet.CopyTo(plaintextDestination);
			decoded = new DecodedAudioPayload(AudioPayloadCodec.Opus, legacyFrameMilliseconds, packet.Length, Legacy: true, Encrypted: false);
			return AudioPayloadDecodeStatus.Ok;
		}

		if (packet.Length < HeaderLength)
		{
			return AudioPayloadDecodeStatus.Malformed;
		}

		// Read the version before anything else, and say which way it differs. A bare
		// drop here is indistinguishable from a wrong password, a firewall, or the VPN
		// being down -- and sends the user to the wrong machine.
		var peerVersion = packet[4];
		if (peerVersion > Version)
		{
			return AudioPayloadDecodeStatus.PeerTooNew;
		}
		if (peerVersion < MinSupportedVersion)
		{
			return AudioPayloadDecodeStatus.PeerTooOld;
		}
		if (!Enum.IsDefined(typeof(AudioPayloadCodec), packet[6]))
		{
			// A version we accept carrying a codec we have never heard of: only a newer
			// publisher produces that, so name the half that is behind rather than
			// blaming the network.
			return AudioPayloadDecodeStatus.PeerTooNew;
		}

		var encrypted = (packet[5] & EncryptedFlag) != 0;
		var bodyLength = packet.Length - HeaderLength - (encrypted ? TagLength : 0);
		if (bodyLength < 0 || bodyLength > plaintextDestination.Length)
		{
			return AudioPayloadDecodeStatus.Malformed;
		}

		if (!encrypted)
		{
			packet.Slice(HeaderLength, bodyLength).CopyTo(plaintextDestination);
		}
		else
		{
			if (_aes is null)
			{
				// The publisher is encrypting and this receiver has no password.
				return AudioPayloadDecodeStatus.AuthenticationFailed;
			}
			Span<byte> nonce = stackalloc byte[NonceLength];
			CreateNonce(packet.Slice(NonceOffset, NonceLength), sequence, nonce);
			Span<byte> associatedData = stackalloc byte[HeaderLength + 16];
			packet[..HeaderLength].CopyTo(associatedData);
			BinaryPrimitives.WriteUInt64BigEndian(associatedData[HeaderLength..], sequence);
			BinaryPrimitives.WriteUInt64BigEndian(associatedData[(HeaderLength + 8)..], timestampMilliseconds);
			try
			{
				_aes.Decrypt(
					nonce,
					packet.Slice(HeaderLength, bodyLength),
					packet.Slice(HeaderLength + bodyLength, TagLength),
					plaintextDestination[..bodyLength],
					associatedData);
			}
			catch (AuthenticationTagMismatchException)
			{
				return AudioPayloadDecodeStatus.AuthenticationFailed;
			}
		}

		decoded = new DecodedAudioPayload((AudioPayloadCodec)packet[6], packet[7], bodyLength, Legacy: false, Encrypted: encrypted);
		return AudioPayloadDecodeStatus.Ok;
	}

	public void Dispose()
	{
		_aes?.Dispose();
		if (_key is not null)
		{
			CryptographicOperations.ZeroMemory(_key);
		}
	}

	private static byte[] DeriveKey(string password, string roomKey)
	{
		var saltMaterial = Encoding.UTF8.GetBytes("NVDARemoteAudioClient-v2\0" + roomKey);
		var salt = SHA256.HashData(saltMaterial);
		return Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyLength);
	}

	private static void CreateNonce(ReadOnlySpan<byte> nonceBase, ulong sequence, Span<byte> nonce)
	{
		nonceBase.CopyTo(nonce);
		Span<byte> sequenceBytes = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64BigEndian(sequenceBytes, sequence);
		for (var index = 0; index < sequenceBytes.Length; index++)
		{
			nonce[NonceLength - sequenceBytes.Length + index] ^= sequenceBytes[index];
		}
	}
}
