using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NVDARemoteAudioHelper;

/// <summary>A peer the user named: a host name or address, with RemSound's port unless another is given.</summary>
internal sealed record RemPeerSpec(string Host, int Port)
{
	public override string ToString() => Port == RemPacket.DefaultPort ? Host : $"{Host}:{Port}";

	/// <summary>
	/// Parses "host", "host:port" or "[v6]:port". Only the characters a host name or
	/// address can contain are accepted, because this text came from a settings field.
	/// </summary>
	public static bool TryParse(string text, out RemPeerSpec peer)
	{
		peer = null!;
		text = (text ?? "").Trim();
		if (text.Length is 0 or > 255)
		{
			return false;
		}
		var host = text;
		var port = RemPacket.DefaultPort;
		var colon = text.LastIndexOf(':');
		if (colon > 0 && text.IndexOf(':') == colon)
		{
			if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535)
			{
				return false;
			}
			host = text[..colon];
		}
		if (host.Length == 0 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
		{
			return false;
		}
		peer = new RemPeerSpec(host, port);
		return true;
	}
}

internal delegate void RemAudioPacketHandler(RemPacketType type, ushort streamId, uint sequence, ReadOnlySpan<byte> payload, IPEndPoint source);

internal sealed class RemSoundLinkOptions
{
	public int LocalPort { get; init; } = RemPacket.DefaultPort;
	public int DiscoveryPort { get; init; } = RemPacket.DiscoveryPort;
	public string DeviceName { get; init; } = Environment.MachineName;
	public IReadOnlyList<RemPeerSpec> Peers { get; init; } = [];
	public IReadOnlyList<string> PeerNames { get; init; } = [];
	/// <summary>
	/// A V2 relay to join as a group member. The relay is also a send/receive
	/// target; group framing switches on once its member list arrives.
	/// </summary>
	public RemPeerSpec? RelayHost { get; init; }
	/// <summary>
	/// Client ids ticked at startup. Null means the legacy hello with no tick
	/// list: everyone in the group. Set by the user in the relay members dialog.
	/// </summary>
	public IReadOnlyList<byte[]>? RelayTicks { get; init; }
	public string Password { get; init; } = "";
	public bool CanSend { get; init; }
	public bool CanReceive { get; init; }
	public bool AllowRemoteControl { get; init; }
	public string Role { get; init; } = "subscriber";
}

/// <summary>
/// The one UDP socket a RemSound peer uses for everything: audio in both directions,
/// heartbeats (1 Hz ping, pong echoing the originator's clock), the relay's address
/// check, and sealed remote-control commands. Keeping all of it on one socket is what
/// holds a NAT pinhole open and claims a slot on the RemSound relay.
///
/// Peers come from two places: addresses the user typed (which may include a relay),
/// and devices the user picked by name, which discovery maps to their current address
/// so a phone that changes address on the next Wi-Fi lease keeps working.
///
/// A peer that turns out to be a V2 relay can also be joined as a group: the link
/// then speaks group framing to it (see RemSoundRelayGroup.cs) once the relay's
/// member list arrives, and falls back to ordinary packets if the lists stop.
/// </summary>
internal sealed class RemSoundLink : IDisposable
{
	private static readonly TimeSpan HealthyPongAge = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan HealthyAudioAge = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan ResolveInterval = TimeSpan.FromSeconds(30);

	private readonly RemSoundLinkOptions _options;
	private readonly Socket _socket;
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private readonly object _healthGate = new();
	private readonly Dictionary<IPAddress, PeerHealth> _health = [];
	private readonly RemControlGuard _controlGuard = new();
	private readonly AesGcm _controlGcm;
	private readonly CancellationTokenSource _cts = new();
	private readonly List<RelayGroup> _relayGroups = [];
	private IDisposable? _relayCommands;
	private RemSoundDiscovery? _discovery;
	private Thread? _receiveThread;
	private volatile IPEndPoint[] _resolvedPeers = [];
	private volatile IPEndPoint[] _targets = [];
	private volatile IPAddress[] _namedAddresses = [];
	private long _heartbeatSequence;
	private long _packetsIn;
	private long _packetsOut;
	private long _sendErrors;
	private bool _disposed;

	public RemSoundLink(RemSoundLinkOptions options)
	{
		_options = options;
		if (string.IsNullOrEmpty(options.Password))
		{
			// RemSound has no unencrypted mode: every port refuses audio without a key.
			throw new ArgumentException("RemSound connections need an encryption password. Set the same password here and on the other device.");
		}
		Key = RemCrypto.DeriveKey(options.Password);
		Fingerprint = RemCrypto.Fingerprint(options.Password);
		_controlGcm = new AesGcm(Key, RemCrypto.TagBytes);
		_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
		{
			SendBufferSize = 512 * 1024,
			ReceiveBufferSize = 1024 * 1024,
		};
		try
		{
			// Without this, an ICMP "port unreachable" from a peer that is not running
			// yet surfaces as an exception on the next receive and ends the loop.
			const int SioUdpConnReset = -1744830452;
			_socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
		}
		catch (SocketException)
		{
		}
		try
		{
			_socket.Bind(new IPEndPoint(IPAddress.Any, options.LocalPort));
		}
		catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
		{
			_socket.Dispose();
			_controlGcm.Dispose();
			throw new InvalidOperationException(
				$"UDP port {options.LocalPort} is already in use, most likely by the RemSound app on this computer. Close RemSound here, or use it instead of this add-on on this computer.");
		}
	}

	public byte[] Key { get; }
	public byte[] Fingerprint { get; }
	public RemAudioPacketHandler? AudioPacketReceived { get; set; }
	public Action<RemControlKind, sbyte, string>? ControlReceived { get; set; }
	public IReadOnlyList<IPEndPoint> Targets => _targets;
	public long PacketsIn => Interlocked.Read(ref _packetsIn);
	public long PacketsOut => Interlocked.Read(ref _packetsOut);
	public long SendErrors => Interlocked.Read(ref _sendErrors);
	public IReadOnlyList<RemSoundPeer> DiscoveredPeers => _discovery?.Peers ?? [];
	/// <summary>Raised when a relay group member leaves, so the receiver drops their streams.</summary>
	public Action<IPAddress>? MemberDeparted { get; set; }
	/// <summary>True while at least one relay's member list is fresh: audio goes out in group framing.</summary>
	public bool GroupModeActive => _relayGroups.Any(group => group.IsActive(_clock.Elapsed));

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await ResolvePeersAsync(cancellationToken);
		if (_options.RelayHost is { } relaySpec)
		{
			var relay = await ResolvePeerAsync(relaySpec, cancellationToken);
			if (relay is not null)
			{
				if (!_resolvedPeers.Any(peer => peer.Equals(relay)))
				{
					// The relay is a target and an allowed source like any peer; it
					// just also gets hellos and group-framed audio.
					_resolvedPeers = [.. _resolvedPeers, relay];
				}
				var joined = new RelayGroup(this, relay, RemSoundGroupIdentity.Resolve(), _options.RelayTicks);
				_relayGroups.Add(joined);
				// The first hello leaves at once; the heartbeat loop repeats it.
				SendHello(joined);
			}
			else
			{
				JsonLog.Write("warning", $"Could not look up the RemSound relay {_options.RelayHost}: peer-to-peer only.");
			}
		}
		_relayCommands = LiveControls.Register(HandleRelayCommand);
		if (_options.DiscoveryPort > 0)
		{
			_discovery = new RemSoundDiscovery(_options.DeviceName, _options.DiscoveryPort, _options.LocalPort, _options.CanSend, _options.CanReceive);
			_discovery.SetUnicastTargets(_resolvedPeers.Select(peer => peer.Address));
			_discovery.PeersChanged += OnPeersChanged;
			_discovery.Start();
		}
		RebuildTargets();
		_receiveThread = new Thread(ReceiveLoop)
		{
			IsBackground = true,
			Name = "RemSound receive",
			Priority = ThreadPriority.Highest,
		};
		_receiveThread.Start();
		_ = Task.Run(() => HeartbeatLoopAsync(_cts.Token), CancellationToken.None);
		JsonLog.Write("status", $"RemSound ready on UDP port {_options.LocalPort}.", new Dictionary<string, object?>
		{
			["transport"] = "remsound",
			["role"] = _options.Role,
			["local_port"] = _options.LocalPort,
			["peers"] = _options.Peers.Select(peer => peer.ToString()).ToList(),
			["peer_names"] = _options.PeerNames,
			["discovery"] = _options.DiscoveryPort > 0,
			["remote_control_accepted"] = _options.AllowRemoteControl,
		});
		if (_options.CanSend && _options.Peers.Count == 0 && _options.PeerNames.Count == 0 && _options.RelayHost is null)
		{
			JsonLog.Write("warning", "No RemSound devices are chosen to send to. Add the other device's address, or choose it from the devices found on this network.");
		}
	}

	/// <summary>
	/// True when audio from this address should play. With nothing configured, any
	/// device that knows the password is welcome, which is what a phone user expects
	/// on first run; once devices are chosen, only they are heard.
	/// </summary>
	public bool IsAllowedSource(IPAddress address)
	{
		if (_options.Peers.Count == 0 && _options.PeerNames.Count == 0)
		{
			return true;
		}
		return _resolvedPeers.Any(peer => peer.Address.Equals(address)) || _namedAddresses.Contains(address);
	}

	public string DescribePeer(IPAddress address)
	{
		foreach (var group in _relayGroups)
		{
			if (group.MemberName(address) is { } memberName)
			{
				return memberName;
			}
		}
		var discovered = DiscoveredPeers.FirstOrDefault(peer => peer.Address.Equals(address));
		if (discovered is not null)
		{
			return discovered.Name;
		}
		var configured = _options.Peers.FirstOrDefault(peer => IPAddress.TryParse(peer.Host, out var parsed) && parsed.Equals(address));
		return configured?.Host ?? address.ToString();
	}

	public void SendToTargets(ReadOnlySpan<byte> packet)
	{
		var hasHeader = RemPacket.TryReadHeader(packet, out var type, out _, out _);
		foreach (var target in _targets)
		{
			var group = GroupFor(target);
			if (group is not null && group.IsActive(_clock.Elapsed) && hasHeader)
			{
				// One copy to the relay in group framing; the relay makes the rest.
				SendTo(RemGroupPacket.Wrap(packet, group.ClientId), target);
				if (group.DuplicateOrdinary(type))
				{
					// Beside the group we may also hold one of the relay's ordinary
					// pair slots, for a phone or an older app. It gets the same
					// audio in ordinary form while it is there.
					SendTo(packet, target);
				}
				continue;
			}
			SendTo(packet, target);
		}
	}

	private RelayGroup? GroupFor(IPEndPoint target) =>
		_relayGroups.FirstOrDefault(group => group.Relay.Equals(target));

	public void SendTo(ReadOnlySpan<byte> packet, IPEndPoint target)
	{
		try
		{
			_socket.SendTo(packet, SocketFlags.None, target);
			Interlocked.Increment(ref _packetsOut);
		}
		catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
		{
			Interlocked.Increment(ref _sendErrors);
		}
	}

	/// <summary>Sends a sealed remote-control command to every chosen device.</summary>
	public int SendControl(RemControlKind kind, sbyte delta)
	{
		var sealedPayload = RemControlSealing.Seal(Key, kind, delta, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		var packet = new byte[RemPacket.HeaderSize + sealedPayload.Length];
		RemPacket.WriteHeader(packet, RemPacketType.Control, 1, (uint)Interlocked.Increment(ref _heartbeatSequence));
		sealedPayload.CopyTo(packet.AsSpan(RemPacket.HeaderSize));
		SendToTargets(packet);
		return _targets.Length;
	}

	/// <summary>Counts audio as proof of life, so a peer that sends but never answers pings still counts as connected.</summary>
	public void NoteAudioFrom(IPAddress address)
	{
		lock (_healthGate)
		{
			GetHealth(address).LastAudio = _clock.Elapsed;
		}
	}

	private PeerHealth GetHealth(IPAddress address)
	{
		if (!_health.TryGetValue(address, out var health))
		{
			health = new PeerHealth();
			_health[address] = health;
		}
		return health;
	}

	private void OnPeersChanged()
	{
		RebuildTargets();
		RemSoundDiscovery.WritePeers(DiscoveredPeers, "RemSound devices on this network changed.");
	}

	private void RebuildTargets()
	{
		var named = new List<IPEndPoint>();
		if (_options.PeerNames.Count > 0)
		{
			foreach (var peer in DiscoveredPeers)
			{
				if (_options.PeerNames.Any(name => string.Equals(name, peer.Name, StringComparison.CurrentCultureIgnoreCase)))
				{
					named.Add(new IPEndPoint(peer.Address, peer.AudioPort));
				}
			}
		}
		_namedAddresses = named.Select(endpoint => endpoint.Address).Distinct().ToArray();
		var targets = _resolvedPeers.Concat(named).Distinct().ToArray();
		var changed = !targets.SequenceEqual(_targets);
		_targets = targets;
		if (changed)
		{
			JsonLog.Write("diagnostic", "RemSound targets changed.", new Dictionary<string, object?>
			{
				["targets"] = targets.Select(target => target.ToString()).ToList(),
			});
		}
	}

	private async Task<IPEndPoint?> ResolvePeerAsync(RemPeerSpec peer, CancellationToken cancellationToken)
	{
		try
		{
			if (IPAddress.TryParse(peer.Host, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork)
			{
				return new IPEndPoint(literal, peer.Port);
			}
			if (!IPAddress.TryParse(peer.Host, out _))
			{
				var addresses = await Dns.GetHostAddressesAsync(peer.Host, AddressFamily.InterNetwork, cancellationToken);
				if (addresses.Length > 0)
				{
					return new IPEndPoint(addresses[0], peer.Port);
				}
			}
		}
		catch (Exception ex) when (ex is SocketException or ArgumentException)
		{
			JsonLog.Write("diagnostic", $"Could not look up RemSound relay {peer}: {ex.Message}");
		}
		return null;
	}

	private async Task ResolvePeersAsync(CancellationToken cancellationToken)
	{
		var resolved = new List<IPEndPoint>();
		foreach (var peer in _options.Peers)
		{
			try
			{
				if (IPAddress.TryParse(peer.Host, out var literal))
				{
					if (literal.AddressFamily == AddressFamily.InterNetwork)
					{
						resolved.Add(new IPEndPoint(literal, peer.Port));
					}
					continue;
				}
				var addresses = await Dns.GetHostAddressesAsync(peer.Host, AddressFamily.InterNetwork, cancellationToken);
				if (addresses.Length > 0)
				{
					resolved.Add(new IPEndPoint(addresses[0], peer.Port));
				}
			}
			catch (Exception ex) when (ex is SocketException or ArgumentException)
			{
				JsonLog.Write("diagnostic", $"Could not look up RemSound peer {peer}: {ex.Message}");
			}
		}
		_resolvedPeers = resolved.Concat(_relayGroups.Select(group => group.Relay)).Distinct().ToArray();
		_discovery?.SetUnicastTargets(_resolvedPeers.Select(endpoint => endpoint.Address));
	}

	private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
	{
		var nextResolve = ResolveInterval;
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			if (_clock.Elapsed >= nextResolve && _options.Peers.Count > 0)
			{
				nextResolve = _clock.Elapsed + ResolveInterval;
				await ResolvePeersAsync(cancellationToken);
				RebuildTargets();
			}
			var nowMs = _clock.ElapsedMilliseconds;
			var ping = RemPacket.BuildHeartbeat(RemHeartbeatKind.Ping, (uint)Interlocked.Increment(ref _heartbeatSequence), nowMs);
			foreach (var target in _targets)
			{
				lock (_healthGate)
				{
					GetHealth(target.Address).FirstPing ??= _clock.Elapsed;
				}
				// The ordinary heartbeat is what claims a pair slot on a relay for
				// a phone or an older app; it is always sent, group or not.
				SendTo(ping, target);
			}
			foreach (var group in _relayGroups)
			{
				// The group heartbeat rides in group framing so the relay carries
				// it to the members, whose pongs come back the same way.
				var groupPing = RemGroupPacket.Wrap(ping, group.ClientId);
				SendTo(groupPing, group.Relay);
				group.NotePingSent(nowMs);
				if (_clock.Elapsed - group.LastHelloSent >= RemGroupPacket.HelloInterval)
				{
					SendHello(group);
				}
			}
			UpdateHealth();
		}
	}

	/// <summary>
	/// RemSound's hysteresis: connected once a pong is at most 2 s old or audio at most
	/// 3 s old; lost only when neither has been seen for 5 s. A two-second Wi-Fi or VPN
	/// stall therefore changes nothing, instead of announcing a disconnect and a
	/// reconnect.
	/// </summary>
	private void UpdateHealth()
	{
		var now = _clock.Elapsed;
		var changes = new List<(IPAddress Address, bool Connected, int? Rtt)>();
		lock (_healthGate)
		{
			var targetAddresses = _targets.Select(target => target.Address).ToHashSet();
			foreach (var (address, health) in _health.ToList())
			{
				var pongAge = health.LastPong is { } pong ? now - pong : TimeSpan.MaxValue;
				var audioAge = health.LastAudio is { } audio ? now - audio : TimeSpan.MaxValue;
				var healthy = pongAge <= HealthyPongAge || audioAge <= HealthyAudioAge;
				var lost = pongAge > LostAfter && audioAge > LostAfter;
				if (!health.Connected && healthy)
				{
					health.Connected = true;
					changes.Add((address, true, health.RttMs));
				}
				else if (health.Connected && lost)
				{
					health.Connected = false;
					changes.Add((address, false, null));
				}
				if (!health.Connected && lost && !targetAddresses.Contains(address))
				{
					_health.Remove(address);
				}
			}
		}
		foreach (var (address, connected, rtt) in changes)
		{
			var name = DescribePeer(address);
			if (connected)
			{
				JsonLog.Write("connected", $"Connected to {name}.", new Dictionary<string, object?>
				{
					["role"] = _options.Role,
					["transport"] = "remsound",
					["peer"] = name,
					["address"] = address.ToString(),
					["rtt_ms"] = rtt,
				});
			}
			else
			{
				JsonLog.Write("peer_lost", $"Lost contact with {name}.", new Dictionary<string, object?>
				{
					["peer"] = name,
					["address"] = address.ToString(),
				});
			}
		}
	}

	public IReadOnlyList<Dictionary<string, object?>> HealthSnapshot()
	{
		var now = _clock.Elapsed;
		lock (_healthGate)
		{
			return _health.Select(pair => new Dictionary<string, object?>
			{
				["peer"] = DescribePeer(pair.Key),
				["address"] = pair.Key.ToString(),
				["connected"] = pair.Value.Connected,
				["rtt_ms"] = pair.Value.RttMs,
				["last_audio_s"] = pair.Value.LastAudio is { } audio ? Math.Round((now - audio).TotalSeconds, 1) : null,
			}).ToList();
		}
	}

	private void ReceiveLoop()
	{
		using var boost = new WindowsAudioThreadBoost("Pro Audio");
		var buffer = new byte[65536];
		EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
		while (!_cts.IsCancellationRequested)
		{
			int length;
			try
			{
				length = _socket.ReceiveFrom(buffer, ref remote);
			}
			catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
			{
				continue;
			}
			catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
			{
				return;
			}
			var source = (IPEndPoint)remote;
			var packet = buffer.AsSpan(0, length);
			if (packet.Length < RemPacket.HeaderSize || BinaryPrimitives.ReadInt32LittleEndian(packet) != RemPacket.Magic)
			{
				continue;
			}
			Interlocked.Increment(ref _packetsIn);
			try
			{
				if (packet[4] == RemGroupPacket.Version)
				{
					DispatchGroup(packet, source);
				}
				else if (RemPacket.TryReadHeader(packet, out var type, out var streamId, out var sequence))
				{
					Dispatch(type, streamId, sequence, packet, source);
				}
			}
			catch (Exception ex)
			{
				// One bad packet must never end reception.
				JsonLog.Write("diagnostic", $"RemSound packet from {source} was not handled: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Handles version-2 packets: the relay's lobby traffic (all-zero client id)
	/// and group members' audio, forwarded by the relay with their id attached.
	/// </summary>
	private void DispatchGroup(ReadOnlySpan<byte> packet, IPEndPoint source)
	{
		if (!RemGroupPacket.TryReadHeader(packet, out var type, out var streamId, out var sequence, out var clientId))
		{
			return;
		}
		var group = _relayGroups.FirstOrDefault(g => g.Relay.Equals(source));
		if (RemGroupPacket.IsRelayId(clientId))
		{
			if (group is null)
			{
				return;
			}
			var payload = packet[RemGroupPacket.HeaderSize..];
			switch (type)
			{
				case RemPacketType.Roster:
					group.OnRoster(payload, _clock.Elapsed);
					break;
				case RemPacketType.Full:
					// Payload: current count, maximum.
					var detail = new Dictionary<string, object?>
					{
						["relay"] = group.Relay.ToString(),
					};
					if (payload.Length >= 2)
					{
						detail["members"] = payload[0];
						detail["capacity"] = payload[1];
						JsonLog.Write("warning", $"The relay is full ({payload[0]} of {payload[1]} places taken).", detail);
					}
					else
					{
						JsonLog.Write("warning", "The relay has no room for this computer.", detail);
					}
					break;
			}
			return;
		}
		// A member's packet always arrives via the relay, and group behavior is
		// only on while its member list is fresh.
		if (group is null || !group.IsActive(_clock.Elapsed))
		{
			return;
		}
		var member = new IPEndPoint(RemGroupPacket.MemberAddress(clientId, group.RosterIds()), source.Port);
		var memberPayload = packet[RemGroupPacket.HeaderSize..];
		switch (type)
		{
			case RemPacketType.Heartbeat:
				if (!RemPacket.TryReadHeartbeat(memberPayload, out var kind, out var originatorTick))
				{
					return;
				}
				if (kind == RemHeartbeatKind.Ping)
				{
					// The pong goes back in group framing so the relay carries it
					// to the member that pinged, with their clock on it.
					var pong = RemGroupPacket.Wrap(
						RemPacket.BuildHeartbeat(RemHeartbeatKind.Pong, (uint)Interlocked.Increment(ref _heartbeatSequence), originatorTick),
						group.ClientId);
					SendTo(pong, group.Relay);
					return;
				}
				group.NotePong(member.Address, originatorTick, _clock.ElapsedMilliseconds);
				return;
			case RemPacketType.AddrCheck:
				// Address checks are echoed exactly as they came, never in group
				// framing; the ordinary path already does that. A member's check
				// arriving here would be a relay bug.
				return;
			case RemPacketType.Control:
				// Ticking works both ways: the relay enforces the same rule, but a
				// misbehaving relay must not talk past the user's choice.
				if (group.IsTicked(clientId))
				{
					HandleGroupControl(memberPayload, member);
				}
				return;
			case RemPacketType.Format:
			case RemPacketType.Audio:
				if (group.IsTicked(clientId))
				{
					AudioPacketReceived?.Invoke(type, streamId, sequence, memberPayload, member);
				}
				return;
		}
	}

	private void HandleGroupControl(ReadOnlySpan<byte> payload, IPEndPoint member)
	{
		var name = DescribePeer(member.Address);
		if (!_controlGuard.TryAccept(_controlGcm, payload, DateTime.UtcNow, out var kind, out var delta, out var reason))
		{
			JsonLog.Write("diagnostic", $"Ignored a remote volume command from {name}: {reason}.");
			return;
		}
		if (!RemControlPolicy.AllowsInbound(kind, _options.AllowRemoteControl))
		{
			JsonLog.Write("diagnostic", RemControlPolicy.IsSystemVolume(kind)
				? $"Ignored a Windows volume command from {name}: a RemSound peer never changes this computer's volume."
				: $"Ignored a remote volume command from {name}: remote volume control is turned off on this computer.");
			return;
		}
		ControlReceived?.Invoke(kind, delta, name);
	}

	private void SendHello(RelayGroup group)
	{
		SendTo(RemGroupPacket.BuildHello(group.ClientId, _options.DeviceName, Fingerprint, group.Ticks), group.Relay);
		group.LastHelloSent = _clock.Elapsed;
	}

	/// <summary>
	/// Live tick changes from the add-on's relay members dialog. The hello goes
	/// out at once; the relay enforces the new tick list from there.
	/// </summary>
	private bool HandleRelayCommand(string command, string argument)
	{
		if (_relayGroups.Count == 0)
		{
			return false;
		}
		switch (command)
		{
			case "relay-set-ticks":
				var ticks = ParseTickIds(argument);
				if (ticks is null)
				{
					JsonLog.Write("diagnostic", "Ignored relay ticks: each id must be 32 hexadecimal characters.");
					return true;
				}
				foreach (var group in _relayGroups)
				{
					group.Ticks = ticks;
					SendHello(group);
				}
				EmitTicks(ticks);
				return true;
			case "relay-ticks-clear":
				foreach (var group in _relayGroups)
				{
					group.Ticks = null;
					SendHello(group);
				}
				EmitTicks(null);
				return true;
			default:
				return false;
		}
	}

	private static List<byte[]>? ParseTickIds(string argument)
	{
		// An empty argument is the explicit empty tick list: nobody is heard.
		// The legacy "everyone" hello is relay-ticks-clear, never this command.
		var ids = new List<byte[]>();
		foreach (var entry in argument.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			byte[] id;
			try
			{
				id = Convert.FromHexString(entry);
			}
			catch (FormatException)
			{
				return null;
			}
			if (id.Length != RemGroupPacket.ClientIdBytes || id.All(b => b == 0) || ids.Any(seen => seen.SequenceEqual(id)))
			{
				return null;
			}
			ids.Add(id);
		}
		return ids.Count <= RemGroupPacket.MaxTicked ? ids : null;
	}

	private void EmitTicks(List<byte[]>? ticks)
	{
		JsonLog.Write("relay_ticks", ticks switch
		{
			null => "Relay ticks cleared: the whole group is heard.",
			{ Count: 0 } => "Relay ticks: nobody in the group is heard.",
			_ => $"Relay ticks: {ticks.Count} member(s) chosen.",
		}, new Dictionary<string, object?>
		{
			["ticked"] = ticks?.Select(id => Convert.ToHexString(id).ToLowerInvariant()).ToList(),
		});
	}

	private void Dispatch(RemPacketType type, ushort streamId, uint sequence, ReadOnlySpan<byte> packet, IPEndPoint source)
	{
		var payload = packet[RemPacket.HeaderSize..];
		switch (type)
		{
			case RemPacketType.Heartbeat:
				if (!RemPacket.TryReadHeartbeat(payload, out var kind, out var originatorTick))
				{
					return;
				}
				if (kind == RemHeartbeatKind.Ping)
				{
					// Reply to wherever the ping came from: a peer's audio port on a LAN,
					// or the relay's forwarding port across the internet.
					SendTo(RemPacket.BuildHeartbeat(RemHeartbeatKind.Pong, (uint)Interlocked.Increment(ref _heartbeatSequence), originatorTick), source);
					return;
				}
				var rtt = (int)Math.Max(0, _clock.ElapsedMilliseconds - originatorTick);
				lock (_healthGate)
				{
					// Pongs match by address only: NAT and the peer's own send port can
					// both change the source port.
					var health = GetHealth(source.Address);
					health.LastPong = _clock.Elapsed;
					health.RttMs = health.RttMs is { } previous ? (int)(previous * 0.7 + rtt * 0.3) : rtt;
				}
				return;
			case RemPacketType.AddrCheck:
				// The relay proves our address is really ours by sending a cookie that must
				// come back unchanged, from this socket. It comes from the relay, not a
				// chosen peer, so it is answered whatever the allow-list says.
				SendTo(packet, source);
				return;
			case RemPacketType.Control:
				HandleControl(payload, source);
				return;
			case RemPacketType.Format:
			case RemPacketType.Audio:
				if (!IsAllowedSource(source.Address))
				{
					return;
				}
				AudioPacketReceived?.Invoke(type, streamId, sequence, payload, source);
				return;
			default:
				// KeepAlive and anything newer are dropped quietly.
				return;
		}
	}

	private void HandleControl(ReadOnlySpan<byte> payload, IPEndPoint source)
	{
		var name = DescribePeer(source.Address);
		if (!IsAllowedSource(source.Address) && !_targets.Any(target => target.Address.Equals(source.Address)))
		{
			JsonLog.Write("diagnostic", $"Ignored a remote volume command from {name}: not a chosen device.");
			return;
		}
		if (!_controlGuard.TryAccept(_controlGcm, payload, DateTime.UtcNow, out var kind, out var delta, out var reason))
		{
			JsonLog.Write("diagnostic", $"Ignored a remote volume command from {name}: {reason}.");
			return;
		}
		if (!RemControlPolicy.AllowsInbound(kind, _options.AllowRemoteControl))
		{
			JsonLog.Write("diagnostic", RemControlPolicy.IsSystemVolume(kind)
				? $"Ignored a Windows volume command from {name}: a RemSound peer never changes this computer's volume."
				: $"Ignored a remote volume command from {name}: remote volume control is turned off on this computer.");
			return;
		}
		ControlReceived?.Invoke(kind, delta, name);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		foreach (var group in _relayGroups)
		{
			try
			{
				SendTo(RemGroupPacket.BuildBye(group.ClientId), group.Relay);
			}
			catch (Exception)
			{
				// Leaving must not fail leaving.
			}
		}
		_relayCommands?.Dispose();
		_cts.Cancel();
		_discovery?.Dispose();
		_socket.Dispose();
		_receiveThread?.Join(TimeSpan.FromSeconds(1));
		_controlGcm.Dispose();
		_cts.Dispose();
	}

	private sealed class PeerHealth
	{
		public TimeSpan? FirstPing { get; set; }
		public TimeSpan? LastPong { get; set; }
		public TimeSpan? LastAudio { get; set; }
		public int? RttMs { get; set; }
		public bool Connected { get; set; }
	}

	/// <summary>
	/// One V2 relay joined as a group member. Group behavior is on only while the
	/// relay's member list is fresh (5 seconds); until the first list arrives the
	/// relay is an ordinary peer, and if the lists stop everything falls back.
	/// </summary>
	private sealed class RelayGroup
	{
		private readonly RemSoundLink _link;
		private readonly Queue<long> _recentPingTicks = new();
		private bool _joined;

		public RelayGroup(RemSoundLink link, IPEndPoint relay, byte[] clientId, IReadOnlyList<byte[]>? ticks)
		{
			_link = link;
			Relay = relay;
			ClientId = clientId;
			Ticks = ticks is null ? null : [.. ticks];
			LastHelloSent = TimeSpan.MinValue;
		}

		public IPEndPoint Relay { get; }
		public byte[] ClientId { get; }
		/// <summary>Null: the legacy 40-byte hello, "everyone in my group".</summary>
		public List<byte[]>? Ticks { get; set; }
		public List<RemRosterMember> Members { get; private set; } = [];
		public byte ListFlags { get; private set; }
		public TimeSpan? LastRoster { get; private set; }
		public TimeSpan LastHelloSent { get; set; }

		public bool IsActive(TimeSpan now) =>
			LastRoster is { } seen && now - seen <= RemGroupPacket.RosterFreshFor;

		public bool HasPairSlot => (ListFlags & RemGroupPacket.ListHasPairSlotFlag) != 0;

		/// <summary>
		/// Audio, formats and control also go out in ordinary form while we hold
		/// one of the relay's pair slots beside a phone or an older app. The pair
		/// partner sends no tick list, so it counts as having ticked us; we count
		/// as having ticked it exactly when we sent no tick list either (the null
		/// "everyone" hello). With explicit ticks the partner is not on our list,
		/// so no ordinary copy goes out: the mutual-tick rule, applied evenly.
		/// </summary>
		public bool DuplicateOrdinary(RemPacketType type) =>
			HasPairSlot && Ticks is null && type is RemPacketType.Format or RemPacketType.Audio or RemPacketType.Control;

		public bool IsTicked(byte[] clientId) =>
			Ticks is null || Ticks.Any(ticked => ticked.SequenceEqual(clientId));

		public IEnumerable<byte[]> RosterIds() => Members.Select(member => member.ClientId);

		public string? MemberName(IPAddress address)
		{
			foreach (var member in Members)
			{
				if (RemGroupPacket.MemberAddress(member.ClientId, RosterIds()).Equals(address))
				{
					return member.Name;
				}
			}
			return null;
		}

		public void NotePingSent(long tickMs)
		{
			_recentPingTicks.Enqueue(tickMs);
			while (_recentPingTicks.Count > 4)
			{
				_recentPingTicks.Dequeue();
			}
		}

		/// <summary>
		/// Counts a pong only if it answers one of our own recent pings: every
		/// member's pong reaches every member, each carrying whoever pinged.
		/// </summary>
		public void NotePong(IPAddress member, long originatorTickMs, long nowTickMs)
		{
			if (!_recentPingTicks.Contains(originatorTickMs))
			{
				return;
			}
			var rtt = nowTickMs - originatorTickMs;
			if (rtt < 0 || rtt > 30_000)
			{
				return;
			}
			lock (_link._healthGate)
			{
				var health = _link.GetHealth(member);
				health.LastPong = _link._clock.Elapsed;
				health.RttMs = health.RttMs is { } previous ? (int)(previous * 0.7 + rtt * 0.3) : (int)rtt;
			}
		}

		public void OnRoster(ReadOnlySpan<byte> payload, TimeSpan now)
		{
			if (!RemRoster.TryRead(payload, out var members, out var listFlags))
			{
				return;
			}
			// We are in our own list; leave ourselves out.
			members.RemoveAll(member => member.ClientId.SequenceEqual(ClientId));
			var nowIds = members.Select(member => member.IdHex).ToHashSet();
			foreach (var departed in Members)
			{
				if (!nowIds.Contains(departed.IdHex))
				{
					// Someone left: drop their streams so a stale session cannot
					// keep playing after they are gone.
					var oldIds = Members.Select(member => member.ClientId);
					_link.MemberDeparted?.Invoke(RemGroupPacket.MemberAddress(departed.ClientId, oldIds));
				}
			}
			Members = members;
			ListFlags = listFlags;
			LastRoster = now;
			var first = !_joined;
			_joined = true;
			JsonLog.Write("relay_members", first
				? $"Joined the relay group: {members.Count} member(s)."
				: $"Relay group: {members.Count} member(s).", new Dictionary<string, object?>
			{
				["relay"] = Relay.ToString(),
				["joined"] = first,
				["has_pair_slot"] = HasPairSlot,
				["members"] = members.Select(member => new Dictionary<string, object?>
				{
					["id"] = member.IdHex,
					["name"] = member.Name,
					["ticked_by_them"] = member.TickedUs,
					["ticked_by_me"] = IsTicked(member.ClientId),
				}).ToList(),
			});
		}
	}
}
