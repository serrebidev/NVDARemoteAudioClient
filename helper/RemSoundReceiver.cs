using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Concentus;

namespace NVDARemoteAudioHelper;

/// <summary>
/// Plays audio from RemSound peers: the Windows app, its service, or an iPhone or Mac
/// sending its microphone. Streams are keyed by source and stream ID, as RemSound
/// keys them. One stream plays at a time; another device takes over once the playing
/// one has been silent for a second, and a device that restarts its stream (a codec
/// change rotates the stream ID) takes over at once.
///
/// Every Format packet carries the sender's password fingerprint, so a password that
/// differs, or a sender too old to encrypt, is reported by name instead of being
/// silence nobody can explain.
/// </summary>
internal sealed class RemSoundReceiver : IDisposable
{
	private const int SampleRate = 48000;
	private const int Channels = 2;
	private const int MaxDecodedSamplesPerChannel = 5760;
	private static readonly TimeSpan HandoverSilence = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan SessionExpiry = TimeSpan.FromSeconds(30);

	private readonly RemSoundLink _link;
	private readonly PlaybackSink _playback;
	private readonly ReceivedAudioRecorder _recorder;
	private readonly AesGcm _gcm;
	private readonly Dictionary<(IPEndPoint Source, ushort StreamId), Session> _sessions = [];
	private readonly HashSet<IPAddress> _reportedMismatch = [];
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private readonly byte[] _plain = new byte[65536];
	private readonly float[] _decoded = new float[MaxDecodedSamplesPerChannel * Channels];
	private readonly float[] _stereo = new float[MaxDecodedSamplesPerChannel * Channels * 4];
	private (IPEndPoint Source, ushort StreamId)? _active;
	private IPAddress? _announcedSource;
	private long _packetsPlayed;
	private long _authenticationFailures;
	private long _fecRecoveries;
	private long _plcFrames;
	private long _unrecoveredGaps;
	private long _ignoredPackets;
	private TimeSpan _nextDiagnostic = TimeSpan.FromSeconds(5);
	private int _consecutiveAuthFailures;

	public RemSoundReceiver(RemSoundLink link, PlaybackSink playback, ReceivedAudioRecorder recorder)
	{
		_link = link;
		_playback = playback;
		_recorder = recorder;
		_gcm = new AesGcm(link.Key, RemCrypto.TagBytes);
	}

	public long PacketsPlayed => Interlocked.Read(ref _packetsPlayed);

	/// <summary>Called on the link's receive thread for every Format and Audio packet from an allowed source.</summary>
	public void OnPacket(RemPacketType type, ushort streamId, uint sequence, ReadOnlySpan<byte> payload, IPEndPoint source)
	{
		var key = (source, streamId);
		var now = _clock.Elapsed;
		if (type == RemPacketType.Format)
		{
			OnFormat(key, payload, now);
		}
		else if (_sessions.TryGetValue(key, out var session) && session.Format is not null)
		{
			session.LastPacket = now;
			if (ClaimPlayback(key, session, now))
			{
				OnAudio(session, sequence, payload);
			}
			else
			{
				_ignoredPackets++;
			}
		}
		else
		{
			// Audio before its Format arrives is normal for the first quarter second.
			_ignoredPackets++;
		}
		if (now >= _nextDiagnostic)
		{
			_nextDiagnostic = now + TimeSpan.FromSeconds(5);
			WriteDiagnostics(now);
		}
	}

	private void OnFormat((IPEndPoint Source, ushort StreamId) key, ReadOnlySpan<byte> payload, TimeSpan now)
	{
		if (!RemPacket.TryReadFormat(payload, out var format, out var fingerprint))
		{
			return;
		}
		if (!format.IsUsable(out var reason))
		{
			JsonLog.Write("diagnostic", $"Ignored a RemSound stream from {key.Source}: {reason}.");
			return;
		}
		ReportFingerprint(key.Source.Address, fingerprint);
		if (!_sessions.TryGetValue(key, out var session))
		{
			session = new Session();
			_sessions[key] = session;
			PruneSessions(now);
		}
		session.LastPacket = now;
		if (session.Format is null ||
			session.Format.Codec != format.Codec ||
			session.Format.Channels != format.Channels ||
			session.Format.SampleRate != format.SampleRate)
		{
			session.Reset(format);
		}
		session.Format = format;
	}

	private void ReportFingerprint(IPAddress source, byte[]? fingerprint)
	{
		if (fingerprint is not null && RemCrypto.FingerprintsEqual(fingerprint, _link.Fingerprint))
		{
			_reportedMismatch.Remove(source);
			return;
		}
		if (!_reportedMismatch.Add(source))
		{
			return;
		}
		var name = _link.DescribePeer(source);
		JsonLog.Write("warning", fingerprint is null
			? $"{name} is running a RemSound version too old to encrypt audio. Update RemSound on {name}."
			: $"{name} uses a different password. Set the same encryption password on both devices.", new Dictionary<string, object?>
			{
				["peer"] = name,
				["reason"] = fingerprint is null ? "peer_needs_update" : "password_mismatch",
			});
	}

	private bool ClaimPlayback((IPEndPoint Source, ushort StreamId) key, Session session, TimeSpan now)
	{
		if (_active == key)
		{
			return true;
		}
		if (_active is { } current && _sessions.TryGetValue(current, out var playing) &&
			now - playing.LastPacket < HandoverSilence &&
			!current.Source.Address.Equals(key.Source.Address))
		{
			return false;
		}
		_active = key;
		session.ResetDecoderState();
		return true;
	}

	/// <summary>Says who is playing, once per device, after its audio has actually decrypted.</summary>
	private void AnnounceSource(IPEndPoint source)
	{
		if (source.Address.Equals(_announcedSource))
		{
			return;
		}
		_announcedSource = source.Address;
		var name = _link.DescribePeer(source.Address);
		var format = _active is { } active && _sessions.TryGetValue(active, out var session) ? session.Format : null;
		JsonLog.Write("receiving", $"Receiving audio from {name}.", new Dictionary<string, object?>
		{
			["peer"] = name,
			["codec"] = format?.Codec == (int)RemCodec.Opus ? "opus" : "pcm",
			["frame_samples"] = format?.FrameSamplesPerChannel,
		});
	}

	private void OnAudio(Session session, uint sequence, ReadOnlySpan<byte> payload)
	{
		var format = session.Format!;
		if (format.Codec == (int)RemCodec.Opus)
		{
			if (!RemCrypto.TryDecryptInto(_gcm, payload, _plain, out var length))
			{
				NoteAuthenticationFailure(session);
				return;
			}
			NoteAuthenticated();
			PlayOpus(session, sequence, _plain.AsSpan(0, length));
			return;
		}

		if (!RemPcmFrame.TryReadSubHeader(payload, out var frameId, out var part, out var parts) ||
			!session.Assembler.TryAdd(payload[RemPcmFrame.SubHeaderSize..], frameId, part, parts, out var frame))
		{
			return;
		}
		if (!RemCrypto.TryDecryptInto(_gcm, frame, _plain, out var plainLength))
		{
			NoteAuthenticationFailure(session);
			return;
		}
		NoteAuthenticated();
		if (format.SampleRate != SampleRate)
		{
			if (!session.WarnedRate)
			{
				session.WarnedRate = true;
				JsonLog.Write("diagnostic", $"Ignored PCM at {format.SampleRate} Hz; only 48 kHz is played.");
			}
			return;
		}
		var samples = Math.Min(plainLength / 3, _decoded.Length);
		RemPcmFrame.Int24ToFloat(_plain.AsSpan(0, samples * 3), _decoded);
		Play(_decoded, samples, format.Channels);
	}

	private void PlayOpus(Session session, uint sequence, ReadOnlySpan<byte> packet)
	{
		var decoder = session.Decoder!;
		var channels = session.Format!.Channels;
		var frameSamples = Math.Max(120, session.Format.FrameSamplesPerChannel);
		if (session.LastSequence is { } last && sequence != last + 1)
		{
			var gap = sequence - last - 1;
			if (gap > 0 && gap < 1000)
			{
				// One lost packet: its content rides in this one's FEC data.
				if (gap == 1 && TryDecode(decoder, packet, frameSamples, fec: true, channels))
				{
					_fecRecoveries++;
				}
				else
				{
					_unrecoveredGaps += gap;
					for (var i = 0u; i < Math.Min(gap, 5u); i++)
					{
						if (TryDecode(decoder, ReadOnlySpan<byte>.Empty, frameSamples, fec: false, channels))
						{
							_plcFrames++;
						}
					}
				}
			}
			else if (sequence <= last && last - sequence < 1000)
			{
				// Late or duplicated; the audio it carried has already been concealed.
				return;
			}
		}
		session.LastSequence = sequence;
		TryDecode(decoder, packet, MaxDecodedSamplesPerChannel, fec: false, channels);
	}

	private bool TryDecode(IOpusDecoder decoder, ReadOnlySpan<byte> packet, int frameSamples, bool fec, int channels)
	{
		try
		{
			var decoded = decoder.Decode(packet, _decoded.AsSpan(), frameSamples, fec);
			if (decoded <= 0)
			{
				return false;
			}
			Play(_decoded, decoded * channels, channels);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private void Play(float[] samples, int count, int channels)
	{
		if (channels == 1)
		{
			// A phone's microphone may arrive mono; it plays in both ears.
			count = Math.Min(count, _stereo.Length / 2);
			for (var i = 0; i < count; i++)
			{
				_stereo[i * 2] = samples[i];
				_stereo[(i * 2) + 1] = samples[i];
			}
			samples = _stereo;
			count *= 2;
		}
		if (count <= 0)
		{
			return;
		}
		_recorder.Write(samples, count);
		_playback.AddSamples(samples.AsSpan(0, count));
		Interlocked.Increment(ref _packetsPlayed);
		if (_active is { } active)
		{
			_link.NoteAudioFrom(active.Source.Address);
			AnnounceSource(active.Source);
		}
	}

	private void NoteAuthenticationFailure(Session session)
	{
		_authenticationFailures++;
		_consecutiveAuthFailures++;
		// The fingerprint normally names this first; this catches a sender whose
		// fingerprint matches but whose audio still fails, which means damage in transit
		// or a peer encrypting with some other key.
		if (_consecutiveAuthFailures == 50 && !session.ReportedAuthFailure &&
			!(_active is { } active && _reportedMismatch.Contains(active.Source.Address)))
		{
			session.ReportedAuthFailure = true;
			JsonLog.Write("warning", "Audio from the other device could not be decrypted. Check that both devices use the same encryption password.");
		}
	}

	private void NoteAuthenticated() => _consecutiveAuthFailures = 0;

	private void PruneSessions(TimeSpan now)
	{
		foreach (var stale in _sessions.Where(pair => now - pair.Value.LastPacket > SessionExpiry).Select(pair => pair.Key).ToList())
		{
			_sessions[stale].Dispose();
			_sessions.Remove(stale);
			if (_active == stale)
			{
				_active = null;
			}
		}
	}

	private void WriteDiagnostics(TimeSpan now)
	{
		PruneSessions(now);
		JsonLog.Write("diagnostic", "RemSound receiver statistics.", new Dictionary<string, object?>
		{
			["packets_played"] = PacketsPlayed,
			["streams"] = _sessions.Count,
			["playing_from"] = _active?.Source.ToString(),
			["authentication_failures"] = _authenticationFailures,
			["fec_recoveries"] = _fecRecoveries,
			["plc_frames"] = _plcFrames,
			["unrecovered_gaps"] = _unrecoveredGaps,
			["ignored_packets"] = _ignoredPackets,
			["buffer_ms"] = _playback.CurrentBufferMs,
			["underruns"] = _playback.Underruns,
			["drops"] = _playback.Drops,
			["endpoint_rebuilds"] = _playback.EndpointRebuilds,
			["peers"] = _link.HealthSnapshot(),
		});
	}

	public void Dispose()
	{
		foreach (var session in _sessions.Values)
		{
			session.Dispose();
		}
		_sessions.Clear();
		_gcm.Dispose();
	}

	private sealed class Session : IDisposable
	{
		public RemFormat? Format { get; set; }
		public IOpusDecoder? Decoder { get; private set; }
		public RemPcmAssembler Assembler { get; private set; } = new();
		public uint? LastSequence { get; set; }
		public TimeSpan LastPacket { get; set; }
		public bool WarnedRate { get; set; }
		public bool ReportedAuthFailure { get; set; }

		public void Reset(RemFormat format)
		{
			(Decoder as IDisposable)?.Dispose();
			// Opus decodes any stream at 48 kHz, whatever rate it was encoded at.
			Decoder = format.Codec == (int)RemCodec.Opus
				? OpusCodecFactory.CreateDecoder(SampleRate, format.Channels, TextWriter.Null)
				: null;
			Assembler = new RemPcmAssembler();
			LastSequence = null;
		}

		public void ResetDecoderState()
		{
			LastSequence = null;
		}

		public void Dispose() => (Decoder as IDisposable)?.Dispose();
	}
}
