using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace NVDARemoteAudioHelper;

internal sealed class PlaybackSink : IDisposable
{
	private readonly LowLatencyFloatProvider _provider;
	private readonly IWavePlayer _output;
	private readonly MMDeviceEnumerator? _deviceEnumerator;
	private readonly MMDevice? _selectedDevice;
	private bool _playing;

	public PlaybackSink(
		int sampleRate,
		int channels,
		int prebufferMilliseconds,
		int outputLatencyMilliseconds,
		int bufferMilliseconds,
		string outputDeviceId,
		int receiveVolume)
	{
		var targetLatencyMs = Math.Clamp(prebufferMilliseconds, 5, 1000);
		var capacityMs = Math.Clamp(Math.Max(bufferMilliseconds, targetLatencyMs * 4), 40, 3000);
		_provider = new LowLatencyFloatProvider(sampleRate, channels, targetLatencyMs, capacityMs, receiveVolume);

		var desiredLatency = Math.Clamp(outputLatencyMilliseconds, 5, 1000);
		WasapiOut? wasapi = null;
		try
		{
			if (string.IsNullOrWhiteSpace(outputDeviceId))
			{
				wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: desiredLatency);
			}
			else
			{
				_deviceEnumerator = new MMDeviceEnumerator();
				_selectedDevice = _deviceEnumerator.GetDevice(outputDeviceId);
				wasapi = new WasapiOut(_selectedDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: desiredLatency);
			}
			wasapi.Init(_provider);
			_output = wasapi;
			JsonLog.Write("status", "WASAPI event-sync playback initialized.", new Dictionary<string, object?>
			{
				["output_latency_ms"] = desiredLatency,
				["output_device"] = _selectedDevice?.FriendlyName ?? "Windows default",
				["receive_volume"] = receiveVolume,
			});
		}
		catch (Exception ex)
		{
			wasapi?.Dispose();
			if (!string.IsNullOrWhiteSpace(outputDeviceId))
			{
				_selectedDevice?.Dispose();
				_deviceEnumerator?.Dispose();
				throw new InvalidOperationException("The selected playback device is unavailable or could not be opened. Choose another device in NVDA Remote Audio settings.", ex);
			}
			JsonLog.Write("status", "WASAPI playback initialization failed; falling back to WaveOutEvent.", new Dictionary<string, object?>
			{
				["type"] = ex.GetType().Name,
				["detail"] = ex.Message,
			});

			var waveOut = new WaveOutEvent
			{
				DesiredLatency = Math.Max(30, desiredLatency),
				NumberOfBuffers = 2,
			};
			waveOut.Init(_provider);
			_output = waveOut;
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

	public void AddSamples(ReadOnlySpan<float> samples)
	{
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
		_output.Stop();
		_output.Dispose();
		_selectedDevice?.Dispose();
		_deviceEnumerator?.Dispose();
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

		private readonly float _volume;

		public LowLatencyFloatProvider(int sampleRate, int channels, int targetLatencyMs, int capacityMs, int receiveVolume)
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
			_volume = Math.Clamp(receiveVolume, 0, 200) / 100f;
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
			if (_volume != 1f)
			{
				for (var index = 0; index < output.Length; index++)
				{
					output[index] = Math.Clamp(output[index] * _volume, -1f, 1f);
				}
			}

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
