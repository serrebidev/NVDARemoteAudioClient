using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NVDARemoteAudioHelper;

/// <summary>
/// RemSound V2 relay groups (relay server v2.9+, RemSound 6.0): a relay carries up
/// to 64 devices in password groups instead of a single pair. The specification is
/// server/RELAY-GROUPS.md in the RemSound repository; this is a byte-for-byte port
/// of the parts a group member has to agree on.
///
/// Everything a group member sends to the relay travels in group framing: the
/// ordinary 12-byte header with its version byte set to 2, then the sender's
/// 16-byte client id, then the ordinary payload untouched. The relay makes one
/// copy per member who is due it; nobody downloads a single shared stream.
///
/// A member that does nothing new keeps working exactly as before: it pairs
/// through the relay's two ordinary slots. Group behavior switches on only once a
/// member list has arrived in the last 5 seconds, and switches back off if the
/// lists stop.
/// </summary>
internal static class RemGroupPacket
{
	public const byte Version = 2;
	public const int ClientIdBytes = 16;
	public const int HeaderSize = RemPacket.HeaderSize + ClientIdBytes; // 28
	public const int HelloNameBytes = 32;
	/// <summary>The same 8 bytes the Format payload carries at offset 36.</summary>
	public const int GroupTagBytes = RemPacket.PasswordFingerprintSize;
	public const int MaxTicked = 64;
	/// <summary>Roster entry size from server v2.9: 16-byte id, 32-byte name, 1 flags byte.</summary>
	public const int RosterEntryNewBytes = 49;
	/// <summary>Roster entry size before server v2.9: no per-member flags byte.</summary>
	public const int RosterEntryOldBytes = 48;
	public const int MaxMembers = 64;
	/// <summary>
	/// Largest audio payload that fits one datagram in group framing: the ordinary
	/// budget minus the 16-byte client id the header gains.
	/// </summary>
	public const int MaxAudioPayloadBytes = RemPacket.MaxAudioPayloadBytes - ClientIdBytes;
	/// <summary>Bit 0 of a member's flags byte: that member has ticked us.</summary>
	public const byte MemberTickedUsFlag = 0x01;
	/// <summary>Bit 0 of the roster's trailing flags byte: we also hold an ordinary pair slot.</summary>
	public const byte ListHasPairSlotFlag = 0x01;
	/// <summary>How long a member list stays fresh before group behavior switches back off.</summary>
	public static readonly TimeSpan RosterFreshFor = TimeSpan.FromSeconds(5);
	/// <summary>How often a member re-sends its hello to the relay.</summary>
	public static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(2);

	private static readonly byte[] ZeroClientId = new byte[ClientIdBytes];

	public static bool IsRelayId(ReadOnlySpan<byte> clientId) => clientId.SequenceEqual(ZeroClientId);

	/// <summary>
	/// Gives each group member its own address in 240.0.0.0/5, the way the Windows
	/// app does, so everything keyed per address (streams, health, names) works per
	/// person without a parallel keying scheme. The base address comes from the
	/// first four client-id bytes; when two ids in the same roster agree there, the
	/// later one (in id order) has its last octet nudged until the address is
	/// unique, so the mapping stays deterministic for a given roster.
	/// </summary>
	public static IPAddress MemberAddress(ReadOnlySpan<byte> clientId, IEnumerable<byte[]>? rosterIds = null)
	{
		var address = BaseMemberAddress(clientId);
		if (rosterIds is null)
		{
			return address;
		}
		var ordered = rosterIds
			.Select(id => id.ToArray())
			.OrderBy(id => Convert.ToHexString(id), StringComparer.Ordinal)
			.ToList();
		var taken = new HashSet<IPAddress>();
		foreach (var id in ordered)
		{
			var candidate = BaseMemberAddress(id);
			var bumped = candidate.GetAddressBytes();
			while (!taken.Add(candidate))
			{
				bumped[3]++;
				candidate = new IPAddress((byte[])bumped.Clone());
			}
			if (id.AsSpan().SequenceEqual(clientId))
			{
				return candidate;
			}
		}
		return address;
	}

	private static IPAddress BaseMemberAddress(ReadOnlySpan<byte> clientId)
	{
		var bytes = new byte[4];
		bytes[0] = (byte)(0xF0 | (clientId[0] & 0x07)); // 240-247
		bytes[1] = clientId[1];
		bytes[2] = clientId[2];
		bytes[3] = clientId[3];
		return new IPAddress(bytes);
	}

	public static int WriteHeader(Span<byte> destination, RemPacketType type, ushort streamId, uint sequence, ReadOnlySpan<byte> clientId)
	{
		BinaryPrimitives.WriteInt32LittleEndian(destination, RemPacket.Magic);
		destination[4] = Version;
		destination[5] = (byte)type;
		BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], streamId == 0 ? (ushort)1 : streamId);
		BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], sequence);
		clientId[..ClientIdBytes].CopyTo(destination.Slice(12, ClientIdBytes));
		return HeaderSize;
	}

	public static bool TryReadHeader(ReadOnlySpan<byte> packet, out RemPacketType type, out ushort streamId, out uint sequence, out byte[] clientId)
	{
		type = default;
		streamId = 0;
		sequence = 0;
		clientId = new byte[ClientIdBytes];
		if (packet.Length < HeaderSize || BinaryPrimitives.ReadInt32LittleEndian(packet) != RemPacket.Magic || packet[4] != Version)
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
		packet.Slice(12, ClientIdBytes).CopyTo(clientId);
		return true;
	}

	/// <summary>
	/// Wraps an ordinary (version 1) packet in group framing for the relay. The
	/// payload is untouched; only the header grows by the 16-byte client id.
	/// </summary>
	public static byte[] Wrap(ReadOnlySpan<byte> ordinary, ReadOnlySpan<byte> clientId)
	{
		if (!RemPacket.TryReadHeader(ordinary, out var type, out var streamId, out var sequence))
		{
			throw new ArgumentException("Only an ordinary RemSound packet can be wrapped in group framing.");
		}
		var wrapped = new byte[HeaderSize + ordinary.Length - RemPacket.HeaderSize];
		WriteHeader(wrapped, type, streamId, sequence, clientId);
		ordinary[RemPacket.HeaderSize..].CopyTo(wrapped.AsSpan(HeaderSize));
		return wrapped;
	}

	/// <summary>
	/// The hello a member sends the relay every 2 seconds (and at once when the
	/// tick list, name or password changes). A null tick list means the legacy
	/// 40-byte hello: "everyone in my group", which is also what every app sent
	/// before groups existed. An empty list means "nobody yet".
	/// </summary>
	public static byte[] BuildHello(ReadOnlySpan<byte> clientId, string displayName, ReadOnlySpan<byte> groupTag, IReadOnlyList<byte[]>? ticked)
	{
		if (groupTag.Length != GroupTagBytes)
		{
			throw new ArgumentException("The group tag is the 8-byte password fingerprint.");
		}
		var tickCount = ticked?.Count ?? 0;
		if (tickCount > MaxTicked)
		{
			throw new ArgumentException("A hello carries at most 64 ticked client ids.");
		}
		var payloadLength = HelloNameBytes + GroupTagBytes + (ticked is null ? 0 : 1 + tickCount * ClientIdBytes);
		var packet = new byte[HeaderSize + payloadLength];
		WriteHeader(packet, RemPacketType.Hello, 1, 0, clientId);
		var payload = packet.AsSpan(HeaderSize);
		EncodeName(displayName).CopyTo(payload);
		groupTag.CopyTo(payload.Slice(HelloNameBytes, GroupTagBytes));
		if (ticked is not null)
		{
			payload[HelloNameBytes + GroupTagBytes] = (byte)tickCount;
			for (var i = 0; i < tickCount; i++)
			{
				ticked[i].AsSpan(0, ClientIdBytes).CopyTo(payload.Slice(HelloNameBytes + GroupTagBytes + 1 + i * ClientIdBytes, ClientIdBytes));
			}
		}
		return packet;
	}

	/// <summary>The goodbye a member sends the relay when it leaves: no payload.</summary>
	/// <summary>
	/// A server ping (type 15): group framing carrying up to 32 bytes of the
	/// sender's own choosing, which the relay sends straight back unchanged,
	/// at most four a second. Windows puts its microsecond clock in it and
	/// times the round trip; a member that pings is also a member the relay
	/// sends its server status to (see <see cref="RemServerInfo"/>).
	/// </summary>
	public static byte[] BuildServerPing(ReadOnlySpan<byte> clientId, long micros)
	{
		var packet = new byte[HeaderSize + 8];
		WriteHeader(packet, RemPacketType.ServerPing, 1, 0, clientId);
		BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(HeaderSize), micros);
		return packet;
	}

	/// <summary>
	/// The tick list a hello actually carries. With hear-myself off it is the
	/// member's own list, untouched: null stays null ("everyone in my group").
	/// With it on, this member's own client id goes first — the relay sends a
	/// member its own Audio and Format back when its hello names itself
	/// (server-v2.15) — followed by the explicit list, or, when there is none,
	/// the current roster, so "hear the whole group" keeps meaning the whole
	/// group plus yourself. Duplicates drop, and the relay's 64-entry cap can
	/// never push the member's own id out, because it is first.
	/// </summary>
	public static List<byte[]>? EffectiveTicks(ReadOnlySpan<byte> clientId, IReadOnlyList<byte[]>? ticks, IEnumerable<byte[]> memberIds, bool hearMyself)
	{
		if (!hearMyself)
		{
			return ticks?.ToList();
		}
		var list = new List<byte[]> { clientId.ToArray() };
		foreach (var id in ticks ?? memberIds)
		{
			if (!list.Any(existing => existing.AsSpan().SequenceEqual(id)))
			{
				list.Add(id.ToArray());
			}
		}
		return list.Count <= MaxTicked ? list : list.Take(MaxTicked).ToList();
	}

	public static byte[] BuildBye(ReadOnlySpan<byte> clientId)
	{
		var packet = new byte[HeaderSize];
		WriteHeader(packet, RemPacketType.Bye, 1, 0, clientId);
		return packet;
	}

	/// <summary>UTF-8, cut to 32 bytes on a character boundary, zero-padded.</summary>
	public static byte[] EncodeName(string name)
	{
		var result = new byte[HelloNameBytes];
		var encoder = Encoding.UTF8.GetEncoder();
		encoder.Convert(name.AsSpan(), result, true, out _, out _, out _);
		return result;
	}

	public static string DecodeName(ReadOnlySpan<byte> nameBytes)
	{
		var length = nameBytes.IndexOf((byte)0);
		return Encoding.UTF8.GetString(nameBytes[..(length < 0 ? nameBytes.Length : length)]);
	}
}

/// <summary>One member of our group, as the relay's roster describes them.</summary>
internal sealed record RemRosterMember(byte[] ClientId, string Name, bool TickedUs)
{
	public string IdHex => Convert.ToHexString(ClientId).ToLowerInvariant();
}

/// <summary>
/// Reads the relay's roster: 1 count byte, one entry per member, 1 trailing flags
/// byte for the list as a whole. Entries are 49 bytes from server v2.9 (with the
/// per-member flags byte) and 48 before it; the size is read from the payload
/// length so both work.
/// </summary>
internal static class RemRoster
{
	public static bool TryRead(ReadOnlySpan<byte> payload, out List<RemRosterMember> members, out byte listFlags)
	{
		members = [];
		listFlags = 0;
		if (payload.Length < 2)
		{
			return false;
		}
		var count = payload[0];
		if (count > RemGroupPacket.MaxMembers)
		{
			return false;
		}
		listFlags = payload[^1];
		var entries = payload.Slice(1, payload.Length - 2);
		if (count == 0)
		{
			return entries.Length == 0;
		}
		if (entries.Length % count != 0)
		{
			return false;
		}
		var entrySize = entries.Length / count;
		if (entrySize is not (RemGroupPacket.RosterEntryNewBytes or RemGroupPacket.RosterEntryOldBytes))
		{
			return false;
		}
		for (var i = 0; i < count; i++)
		{
			var entry = entries.Slice(i * entrySize, entrySize);
			var id = entry[..RemGroupPacket.ClientIdBytes].ToArray();
			var name = RemGroupPacket.DecodeName(entry.Slice(RemGroupPacket.ClientIdBytes, RemGroupPacket.HelloNameBytes));
			var tickedUs = entrySize == RemGroupPacket.RosterEntryNewBytes &&
				(entry[RemGroupPacket.ClientIdBytes + RemGroupPacket.HelloNameBytes] & RemGroupPacket.MemberTickedUsFlag) != 0;
			members.Add(new RemRosterMember(id, name, tickedUs));
		}
		return true;
	}
}

/// <summary>
/// The group client id this installation announces to V2 relays: 16 random bytes,
/// picked once and kept, never all zero. A phone that has ticked this computer
/// must see the same id after a restart, or the user has to find and tick it
/// again — the same reason the discovery identity is kept.
/// </summary>
internal static class RemSoundGroupIdentity
{
	private const string FileName = "remSoundGroupClientId";

	public static byte[] Resolve(string? folder = null)
	{
		var path = Path.Combine(folder ?? RemSoundIdentity.DefaultFolder(), FileName);
		try
		{
			if (File.Exists(path))
			{
				try
				{
					var stored = Convert.FromHexString(File.ReadAllText(path).Trim());
					if (stored.Length == RemGroupPacket.ClientIdBytes && stored.Any(b => b != 0))
					{
						return stored;
					}
				}
				catch (FormatException)
				{
					// A hand-edited file falls through to a fresh id below.
				}
			}

			var fresh = NewId();
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, Convert.ToHexString(fresh).ToLowerInvariant());
			return fresh;
		}
		catch (Exception ex)
		{
			// A read-only profile, a full disk, or a locked-down machine must not
			// stop audio. A fresh id per run is what the spec allows as a fallback.
			JsonLog.Write("diagnostic", $"Could not keep a RemSound group client ID: {ex.Message}");
			return NewId();
		}
	}

	public static byte[] NewId()
	{
		var id = new byte[RemGroupPacket.ClientIdBytes];
		do
		{
			RandomNumberGenerator.Fill(id);
		}
		while (id.All(b => b == 0));
		return id;
	}
}

/// <summary>
/// The relay's own status for the group it is carrying (packet type 14,
/// RELAY-GROUPS.md §10; read here the way RelayGroupClient.ReadServerInfo
/// reads it in RemSound). The relay sends it every five seconds, and only to
/// members that have sent it a server ping: an app that never pings never sees
/// it. It is one of the two things a member learns about the relay itself —
/// the other is its roster — and the only way to tell a current relay from
/// one too old to say anything (server-v2.16 added it).
///
/// The body after the 28-byte group header: a layout byte (1), a version
/// length, the version in ASCII ("server-v2.17"), then little-endian fields —
/// uptime seconds, people on the relay, groups, places free, phones and older
/// apps in ordinary pairs, and this member's own arrival numbers over the
/// last minute: packets, the longest gap between two of them in MICROseconds
/// (RemSound divides by 1000 and shows milliseconds), gaps over 20 ms, and
/// seconds with sound. A reader that meets a longer body reads the fields it
/// knows and stops.
/// </summary>
internal sealed record RemServerInfo(
	string Version,
	TimeSpan Uptime,
	int People,
	int Groups,
	int PlacesFree,
	int Phones,
	int YourPackets,
	double YourLongestGapMs,
	int YourLongGaps,
	int YourSeconds,
	DateTime ReceivedUtc)
{
	/// <summary>Bytes after the version string: 4 + 2+2+2+2 + 4 + 4 + 2 + 2.</summary>
	public const int FixedBytes = 24;

	public static bool TryRead(ReadOnlySpan<byte> payload, DateTime receivedUtc, out RemServerInfo info)
	{
		info = null!;
		if (payload.Length < 2 || payload[0] != 1)
		{
			return false;
		}
		var versionBytes = payload[1];
		if (payload.Length < 2 + versionBytes + FixedBytes)
		{
			return false;
		}
		var version = Encoding.ASCII.GetString(payload.Slice(2, versionBytes));
		var at = payload[(2 + versionBytes)..];
		info = new RemServerInfo(
			version,
			TimeSpan.FromSeconds(BinaryPrimitives.ReadUInt32LittleEndian(at)),
			BinaryPrimitives.ReadUInt16LittleEndian(at[4..]),
			BinaryPrimitives.ReadUInt16LittleEndian(at[6..]),
			BinaryPrimitives.ReadUInt16LittleEndian(at[8..]),
			BinaryPrimitives.ReadUInt16LittleEndian(at[10..]),
			(int)BinaryPrimitives.ReadUInt32LittleEndian(at[12..]),
			BinaryPrimitives.ReadUInt32LittleEndian(at[16..]) / 1000.0,
			BinaryPrimitives.ReadUInt16LittleEndian(at[20..]),
			BinaryPrimitives.ReadUInt16LittleEndian(at[22..]),
			receivedUtc);
		return true;
	}
}
