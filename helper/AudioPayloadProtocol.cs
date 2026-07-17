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
/// End-to-end audio payload envelope. The relay forwards these bytes without
/// understanding them, so legacy relay servers remain compatible.
/// </summary>
internal sealed class AudioPayloadProtocol : IDisposable
{
	private static ReadOnlySpan<byte> Magic => "RAE2"u8;
	private const byte Version = 2;
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

	public bool TryDecode(
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
				return false;
			}
			packet.CopyTo(plaintextDestination);
			decoded = new DecodedAudioPayload(AudioPayloadCodec.Opus, legacyFrameMilliseconds, packet.Length, Legacy: true, Encrypted: false);
			return true;
		}

		if (packet.Length < HeaderLength || packet[4] != Version)
		{
			return false;
		}
		if (!Enum.IsDefined(typeof(AudioPayloadCodec), packet[6]))
		{
			return false;
		}

		var encrypted = (packet[5] & EncryptedFlag) != 0;
		var bodyLength = packet.Length - HeaderLength - (encrypted ? TagLength : 0);
		if (bodyLength < 0 || bodyLength > plaintextDestination.Length)
		{
			return false;
		}

		if (!encrypted)
		{
			packet.Slice(HeaderLength, bodyLength).CopyTo(plaintextDestination);
		}
		else
		{
			if (_aes is null)
			{
				return false;
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
				return false;
			}
		}

		decoded = new DecodedAudioPayload((AudioPayloadCodec)packet[6], packet[7], bodyLength, Legacy: false, Encrypted: encrypted);
		return true;
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
