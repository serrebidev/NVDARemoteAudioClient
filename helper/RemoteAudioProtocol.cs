using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NVDARemoteAudioHelper;

internal enum ConnectionRole
{
	Publisher,
	Subscriber,
}

internal enum UdpPacketKind : byte
{
	Register = 1,
	RegisterAck = 2,
	Heartbeat = 3,
	AudioData = 4,
}

internal sealed class RemoteAudioSession : IAsyncDisposable
{
	private const int HandshakeMaxBytes = 64 * 1024;

	private static readonly Encoding WireEncoding = new UTF8Encoding(false);
	private readonly TcpClient _tcp;
	private readonly StreamWriter _writer;
	private readonly UdpClient _udp;
	private readonly CancellationTokenSource _lifetimeCts;
	private readonly Task _tcpHeartbeatTask;
	private readonly Task _udpHeartbeatTask;
	private readonly SemaphoreSlim _udpSendLock = new(1, 1);
	private readonly byte[] _sessionId;
	private readonly NetworkPriority _networkPriority;
	private readonly byte[] _audioPacketBuffer;
	private long _maxUdpSendTicks;
	private long _lastReceiveTicks;
	private long _maxReceiveGapTicks;

	public int MaxPayloadBytes { get; }
	public ConnectionRole Role { get; }
	public string SessionIdHex { get; }

	private RemoteAudioSession(
		TcpClient tcp,
		StreamWriter writer,
		UdpClient udp,
		byte[] sessionId,
		int maxPayloadBytes,
		int tcpHeartbeatIntervalMs,
		int udpSessionTimeoutMs,
		NetworkPriority networkPriority,
		ConnectionRole role,
		CancellationToken externalToken)
	{
		_tcp = tcp;
		_writer = writer;
		_udp = udp;
		_sessionId = sessionId;
		_networkPriority = networkPriority;
		_audioPacketBuffer = new byte[UdpPacket.AudioHeaderLength + maxPayloadBytes];
		MaxPayloadBytes = maxPayloadBytes;
		Role = role;
		SessionIdHex = Convert.ToHexString(sessionId).ToLowerInvariant();
		_lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
		_tcpHeartbeatTask = Task.Run(() => TcpHeartbeatLoopAsync(tcpHeartbeatIntervalMs, _lifetimeCts.Token));
		// Heartbeat at a third of the server's UDP timeout, but no slower than every 3 seconds.
		// At least one heartbeat per second-third covers most NAT/path drop scenarios.
		var udpHeartbeatIntervalMs = Math.Min(3000, Math.Max(500, udpSessionTimeoutMs / 3));
		_udpHeartbeatTask = Task.Run(() => UdpHeartbeatLoopAsync(udpHeartbeatIntervalMs, _lifetimeCts.Token));
	}

	public static async Task<RemoteAudioSession> ConnectAsync(
		string host,
		int port,
		string key,
		ConnectionRole role,
		CancellationToken cancellationToken)
	{
		var tcp = new TcpClient { NoDelay = true };
		await tcp.ConnectAsync(host, port, cancellationToken);

		var stream = tcp.GetStream();
		var writer = new StreamWriter(stream, WireEncoding, leaveOpen: true)
		{
			AutoFlush = true,
			NewLine = "\n",
		};

		var roleText = role == ConnectionRole.Publisher ? "publisher" : "subscriber";
		await writer.WriteLineAsync(JsonSerializer.Serialize(new { role = roleText, key }));

		// Bounded line read so a hostile server cannot stream unlimited bytes without a newline
		// and exhaust our memory.
		var responseLine = await ReadLineBoundedAsync(stream, HandshakeMaxBytes, cancellationToken);
		var response = JsonSerializer.Deserialize<HandshakeResponse>(responseLine)
			?? throw new IOException("The audio server returned an empty handshake response.");

		if (!string.Equals(response.Status, "ok", StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException(response.Message ?? "The audio server rejected the handshake.");
		}

		var sessionId = ParseSessionId(response.SessionId);
		var udpPort = response.UdpPort > 0 && response.UdpPort <= 65535 ? response.UdpPort : port;
		var rawMaxPayload = response.UdpAudioPayloadMaxBytes > 0 ? response.UdpAudioPayloadMaxBytes : 1200;
		// Cap the server's per-packet limit to a single MTU. A hostile/buggy server could otherwise force
		// large allocations on every send.
		var maxPayload = Math.Clamp(rawMaxPayload, 64, 1500);
		var rawTcpHeartbeatMs = response.TcpHeartbeatIntervalMs > 0 ? response.TcpHeartbeatIntervalMs : 5000;
		// Floor the TCP heartbeat interval. A server returning 0/1 would spin our heartbeat loop tight.
		var tcpHeartbeatMs = Math.Max(500, rawTcpHeartbeatMs);
		var udpTimeoutMs = response.UdpSessionTimeoutMs > 0 ? response.UdpSessionTimeoutMs : 15000;

		var udp = new UdpClient();
		udp.Client.SendBufferSize = 256 * 1024;
		udp.Client.ReceiveBufferSize = 512 * 1024;
		udp.Connect(host, udpPort);
		var networkPriority = new NetworkPriority();
		networkPriority.Attach(udp.Client, message => JsonLog.Write("diagnostic", message));

		var session = new RemoteAudioSession(tcp, writer, udp, sessionId, maxPayload, tcpHeartbeatMs, udpTimeoutMs, networkPriority, role, cancellationToken);
		await session.RegisterUdpAsync(cancellationToken);

		JsonLog.Write("connected", $"Connected as {roleText}.", new Dictionary<string, object?>
		{
			["role"] = roleText,
			["session_id"] = session.SessionIdHex,
			["udp_port"] = udpPort,
			["max_payload"] = maxPayload,
		});

		return session;
	}

	public async Task SendAudioAsync(ulong sequence, ulong timestampMs, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
	{
		if (payload.Length > MaxPayloadBytes)
		{
			throw new InvalidOperationException($"Encoded Opus frame is {payload.Length} bytes, above server limit {MaxPayloadBytes}.");
		}

		await _udpSendLock.WaitAsync(cancellationToken);
		try
		{
			var packetLength = UdpPacket.WriteAudio(_audioPacketBuffer, _sessionId, sequence, timestampMs, payload.Span);
			var start = Stopwatch.GetTimestamp();
			await _udp.Client.SendAsync(_audioPacketBuffer.AsMemory(0, packetLength), SocketFlags.None, cancellationToken);
			RecordMaxTicks(ref _maxUdpSendTicks, Stopwatch.GetTimestamp() - start);
		}
		finally
		{
			_udpSendLock.Release();
		}
	}

	public async Task<UdpAudioFrame> ReceiveAudioAsync(CancellationToken cancellationToken)
	{
		while (true)
		{
			var result = await _udp.ReceiveAsync(cancellationToken);
			if (!UdpPacket.TryParse(result.Buffer, out var parsed) ||
				parsed.Kind != UdpPacketKind.AudioData ||
				!_sessionId.AsSpan().SequenceEqual(parsed.SessionId))
			{
				continue;
			}

			var now = Stopwatch.GetTimestamp();
			var previous = Interlocked.Exchange(ref _lastReceiveTicks, now);
			if (previous != 0)
			{
				RecordMaxTicks(ref _maxReceiveGapTicks, now - previous);
			}

			return new UdpAudioFrame(parsed.Sequence, parsed.TimestampMs, parsed.Payload);
		}
	}

	public double TakeMaxUdpSendMilliseconds() => TicksToMilliseconds(Interlocked.Exchange(ref _maxUdpSendTicks, 0));

	public double TakeMaxReceiveGapMilliseconds() => TicksToMilliseconds(Interlocked.Exchange(ref _maxReceiveGapTicks, 0));

	public async ValueTask DisposeAsync()
	{
		_lifetimeCts.Cancel();
		_networkPriority.Dispose();
		_tcp.Close();
		_udp.Close();

		try
		{
			await Task.WhenAll(_tcpHeartbeatTask, _udpHeartbeatTask).WaitAsync(TimeSpan.FromSeconds(2));
		}
		catch
		{
			// Shutdown should not mask the original result.
		}

		_lifetimeCts.Dispose();
		_udpSendLock.Dispose();
		_udp.Dispose();
		_tcp.Dispose();
	}

	private async Task RegisterUdpAsync(CancellationToken cancellationToken)
	{
		var registerPacket = UdpPacket.CreateControl(UdpPacketKind.Register, _sessionId);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));

		while (!timeout.Token.IsCancellationRequested)
		{
			await SendUdpAsync(registerPacket, cancellationToken);

			try
			{
				var result = await _udp.ReceiveAsync(timeout.Token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(500), timeout.Token);
				if (UdpPacket.TryParse(result.Buffer, out var parsed) &&
					parsed.Kind == UdpPacketKind.RegisterAck &&
					_sessionId.AsSpan().SequenceEqual(parsed.SessionId))
				{
					return;
				}
			}
			catch (TimeoutException)
			{
				// Retry until the overall timeout expires.
			}
		}

		throw new TimeoutException("Timed out waiting for UDP registration acknowledgement from the audio server.");
	}

	private async Task TcpHeartbeatLoopAsync(int intervalMs, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				await Task.Delay(intervalMs, cancellationToken);
				await _writer.WriteLineAsync("""{"type":"heartbeat"}""");
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			JsonLog.Write(
				"error",
				"TCP heartbeat failed: " + ex.Message,
				new Dictionary<string, object?> { ["type"] = ex.GetType().Name });
			TriggerSessionTeardown();
		}
	}

	private async Task UdpHeartbeatLoopAsync(int intervalMs, CancellationToken cancellationToken)
	{
		var heartbeat = UdpPacket.CreateControl(UdpPacketKind.Heartbeat, _sessionId);
		var register = UdpPacket.CreateControl(UdpPacketKind.Register, _sessionId);
		var sendCount = 0;

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				await Task.Delay(intervalMs, cancellationToken);
				await SendUdpAsync(heartbeat, cancellationToken);
				sendCount++;
				// Roughly every 15s (5 cycles at 3s) re-send a UDP register packet so the server
				// refreshes our session even if individual heartbeats were dropped by NAT/path.
				if (sendCount % 5 == 0)
				{
					await SendUdpAsync(register, cancellationToken);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			JsonLog.Write(
				"error",
				"UDP heartbeat failed: " + ex.Message,
				new Dictionary<string, object?> { ["type"] = ex.GetType().Name });
			TriggerSessionTeardown();
		}
	}

	private void TriggerSessionTeardown()
	{
		try
		{
			_lifetimeCts.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// Already disposed; nothing to do.
		}
	}

	private async Task SendUdpAsync(byte[] packet, CancellationToken cancellationToken)
	{
		await _udpSendLock.WaitAsync(cancellationToken);
		try
		{
			var start = Stopwatch.GetTimestamp();
			await _udp.SendAsync(packet, cancellationToken);
			RecordMaxTicks(ref _maxUdpSendTicks, Stopwatch.GetTimestamp() - start);
		}
		finally
		{
			_udpSendLock.Release();
		}
	}

	private static double TicksToMilliseconds(long ticks) =>
		ticks <= 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency;

	private static void RecordMaxTicks(ref long target, long value)
	{
		while (true)
		{
			var current = Volatile.Read(ref target);
			if (value <= current)
			{
				return;
			}

			if (Interlocked.CompareExchange(ref target, value, current) == current)
			{
				return;
			}
		}
	}

	private static async Task<string> ReadLineBoundedAsync(NetworkStream stream, int maxBytes, CancellationToken cancellationToken)
	{
		var buffer = new byte[maxBytes];
		var read = 0;
		var single = new byte[1];

		while (read < maxBytes)
		{
			var n = await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken);
			if (n == 0)
			{
				throw new IOException("The audio server closed the TCP connection during handshake.");
			}

			if (single[0] == (byte)'\n')
			{
				var length = read;
				if (length > 0 && buffer[length - 1] == (byte)'\r')
				{
					length--;
				}

				return WireEncoding.GetString(buffer, 0, length);
			}

			buffer[read++] = single[0];
		}

		throw new IOException($"Audio server handshake response exceeded {maxBytes} bytes.");
	}

	private static byte[] ParseSessionId(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new IOException("The audio server did not return a session ID.");
		}

		try
		{
			var bytes = Convert.FromHexString(value);
			if (bytes.Length != 16)
			{
				throw new FormatException("Session ID must be 16 bytes.");
			}

			return bytes;
		}
		catch (FormatException ex)
		{
			throw new IOException("The audio server returned an invalid session ID.", ex);
		}
	}

	private sealed class HandshakeResponse
	{
		[JsonPropertyName("status")]
		public string? Status { get; set; }

		[JsonPropertyName("message")]
		public string? Message { get; set; }

		[JsonPropertyName("session_id")]
		public string? SessionId { get; set; }

		[JsonPropertyName("udp_port")]
		public int UdpPort { get; set; }

		[JsonPropertyName("tcp_heartbeat_interval_ms")]
		public int TcpHeartbeatIntervalMs { get; set; }

		[JsonPropertyName("udp_session_timeout_ms")]
		public int UdpSessionTimeoutMs { get; set; }

		[JsonPropertyName("udp_audio_payload_max_bytes")]
		public int UdpAudioPayloadMaxBytes { get; set; }
	}
}

internal sealed record UdpAudioFrame(ulong Sequence, ulong TimestampMs, byte[] Payload);

internal readonly struct ParsedUdpPacket
{
	public ParsedUdpPacket(UdpPacketKind kind, byte[] sessionId, ulong sequence, ulong timestampMs, byte[] payload)
	{
		Kind = kind;
		SessionId = sessionId;
		Sequence = sequence;
		TimestampMs = timestampMs;
		Payload = payload;
	}

	public UdpPacketKind Kind { get; }
	public byte[] SessionId { get; }
	public ulong Sequence { get; }
	public ulong TimestampMs { get; }
	public byte[] Payload { get; }
}

internal static class UdpPacket
{
	private static readonly byte[] Magic = "RAS1"u8.ToArray();
	private const int HeaderLength = 22;
	public const int AudioHeaderLength = HeaderLength + 16;

	public static byte[] CreateControl(UdpPacketKind kind, ReadOnlySpan<byte> sessionId)
	{
		var packet = new byte[HeaderLength];
		WriteHeader(packet, kind, sessionId);
		return packet;
	}

	public static byte[] CreateAudio(ReadOnlySpan<byte> sessionId, ulong sequence, ulong timestampMs, ReadOnlySpan<byte> payload)
	{
		var packet = new byte[AudioHeaderLength + payload.Length];
		WriteAudio(packet, sessionId, sequence, timestampMs, payload);
		return packet;
	}

	public static int WriteAudio(Span<byte> packet, ReadOnlySpan<byte> sessionId, ulong sequence, ulong timestampMs, ReadOnlySpan<byte> payload)
	{
		if (packet.Length < AudioHeaderLength + payload.Length)
		{
			throw new ArgumentException("Audio packet destination is too small.", nameof(packet));
		}

		WriteHeader(packet, UdpPacketKind.AudioData, sessionId);
		BinaryPrimitives.WriteUInt64BigEndian(packet.Slice(HeaderLength, 8), sequence);
		BinaryPrimitives.WriteUInt64BigEndian(packet.Slice(HeaderLength + 8, 8), timestampMs);
		payload.CopyTo(packet[AudioHeaderLength..]);
		return AudioHeaderLength + payload.Length;
	}

	public static bool TryParse(byte[] packet, out ParsedUdpPacket parsed)
	{
		parsed = default;

		if (packet.Length < HeaderLength ||
			!packet.AsSpan(0, 4).SequenceEqual(Magic) ||
			packet[4] != 1)
		{
			return false;
		}

		var kind = (UdpPacketKind)packet[5];
		var sessionId = packet.AsSpan(6, 16).ToArray();
		if (kind != UdpPacketKind.AudioData)
		{
			parsed = new ParsedUdpPacket(kind, sessionId, 0, 0, []);
			return true;
		}

		if (packet.Length < AudioHeaderLength)
		{
			return false;
		}

		var sequence = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(HeaderLength, 8));
		var timestampMs = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(HeaderLength + 8, 8));
		var payload = packet.AsSpan(AudioHeaderLength).ToArray();
		parsed = new ParsedUdpPacket(kind, sessionId, sequence, timestampMs, payload);
		return true;
	}

	private static void WriteHeader(Span<byte> packet, UdpPacketKind kind, ReadOnlySpan<byte> sessionId)
	{
		if (sessionId.Length != 16)
		{
			throw new ArgumentException("Session ID must be 16 bytes.", nameof(sessionId));
		}

		Magic.CopyTo(packet);
		packet[4] = 1;
		packet[5] = (byte)kind;
		sessionId.CopyTo(packet[6..22]);
	}
}
