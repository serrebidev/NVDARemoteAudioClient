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

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await ResolvePeersAsync(cancellationToken);
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
		if (_options.CanSend && _options.Peers.Count == 0 && _options.PeerNames.Count == 0)
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
		foreach (var target in _targets)
		{
			SendTo(packet, target);
		}
	}

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
		_resolvedPeers = resolved.Distinct().ToArray();
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
			var ping = RemPacket.BuildHeartbeat(RemHeartbeatKind.Ping, (uint)Interlocked.Increment(ref _heartbeatSequence), _clock.ElapsedMilliseconds);
			foreach (var target in _targets)
			{
				lock (_healthGate)
				{
					GetHealth(target.Address).FirstPing ??= _clock.Elapsed;
				}
				SendTo(ping, target);
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
			if (!RemPacket.TryReadHeader(packet, out var type, out var streamId, out var sequence))
			{
				continue;
			}
			Interlocked.Increment(ref _packetsIn);
			try
			{
				Dispatch(type, streamId, sequence, packet, source);
			}
			catch (Exception ex)
			{
				// One bad packet must never end reception.
				JsonLog.Write("diagnostic", $"RemSound packet from {source} was not handled: {ex.GetType().Name}: {ex.Message}");
			}
		}
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
		if (!_options.AllowRemoteControl)
		{
			JsonLog.Write("diagnostic", $"Ignored a remote volume command from {name}: remote volume control is turned off on this computer.");
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
}
