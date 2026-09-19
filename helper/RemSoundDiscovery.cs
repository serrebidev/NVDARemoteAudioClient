using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NVDARemoteAudioHelper;

internal sealed record RemSoundPeer(
	string InstanceId,
	string Name,
	IPAddress Address,
	int AudioPort,
	bool CanSend,
	bool CanReceive,
	DateTime LastSeenUtc);

/// <summary>
/// RemSound's LAN discovery: every 1.5 seconds each app announces a small JSON object
/// (InstanceId, Name, AudioPort, CanSend, CanReceive, matched case-sensitively) to UDP
/// 47821, by broadcast on every network and by unicast to every address it knows.
/// Unicast is what crosses Tailscale and what iPhones rely on, because iOS restricts
/// broadcast: an announcement received from an address makes that address a unicast
/// target from then on. Peers expire eight seconds after their last announcement.
/// </summary>
internal sealed class RemSoundDiscovery : IDisposable
{
	private static readonly TimeSpan AnnounceInterval = TimeSpan.FromMilliseconds(1500);
	public static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(8);
	// The Android receiver announces to 47831, one above the audio port, which is
	// what RemSound's own comments once described. Listening there too costs nothing
	// and lets an Android phone show up without typing its address.
	private const int AndroidAnnouncePort = 47831;

	private readonly object _gate = new();
	private readonly Dictionary<string, RemSoundPeer> _peers = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<UdpClient> _listeners = [];
	private readonly string _instanceId;
	private readonly string _displayName;
	private readonly int _discoveryPort;
	private readonly int _audioPort;
	private UdpClient? _announcer;
	private CancellationTokenSource? _cts;
	private volatile IPAddress[] _unicastTargets = [];
	private volatile bool _canSend;
	private volatile bool _canReceive;

	/// <summary>
	/// <paramref name="instanceId"/> is normally left null so the identity is the one
	/// remembered from a previous run; see <see cref="RemSoundIdentity"/>.
	/// </summary>
	public RemSoundDiscovery(string displayName, int discoveryPort, int audioPort, bool canSend, bool canReceive, string? instanceId = null)
	{
		_instanceId = RemSoundIdentity.Resolve(instanceId);
		_displayName = string.IsNullOrWhiteSpace(displayName) ? Environment.MachineName : displayName.Trim();
		_discoveryPort = discoveryPort;
		_audioPort = audioPort;
		_canSend = canSend;
		_canReceive = canReceive;
	}

	public string InstanceId => _instanceId;

	public event Action? PeersChanged;

	public void Start(bool listen = true, bool announce = true)
	{
		_cts = new CancellationTokenSource();
		if (listen)
		{
			foreach (var port in _discoveryPort == RemPacket.DiscoveryPort
				? new[] { _discoveryPort, AndroidAnnouncePort }
				: new[] { _discoveryPort })
			{
				try
				{
					var listener = new UdpClient(AddressFamily.InterNetwork);
					// RemSound itself may be running on this computer and holds the same
					// port. Sharing it keeps both working instead of failing to start.
					listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
					listener.EnableBroadcast = true;
					listener.Client.Bind(new IPEndPoint(IPAddress.Any, port));
					_listeners.Add(listener);
					_ = Task.Run(() => ListenLoopAsync(listener, _cts.Token));
				}
				catch (SocketException ex)
				{
					JsonLog.Write("diagnostic", $"RemSound discovery could not listen on UDP {port}: {ex.Message}");
				}
			}
		}
		if (announce)
		{
			_announcer = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
			_ = Task.Run(() => AnnounceLoopAsync(_cts.Token));
		}
	}

	public IReadOnlyList<RemSoundPeer> Peers
	{
		get
		{
			lock (_gate)
			{
				PruneExpired(DateTime.UtcNow);
				return _peers.Values.OrderBy(peer => peer.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
			}
		}
	}

	public void SetUnicastTargets(IEnumerable<IPAddress> addresses)
	{
		var merged = _unicastTargets.Concat(addresses).Distinct().ToArray();
		_unicastTargets = merged;
	}

	public void UpdateCapabilities(bool canSend, bool canReceive)
	{
		_canSend = canSend;
		_canReceive = canReceive;
		Announce();
	}

	/// <summary>
	/// Parses one announcement. Discovery is an open broadcast port, so this is a trust
	/// boundary: anything malformed is dropped, and a name is trimmed rather than
	/// trusted, because it is spoken aloud and written to logs.
	/// </summary>
	internal static bool TryParseAnnouncement(ReadOnlySpan<byte> payload, IPAddress from, string ownInstanceId, out RemSoundPeer peer)
	{
		peer = null!;
		if (payload.IsEmpty || payload.Length > 4096)
		{
			return false;
		}
		try
		{
			using var document = JsonDocument.Parse(payload.ToArray());
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object ||
				!root.TryGetProperty("InstanceId", out var idElement) || idElement.ValueKind != JsonValueKind.String ||
				!root.TryGetProperty("AudioPort", out var portElement) || !portElement.TryGetInt32(out var port))
			{
				return false;
			}
			var id = idElement.GetString() ?? "";
			if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty ||
				string.Equals(id, ownInstanceId, StringComparison.OrdinalIgnoreCase) || port is < 1 or > 65535)
			{
				return false;
			}
			var name = root.TryGetProperty("Name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
				? (nameElement.GetString() ?? "").Trim()
				: "";
			name = new string(name.Where(c => !char.IsControl(c)).ToArray());
			if (name.Length == 0)
			{
				name = from.ToString();
			}
			if (name.Length > 128)
			{
				name = name[..128];
			}
			peer = new RemSoundPeer(
				guid.ToString("D"),
				name,
				from,
				port,
				ReadBool(root, "CanSend"),
				ReadBool(root, "CanReceive"),
				DateTime.UtcNow);
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	internal static byte[] BuildAnnouncement(string instanceId, string name, int audioPort, bool canSend, bool canReceive)
	{
		var message = new Dictionary<string, object>
		{
			["InstanceId"] = instanceId,
			["Name"] = name,
			["AudioPort"] = audioPort,
			["CanSend"] = canSend,
			["CanReceive"] = canReceive,
		};
		return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
	}

	private static bool ReadBool(JsonElement root, string name) =>
		root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;

	private async Task ListenLoopAsync(UdpClient listener, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			UdpReceiveResult result;
			try
			{
				result = await listener.ReceiveAsync(token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			catch (SocketException)
			{
				continue;
			}
			var from = result.RemoteEndPoint.Address;
			if (from.IsIPv4MappedToIPv6)
			{
				from = from.MapToIPv4();
			}
			if (!TryParseAnnouncement(result.Buffer, from, _instanceId, out var peer))
			{
				continue;
			}
			// Answer the way it came: this is what makes discovery work both ways over a
			// VPN, and with an iPhone that cannot broadcast.
			if (!_unicastTargets.Contains(from))
			{
				_unicastTargets = [.. _unicastTargets, from];
			}
			if (Record(peer))
			{
				PeersChanged?.Invoke();
			}
		}
	}

	private bool Record(RemSoundPeer peer)
	{
		lock (_gate)
		{
			// One entry per machine and path; a phone on Wi-Fi and Tailscale at once
			// announces from both addresses, and either may carry its audio.
			var key = peer.InstanceId + "|" + peer.Address;
			var changed = !_peers.TryGetValue(key, out var existing) ||
				existing.Name != peer.Name ||
				existing.CanSend != peer.CanSend ||
				existing.CanReceive != peer.CanReceive ||
				existing.AudioPort != peer.AudioPort;
			_peers[key] = peer;
			return PruneExpired(peer.LastSeenUtc) || changed;
		}
	}

	private bool PruneExpired(DateTime nowUtc)
	{
		var expired = _peers.Where(pair => nowUtc - pair.Value.LastSeenUtc > PeerExpiry).Select(pair => pair.Key).ToList();
		foreach (var key in expired)
		{
			_peers.Remove(key);
		}
		return expired.Count > 0;
	}

	private async Task AnnounceLoopAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			Announce();
			bool expired;
			lock (_gate)
			{
				expired = PruneExpired(DateTime.UtcNow);
			}
			if (expired)
			{
				PeersChanged?.Invoke();
			}
			try
			{
				await Task.Delay(AnnounceInterval, token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	private void Announce()
	{
		var announcer = _announcer;
		if (announcer is null)
		{
			return;
		}
		var bytes = BuildAnnouncement(_instanceId, _displayName, _audioPort, _canSend, _canReceive);
		foreach (var address in BroadcastAddresses().Concat(_unicastTargets).Distinct())
		{
			try
			{
				announcer.Send(bytes, bytes.Length, new IPEndPoint(address, _discoveryPort));
			}
			catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
			{
				// Discovery is a convenience; audio works without it.
			}
		}
	}

	private static IEnumerable<IPAddress> BroadcastAddresses()
	{
		var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
		try
		{
			foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
				{
					continue;
				}
				foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
				{
					if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
					{
						continue;
					}
					var address = unicast.Address.GetAddressBytes();
					var mask = unicast.IPv4Mask.GetAddressBytes();
					var broadcast = new byte[4];
					for (var i = 0; i < 4; i++)
					{
						broadcast[i] = (byte)(address[i] | ~mask[i]);
					}
					addresses.Add(new IPAddress(broadcast));
				}
			}
		}
		catch (NetworkInformationException)
		{
			// The limited broadcast address still reaches most LANs.
		}
		return addresses;
	}

	public void Dispose()
	{
		_cts?.Cancel();
		foreach (var listener in _listeners)
		{
			listener.Dispose();
		}
		_listeners.Clear();
		_announcer?.Dispose();
		_announcer = null;
		_cts?.Dispose();
		_cts = null;
	}

	/// <summary>
	/// Listens for a few seconds and reports what answered, for the add-on's "find
	/// devices" dialog when no connection is running. Announces too, so an iPhone
	/// that only unicasts to addresses it has heard from can find this computer.
	/// </summary>
	public static async Task WriteDiscoveredPeersAsync(int discoveryPort, int seconds, string displayName)
	{
		using var discovery = new RemSoundDiscovery(displayName, discoveryPort, RemPacket.DefaultPort, canSend: true, canReceive: true);
		discovery.Start();
		await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)));
		WritePeers(discovery.Peers, "RemSound devices found.");
	}

	public static void WritePeers(IReadOnlyList<RemSoundPeer> peers, string message)
	{
		JsonLog.Write("peers", message, new Dictionary<string, object?>
		{
			["peers"] = peers.Select(peer => new Dictionary<string, object?>
			{
				["Name"] = peer.Name,
				["Address"] = peer.Address.ToString(),
				["AudioPort"] = peer.AudioPort,
				["CanSend"] = peer.CanSend,
				["CanReceive"] = peer.CanReceive,
				["InstanceId"] = peer.InstanceId,
			}).ToList(),
		});
	}
}
