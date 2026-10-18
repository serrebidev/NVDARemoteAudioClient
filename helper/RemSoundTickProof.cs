using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NVDARemoteAudioHelper;

/// <summary>
/// Tick proofs (packet type 11), RemSound's "I have ticked you, and I hold the
/// password", added by RemSound on 2026-09-25 and ported here byte-for-byte from
/// src/RemSound.Core/TickProof.cs, which is the specification.
///
/// A peer sends one every few seconds to each peer it has ticked, whatever it
/// sends or receives, so "accept connections" on the other side can act on a
/// peer that only listens: a phone ticking this computer to hear it sends no
/// audio, and its Format packet was the only thing on the wire that proved it
/// held the password. Here the flow is the add-on's own: the devices the user
/// chose are the ticked ones, and a proof arriving from one of them is how this
/// computer learns it has been ticked back — the fact a person setting up
/// cannot otherwise work out for themselves.
///
/// The plaintext is sealed with the same AES-256-GCM audio key as remote
/// control: only a password holder can make one, and a captured one cannot be
/// reused, because it carries its own time and the receiver remembers every
/// seal it has accepted. The layout is the usual nonce(12) || tag(16) ||
/// ciphertext over 25 plaintext bytes: a format version (1), the sender's UTC
/// time in Unix seconds, and the sender's discovery instance id, the GUID in
/// the order its text form reads. Older peers see an unknown packet type and
/// drop it, exactly as they did Control and AddrCheck before this.
/// </summary>
internal static class RemTickProof
{
	public const byte FormatVersion = 1;
	/// <summary>version(1) + unix-utc-seconds(8) + instance id(16).</summary>
	public const int PlainBytes = 1 + 8 + 16;
	/// <summary>Sealed wire size: nonce + tag over the 25-byte plaintext (= 53).</summary>
	public const int SealedPayloadBytes = PlainBytes + RemCrypto.OverheadBytes;
	/// <summary>How often a peer sends a proof to each peer it has ticked.</summary>
	public static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(5);

	public static byte[] Seal(byte[] key, Guid senderInstanceId, long unixUtcSeconds)
	{
		Span<byte> plain = stackalloc byte[PlainBytes];
		plain[0] = FormatVersion;
		BinaryPrimitives.WriteInt64LittleEndian(plain[1..], unixUtcSeconds);
		senderInstanceId.TryWriteBytes(plain[9..], bigEndian: true, out _);
		var sealedPayload = new byte[SealedPayloadBytes];
		var nonce = sealedPayload.AsSpan(0, RemCrypto.NonceBytes);
		RandomNumberGenerator.Fill(nonce);
		using var gcm = new AesGcm(key, RemCrypto.TagBytes);
		gcm.Encrypt(nonce, plain, sealedPayload.AsSpan(RemCrypto.OverheadBytes), sealedPayload.AsSpan(RemCrypto.NonceBytes, RemCrypto.TagBytes));
		return sealedPayload;
	}

	/// <summary>The whole packet: header (type 11, stream 0xFFFF) and the sealed payload.</summary>
	public static byte[] BuildPacket(byte[] key, Guid senderInstanceId, long unixUtcSeconds, uint sequence)
	{
		var sealedPayload = Seal(key, senderInstanceId, unixUtcSeconds);
		var packet = new byte[RemPacket.HeaderSize + sealedPayload.Length];
		RemPacket.WriteHeader(packet, RemPacketType.TickProof, RemPacket.HeartbeatStreamId, sequence);
		sealedPayload.CopyTo(packet.AsSpan(RemPacket.HeaderSize));
		return packet;
	}

	/// <summary>
	/// Open a sealed proof. False when the size is wrong, the tag fails (another
	/// password, or tampered), or the format version is one this build does not know.
	/// </summary>
	public static bool TryUnseal(AesGcm gcm, ReadOnlySpan<byte> payload, out Guid senderInstanceId, out long unixUtcSeconds, out ulong nonceId)
	{
		senderInstanceId = Guid.Empty;
		unixUtcSeconds = 0;
		nonceId = 0;
		if (payload.Length != SealedPayloadBytes)
		{
			return false;
		}
		Span<byte> plain = stackalloc byte[PlainBytes];
		if (!RemCrypto.TryDecryptInto(gcm, payload, plain, out var written) || written != PlainBytes ||
			plain[0] != FormatVersion)
		{
			return false;
		}
		unixUtcSeconds = BinaryPrimitives.ReadInt64LittleEndian(plain[1..]);
		senderInstanceId = new Guid(plain.Slice(9, 16), bigEndian: true);
		// The nonce is random per seal, so its first 8 bytes identify this packet for the replay memory.
		nonceId = BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]);
		return true;
	}
}

/// <summary>
/// Receiver-side gate for tick proofs: authenticates with the key, bounds
/// staleness against clock skew, and never accepts the same seal twice, so a
/// captured proof replayed from another address proves nothing.
/// </summary>
internal sealed class RemTickProofGuard
{
	/// <summary>The reason a proof sealed with another password is refused.</summary>
	public const string NotOurPassword = "not our password";
	/// <summary>Generous, as for remote control: peers' clocks are not synchronised.</summary>
	public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(10);

	private readonly Dictionary<ulong, DateTime> _seen = [];

	public bool TryAccept(AesGcm gcm, ReadOnlySpan<byte> payload, DateTime utcNow, out Guid senderInstanceId, out string rejectReason)
	{
		if (!RemTickProof.TryUnseal(gcm, payload, out senderInstanceId, out var unixSeconds, out var nonceId))
		{
			rejectReason = NotOurPassword;
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
		// A peer sends one every few seconds for as long as it has us ticked, so
		// the memory is pruned of anything older than the window, and hard-capped
		// against a flood.
		if (_seen.Count >= 4096)
		{
			foreach (var stale in _seen.Where(pair => utcNow - pair.Value > MaxSkew + MaxSkew).Select(pair => pair.Key).ToList())
			{
				_seen.Remove(stale);
			}
			if (_seen.Count >= 4096)
			{
				_seen.Clear();
			}
		}
		_seen[nonceId] = utcNow;
		rejectReason = "";
		return true;
	}
}
