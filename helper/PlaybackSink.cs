using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace NVDARemoteAudioHelper;

internal sealed class PlaybackSink : IDisposable
{
	/// <summary>
	/// NAudio opens the default endpoint for <see cref="Role.Console"/>, so that is
	/// the role whose changes matter to a sink following the Windows default.
	/// </summary>
	private const Role DefaultEndpointRole = Role.Console;

	private readonly LowLatencyFloatProvider _provider;
	private readonly MMDeviceEnumerator _deviceEnumerator;
	private readonly EndpointWatcher _endpointWatcher;
	private readonly string _outputDeviceId;
	private readonly int _desiredLatencyMs;
	private readonly object _outputLock = new();
	private IWavePlayer _output;
	private MMDevice? _selectedDevice;
	private bool _playing;
	private bool _disposed;
	private long _endpointRebuilds;
	/// <summary>
	/// Set from the endpoint-notification thread and from PlaybackStopped; acted on
	/// by <see cref="AddSamples"/>, which is the receive loop and the only thread
	/// allowed to tear a WASAPI client down.
	/// </summary>
	private int _outputStale;

	public PlaybackSink(
		int sampleRate,
		int channels,
		int prebufferMilliseconds,
		int outputLatencyMilliseconds,
		int bufferMilliseconds,
		string outputDeviceId,
		int receiveVolume,
		int receivePan,
		int bassDb,
		int midDb,
		int trebleDb)
	{
		var targetLatencyMs = Math.Clamp(prebufferMilliseconds, 5, 1000);
		var capacityMs = Math.Clamp(Math.Max(bufferMilliseconds, targetLatencyMs * 4), 40, 3000);
		_provider = new LowLatencyFloatProvider(
			sampleRate,
			channels,
			targetLatencyMs,
			capacityMs,
			receiveVolume,
			receivePan,
			bassDb,
			midDb,
			trebleDb);

		_outputDeviceId = (outputDeviceId ?? string.Empty).Trim();
		_desiredLatencyMs = Math.Clamp(outputLatencyMilliseconds, 5, 1000);
		_deviceEnumerator = new MMDeviceEnumerator();
		try
		{
			_output = CreateOutput(firstOpen: true);
		}
		catch
		{
			_deviceEnumerator.Dispose();
			throw;
		}

		// Headphones unplugged, a Bluetooth link dropping, or the user moving Windows
		// to another output all leave this sink rendering into an endpoint nobody is
		// listening to: silence with no error, which on a receiver whose whole job is
		// carrying audio is indistinguishable from the sender having stopped. Watch
		// for it and re-open on the endpoint that is actually current.
		_endpointWatcher = new EndpointWatcher(this);
		try
		{
			_deviceEnumerator.RegisterEndpointNotificationCallback(_endpointWatcher);
		}
		catch (Exception ex)
		{
			// Not fatal: playback still works, it just will not follow a device change.
			JsonLog.Write("status", "Could not watch for audio device changes.", new Dictionary<string, object?>
			{
				["type"] = ex.GetType().Name,
				["detail"] = ex.Message,
			});
		}
	}

	/// <summary>
	/// Opens the configured endpoint (the pinned device, or whatever Windows calls
	/// the default right now) against the existing provider, so re-opening keeps the
	/// ring buffer, the volume, the pan and the EQ exactly as they were.
	/// </summary>
	private IWavePlayer CreateOutput(bool firstOpen)
	{
		WasapiOut? wasapi = null;
		MMDevice? device = null;
		try
		{
			device = _outputDeviceId.Length == 0
				? _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, DefaultEndpointRole)
				: _deviceEnumerator.GetDevice(_outputDeviceId);
			wasapi = new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: _desiredLatencyMs);
			wasapi.Init(_provider);
			wasapi.PlaybackStopped += OnPlaybackStopped;
			_selectedDevice?.Dispose();
			_selectedDevice = device;
			JsonLog.Write("status", "WASAPI event-sync playback initialized.", new Dictionary<string, object?>
			{
				["output_latency_ms"] = _desiredLatencyMs,
				["output_device"] = device.FriendlyName,
				["following_windows_default"] = _outputDeviceId.Length == 0,
				["reopened"] = !firstOpen,
			});
			return wasapi;
		}
		catch (Exception ex)
		{
			wasapi?.Dispose();
			device?.Dispose();
			if (_outputDeviceId.Length != 0)
			{
				throw new InvalidOperationException("The selected playback device is unavailable or could not be opened. Choose another device in NVDA Remote Audio settings.", ex);
			}
			JsonLog.Write("status", "WASAPI playback initialization failed; falling back to WaveOutEvent.", new Dictionary<string, object?>
			{
				["type"] = ex.GetType().Name,
				["detail"] = ex.Message,
				["reopened"] = !firstOpen,
			});

			var waveOut = new WaveOutEvent
			{
				DesiredLatency = Math.Max(30, _desiredLatencyMs),
				NumberOfBuffers = 2,
			};
			waveOut.Init(_provider);
			waveOut.PlaybackStopped += OnPlaybackStopped;
			_selectedDevice?.Dispose();
			_selectedDevice = null;
			return waveOut;
		}
	}

	/// <summary>
	/// Flags the output for re-opening. Safe from any thread: the work itself
	/// happens on the receive loop, never on a WASAPI or MMDevice callback thread.
	/// </summary>
	private void InvalidateOutput() => Interlocked.Exchange(ref _outputStale, 1);

	private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
	{
		// A removed endpoint stops the player with an exception and raises no device
		// notification we would otherwise act on.
		if (e.Exception is not null && !_disposed)
		{
			InvalidateOutput();
		}
	}

	private void RebuildOutputIfStale()
	{
		if (Interlocked.Exchange(ref _outputStale, 0) == 0)
		{
			return;
		}

		lock (_outputLock)
		{
			if (_disposed)
			{
				return;
			}

			try
			{
				_output.PlaybackStopped -= OnPlaybackStopped;
				_output.Stop();
				_output.Dispose();
			}
			catch (Exception ex)
			{
				JsonLog.Write("status", "Closing the previous playback device failed.", new Dictionary<string, object?>
				{
					["type"] = ex.GetType().Name,
					["detail"] = ex.Message,
				});
			}

			try
			{
				_output = CreateOutput(firstOpen: false);
			}
			catch (Exception ex)
			{
				// The new endpoint is not ready yet -- a Bluetooth headset still
				// connecting, say. Leave the flag set so the next buffer retries,
				// rather than ending a session that is about to recover on its own.
				InvalidateOutput();
				_playing = false;
				JsonLog.Write("status", "Waiting for a usable playback device.", new Dictionary<string, object?>
				{
					["type"] = ex.GetType().Name,
					["detail"] = ex.Message,
				});
				return;
			}

			// Start from silence on the new endpoint: whatever is buffered is now as
			// stale as the switch took, and the drift estimate was measured against a
			// clock that no longer drives playback.
			_provider.Reset();
			_playing = false;
			_endpointRebuilds++;
			JsonLog.Write("status", "Playback moved to the current audio device.", new Dictionary<string, object?>
			{
				["endpoint_rebuilds"] = _endpointRebuilds,
			});
		}
	}

	/// <summary>
	/// Endpoint notifications arrive on an MMDevice thread. Nothing here does more
	/// than set a flag: re-opening a WASAPI client from inside the callback risks
	/// deadlocking against the audio service.
	/// </summary>
	private sealed class EndpointWatcher : IMMNotificationClient
	{
		private readonly PlaybackSink _sink;

		public EndpointWatcher(PlaybackSink sink) => _sink = sink;

		public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
		{
			// Only matters while following the default; a pinned device stays pinned.
			if (_sink._outputDeviceId.Length == 0 && flow == DataFlow.Render && role == DefaultEndpointRole)
			{
				_sink.InvalidateOutput();
			}
		}

		public void OnDeviceStateChanged(string deviceId, DeviceState newState)
		{
			// A pinned device that was unplugged and came back gets another try.
			if (_sink._outputDeviceId.Length != 0
				&& string.Equals(deviceId, _sink._outputDeviceId, StringComparison.OrdinalIgnoreCase))
			{
				_sink.InvalidateOutput();
			}
		}

		public void OnDeviceRemoved(string deviceId)
		{
			if (_sink._outputDeviceId.Length != 0
				&& string.Equals(deviceId, _sink._outputDeviceId, StringComparison.OrdinalIgnoreCase))
			{
				_sink.InvalidateOutput();
			}
		}

		public void OnDeviceAdded(string pwstrDeviceId)
		{
		}

		public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
		{
		}
	}

	public int CurrentBufferMs => _provider.CurrentBufferMs;
	public long Underruns => _provider.Underruns;
	public long Drops => _provider.Drops;
	public long TrimDrops => _provider.TrimDrops;
	public long DriftDrops => _provider.DriftDrops;
	public long DriftRepeats => _provider.DriftRepeats;
	public long PartialReads => _provider.PartialReads;
	public double DriftResamplerRatio => _provider.DriftResamplerRatio;
	public long DriftResamplerUpdates => _provider.DriftResamplerUpdates;

	/// <summary>Number of times playback followed the output device to a new endpoint.</summary>
	public long EndpointRebuilds => Interlocked.Read(ref _endpointRebuilds);

	/// <summary>
	/// True once enough audio has arrived to start the output device. A sink that
	/// re-opened but never started again looks healthy from every other counter
	/// and is silent, so this is the property worth checking after a switch.
	/// </summary>
	public bool IsPlaying => _playing;

	/// <summary>
	/// Pretends Windows announced a new default output device, for the self-test.
	/// A real device change cannot be staged from code, and the part worth proving
	/// is what happens afterwards: the old client is torn down, a new one is opened
	/// and started, and audio keeps flowing. Everything downstream of this call is
	/// the production path, byte for byte.
	/// </summary>
	internal void SimulateEndpointChangeForSelfTest() => InvalidateOutput();

	public void AddSamples(ReadOnlySpan<float> samples)
	{
		RebuildOutputIfStale();
		var armedNow = _provider.AddSamples(samples);
		if (!_playing && armedNow)
		{
			_output.Play();
			_playing = true;
			JsonLog.Write("status", "Playback started.", new Dictionary<string, object?>
			{
				["target_latency_ms"] = _provider.TargetLatencyMs,
			});
		}
	}

	public void Dispose()
	{
		lock (_outputLock)
		{
			_disposed = true;
			try
			{
				_deviceEnumerator.UnregisterEndpointNotificationCallback(_endpointWatcher);
			}
			catch
			{
				// Already gone, or never registered. Nothing left to unhook.
			}
			_output.PlaybackStopped -= OnPlaybackStopped;
			_output.Stop();
			_output.Dispose();
			_selectedDevice?.Dispose();
			_deviceEnumerator.Dispose();
		}
	}

	private sealed class LowLatencyFloatProvider : IWaveProvider
	{
		private const int ConcealFadeFrames = 48;
		private const int MaxConsecutiveConcealmentReads = 8;
		private const double DriftMeasurementWindowSec = 10.0;
		private const double DriftFirstWindowSec = 10.0;
		private const double DriftRatioSmoothingNew = 0.30;
		private const double DriftRatioMin = 0.95;
		private const double DriftRatioMax = 1.05;
		private const double DriftFilterTimeConstantSec = 2.0;
		private const double DepthCorrectionSec = 15.0;
		private const double MaxDepthBias = 0.003;

		[ThreadStatic]
		private static WindowsAudioThreadBoost? renderThreadBoost;

		private readonly int _sampleRate;
		private readonly int _channels;
		private readonly int _bytesPerFrame;
		private readonly int _bytesPerSecond;
		private readonly AudioRingBuffer _ring;
		private readonly WdlResampler _driftResampler;
		private volatile bool _armed;
		private volatile int _largestWriteMs;
		private bool _inConcealment;
		private int _consecutiveEmptyReads;
		private float _lastSampleL;
		private float _lastSampleR;
		private long _trimDrops;
		private long _partialReads;
		private long _bytesWrittenForDriftEst;
		private long _bytesReadOutputForDriftEst;
		private long _resamplerWindowStartTicks;
		private long _resamplerWindowStartBytesWritten;
		private long _resamplerWindowStartBytesOutput;
		private double _smoothedRateRatio = 1.0;
		private bool _resamplerActivelyTracking;
		private long _driftResamplerUpdates;
		private double _filteredErrorFrames;
		private long _prevDriftSampleTicks;
		private float[] _resamplerInputScratch = new float[2048];
		private float[] _resamplerOutputScratch = new float[2048];
		private int _lastInputFramesAvailable;

		private readonly AudioShaper _shaper;

		public LowLatencyFloatProvider(
			int sampleRate,
			int channels,
			int targetLatencyMs,
			int capacityMs,
			int receiveVolume,
			int receivePan,
			int bassDb,
			int midDb,
			int trebleDb)
		{
			_sampleRate = sampleRate;
			_channels = channels;
			TargetLatencyMs = targetLatencyMs;
			WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
			_bytesPerFrame = channels * sizeof(float);
			_bytesPerSecond = sampleRate * _bytesPerFrame;
			_ring = new AudioRingBuffer(MillisecondsToBytes(capacityMs));
			_driftResampler = new WdlResampler();
			_driftResampler.SetMode(interp: true, filtercnt: 0, sinc: false);
			_driftResampler.SetFeedMode(false);
			_driftResampler.SetRates(sampleRate, sampleRate);
			_shaper = new AudioShaper(sampleRate, channels, receiveVolume, receivePan, bassDb, midDb, trebleDb);
		}

		public WaveFormat WaveFormat { get; }
		public int TargetLatencyMs { get; }
		public int CurrentBufferMs => _ring.BufferedBytes / Math.Max(1, _bytesPerFrame) * 1000 / _sampleRate;
		public long Underruns => _ring.Underruns;
		public long Drops => _ring.Drops;
		public long TrimDrops => Interlocked.Read(ref _trimDrops);
		public long DriftDrops => 0;
		public long DriftRepeats => 0;
		public long PartialReads => Interlocked.Read(ref _partialReads);
		public double DriftResamplerRatio => _smoothedRateRatio;
		public long DriftResamplerUpdates => Interlocked.Read(ref _driftResamplerUpdates);

		/// <summary>
		/// Drops everything buffered and re-arms the prebuffer, for when playback
		/// moves to a different endpoint. Every drift measurement was taken against
		/// the old device's clock, so all of it has to go with it.
		/// </summary>
		public void Reset()
		{
			_armed = false;
			_ring.Clear();
			_inConcealment = false;
			_consecutiveEmptyReads = 0;
			_lastSampleL = 0f;
			_lastSampleR = 0f;
			_largestWriteMs = 0;
			Interlocked.Exchange(ref _bytesWrittenForDriftEst, 0);
			Interlocked.Exchange(ref _bytesReadOutputForDriftEst, 0);
			_resamplerWindowStartTicks = 0;
			_resamplerWindowStartBytesWritten = 0;
			_resamplerWindowStartBytesOutput = 0;
			_smoothedRateRatio = 1.0;
			_resamplerActivelyTracking = false;
			_filteredErrorFrames = 0.0;
			_prevDriftSampleTicks = 0;
			_lastInputFramesAvailable = 0;
			_driftResampler.SetRates(_sampleRate, _sampleRate);
			_driftResampler.Reset(0.0);
		}

		public bool AddSamples(ReadOnlySpan<float> samples)
		{
			if (samples.IsEmpty)
			{
				return _armed;
			}

			var bytes = MemoryMarshal.AsBytes(samples);
			_ring.Write(bytes);
			Interlocked.Add(ref _bytesWrittenForDriftEst, bytes.Length);
			var writeMs = bytes.Length * 1000 / _bytesPerSecond;
			if (writeMs > _largestWriteMs)
			{
				_largestWriteMs = writeMs;
			}

			if (!_armed && _ring.BufferedBytes >= MillisecondsToBytes(TargetLatencyMs))
			{
				_armed = true;
			}

			return _armed;
		}

		public int Read(byte[] buffer, int offset, int count)
		{
			renderThreadBoost ??= new WindowsAudioThreadBoost("Pro Audio");

			var destinationBytes = buffer.AsSpan(offset, count);
			if (count % sizeof(float) != 0)
			{
				destinationBytes.Clear();
				return count;
			}

			var output = MemoryMarshal.Cast<byte, float>(destinationBytes);
			if (!_armed)
			{
				output.Clear();
				return count;
			}

			var outFrames = output.Length / _channels;
			if (outFrames <= 0)
			{
				return count;
			}

			TrimBacklogIfNeeded();
			UpdateDriftResamplerRateIfDue(outFrames);
			ReadThroughResampler(output, outFrames);
			_shaper.Process(output);

			return count;
		}

		private void TrimBacklogIfNeeded()
		{
			var largestWriteMs = Math.Max(1, _largestWriteMs);
			var trimMarginMs = Math.Max(largestWriteMs * 4 + 4, 15) + 8;
			var trimThresholdBytes = MillisecondsToBytes(TargetLatencyMs + trimMarginMs);
			var buffered = _ring.BufferedBytes;
			if (buffered <= trimThresholdBytes)
			{
				return;
			}

			var keepBytes = MillisecondsToBytes(TargetLatencyMs + largestWriteMs * 2 + 5);
			var dropBytes = AlignToFrame(buffered - keepBytes);
			if (dropBytes <= 0)
			{
				return;
			}

			_ring.DropOldest(dropBytes);
			Interlocked.Add(ref _trimDrops, dropBytes);
		}

		private void UpdateDriftResamplerRateIfDue(int outFrames)
		{
			var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
			var driftTargetBytes = MillisecondsToBytes(TargetLatencyMs);
			if (_prevDriftSampleTicks != 0)
			{
				var dtSec = (nowTicks - _prevDriftSampleTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
				var errorFrames = ((double)_ring.BufferedBytes - driftTargetBytes) / _bytesPerFrame;
				var alpha = dtSec / (DriftFilterTimeConstantSec + dtSec);
				_filteredErrorFrames = (1.0 - alpha) * _filteredErrorFrames + alpha * errorFrames;
			}

			_prevDriftSampleTicks = nowTicks;
			if (_resamplerWindowStartTicks == 0)
			{
				_resamplerWindowStartTicks = nowTicks;
				_resamplerWindowStartBytesWritten = Interlocked.Read(ref _bytesWrittenForDriftEst);
				_resamplerWindowStartBytesOutput = Interlocked.Read(ref _bytesReadOutputForDriftEst);
				return;
			}

			var windowDuration = _resamplerActivelyTracking ? DriftMeasurementWindowSec : DriftFirstWindowSec;
			var elapsedSec = (nowTicks - _resamplerWindowStartTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
			if (elapsedSec < windowDuration)
			{
				return;
			}

			var bytesWrittenNow = Interlocked.Read(ref _bytesWrittenForDriftEst);
			var bytesOutputNow = Interlocked.Read(ref _bytesReadOutputForDriftEst);
			var bytesWrittenInWindow = bytesWrittenNow - _resamplerWindowStartBytesWritten;
			var bytesOutputInWindow = bytesOutputNow - _resamplerWindowStartBytesOutput;

			if (bytesOutputInWindow > 0 && bytesWrittenInWindow > 0)
			{
				var measuredRatio = (double)bytesWrittenInWindow / bytesOutputInWindow;
				if (measuredRatio >= DriftRatioMin && measuredRatio <= DriftRatioMax)
				{
					_smoothedRateRatio = _resamplerActivelyTracking
						? (1.0 - DriftRatioSmoothingNew) * _smoothedRateRatio + DriftRatioSmoothingNew * measuredRatio
						: measuredRatio;
					_resamplerActivelyTracking = true;
				}
			}

			if (_resamplerActivelyTracking)
			{
				var depthFrames = _ring.BufferedBytes / _bytesPerFrame;
				var targetFrames = TargetLatencyMs * _sampleRate / 1000;
				var depthError = depthFrames - targetFrames;
				var depthCorrection = Math.Clamp(depthError / (DepthCorrectionSec * _sampleRate), -MaxDepthBias, MaxDepthBias);
				_driftResampler.SetRates(_sampleRate * (_smoothedRateRatio + depthCorrection), _sampleRate);
				Interlocked.Increment(ref _driftResamplerUpdates);
			}

			_resamplerWindowStartTicks = nowTicks;
			_resamplerWindowStartBytesWritten = bytesWrittenNow;
			_resamplerWindowStartBytesOutput = bytesOutputNow;
		}

		private void ReadThroughResampler(Span<float> output, int outFrames)
		{
			var outFloats = outFrames * _channels;
			var inputFramesNeeded = _driftResampler.ResamplePrepare(outFrames, _channels, out var inputBuffer, out var inputOffset);
			_lastInputFramesAvailable = inputFramesNeeded;
			if (inputFramesNeeded <= 0)
			{
				ResampleOutAndCopy(output, outFrames);
				Interlocked.Add(ref _bytesReadOutputForDriftEst, outFloats * sizeof(float));
				return;
			}

			var inputFloatsNeeded = inputFramesNeeded * _channels;
			if (_resamplerInputScratch.Length < inputFloatsNeeded)
			{
				_resamplerInputScratch = new float[inputFloatsNeeded];
			}

			var bytesGot = _ring.Read(MemoryMarshal.AsBytes(_resamplerInputScratch.AsSpan(0, inputFloatsNeeded)));
			var floatsGot = bytesGot / sizeof(float);
			var framesGot = floatsGot / _channels;

			_resamplerInputScratch.AsSpan(0, inputFloatsNeeded).CopyTo(inputBuffer.AsSpan(inputOffset, inputFloatsNeeded));
			ResampleOutAndCopy(output, outFrames);
			Interlocked.Add(ref _bytesReadOutputForDriftEst, outFloats * sizeof(float));

			if (framesGot == 0)
			{
				_consecutiveEmptyReads++;
				if (_consecutiveEmptyReads <= MaxConsecutiveConcealmentReads)
				{
					ApplyFadeOut(output, 0, outFrames);
				}

				_inConcealment = true;
			}
			else
			{
				if (framesGot < inputFramesNeeded)
				{
					Interlocked.Increment(ref _partialReads);
				}

				_consecutiveEmptyReads = 0;
				if (_inConcealment)
				{
					ApplyFadeIn(output, outFrames);
					_inConcealment = false;
				}

				var lastIdx = (floatsGot - _channels);
				_lastSampleL = _resamplerInputScratch[lastIdx];
				_lastSampleR = _channels > 1 ? _resamplerInputScratch[lastIdx + 1] : _resamplerInputScratch[lastIdx];
			}
		}

		private void ResampleOutAndCopy(Span<float> output, int outFrames)
		{
			var outFloats = outFrames * _channels;
			if (_resamplerOutputScratch.Length < outFloats)
			{
				_resamplerOutputScratch = new float[outFloats];
			}

			var producedFrames = _driftResampler.ResampleOut(_resamplerOutputScratch, 0, _lastInputFramesAvailable, outFrames, _channels);
			var producedFloats = producedFrames * _channels;
			for (var i = 0; i < producedFloats; i++)
			{
				var sample = _resamplerOutputScratch[i];
				output[i] = float.IsNaN(sample) ? 0f : Math.Clamp(sample, -1f, 1f);
			}

			if (producedFloats < outFloats)
			{
				output[producedFloats..outFloats].Clear();
			}
		}

		private void ApplyFadeOut(Span<float> output, int startFrame, int frameCount)
		{
			var fadeFrames = Math.Min(ConcealFadeFrames, frameCount);
			for (var frame = 0; frame < fadeFrames; frame++)
			{
				var t = (frame + 1) / (double)fadeFrames;
				var gain = (float)((Math.Cos(Math.PI * t) + 1.0) * 0.5);
				var idx = (startFrame + frame) * _channels;
				output[idx] = _lastSampleL * gain;
				if (_channels > 1)
				{
					output[idx + 1] = _lastSampleR * gain;
				}
			}
		}

		private void ApplyFadeIn(Span<float> output, int requestedFrames)
		{
			var fadeFrames = Math.Min(ConcealFadeFrames, requestedFrames);
			for (var frame = 0; frame < fadeFrames; frame++)
			{
				var t = frame / (double)Math.Max(1, fadeFrames);
				var gain = (float)((1.0 - Math.Cos(Math.PI * t)) * 0.5);
				var idx = frame * _channels;
				output[idx] *= gain;
				if (_channels > 1)
				{
					output[idx + 1] *= gain;
				}
			}
		}

		private int MillisecondsToBytes(int milliseconds) =>
			AlignToFrame(Math.Max(_bytesPerFrame, milliseconds * _bytesPerSecond / 1000));

		private int AlignToFrame(int bytes) => bytes / _bytesPerFrame * _bytesPerFrame;
	}
}

internal sealed class AudioShaper
{
	private readonly int _channels;
	private readonly float _volume;
	private readonly float _leftPanGain;
	private readonly float _rightPanGain;
	private readonly BiQuadFilter[] _bassFilters;
	private readonly BiQuadFilter[] _midFilters;
	private readonly BiQuadFilter[] _trebleFilters;

	public AudioShaper(int sampleRate, int channels, int receiveVolume, int receivePan, int bassDb, int midDb, int trebleDb)
	{
		_channels = channels;
		_volume = Math.Clamp(receiveVolume, 0, 200) / 100f;
		var pan = Math.Clamp(receivePan, -100, 100) / 100f;
		_leftPanGain = pan > 0 ? 1f - pan : 1f;
		_rightPanGain = pan < 0 ? 1f + pan : 1f;
		_bassFilters = Enumerable.Range(0, channels)
			.Select(_ => BiQuadFilter.LowShelf(sampleRate, 180f, 0.8f, Math.Clamp(bassDb, -12, 12)))
			.ToArray();
		_midFilters = Enumerable.Range(0, channels)
			.Select(_ => BiQuadFilter.PeakingEQ(sampleRate, 1200f, 0.9f, Math.Clamp(midDb, -12, 12)))
			.ToArray();
		_trebleFilters = Enumerable.Range(0, channels)
			.Select(_ => BiQuadFilter.HighShelf(sampleRate, 6000f, 0.8f, Math.Clamp(trebleDb, -12, 12)))
			.ToArray();
	}

	public void Process(Span<float> samples)
	{
		for (var index = 0; index < samples.Length; index++)
		{
			var channel = index % _channels;
			var sample = _bassFilters[channel].Transform(samples[index]);
			sample = _midFilters[channel].Transform(sample);
			sample = _trebleFilters[channel].Transform(sample);
			var panGain = channel == 0 ? _leftPanGain : channel == 1 ? _rightPanGain : 1f;
			samples[index] = Math.Clamp(sample * _volume * panGain, -1f, 1f);
		}
	}
}
