using System.Runtime.InteropServices;
using NAudio.Wave;

namespace NVDARemoteAudioHelper;

internal sealed class PlaybackSink : IDisposable
{
	private readonly LowLatencyFloatProvider _provider;
	private readonly IWavePlayer _output;
	private bool _playing;

	public PlaybackSink(int sampleRate, int channels, int prebufferMilliseconds, int outputLatencyMilliseconds, int bufferMilliseconds)
	{
		var targetLatencyMs = Math.Clamp(prebufferMilliseconds, 5, 1000);
		var capacityMs = Math.Clamp(Math.Max(bufferMilliseconds, targetLatencyMs * 4), 40, 3000);
		_provider = new LowLatencyFloatProvider(sampleRate, channels, targetLatencyMs, capacityMs);

		var desiredLatency = Math.Clamp(outputLatencyMilliseconds, 5, 1000);
		try
		{
			var wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: desiredLatency);
			wasapi.Init(_provider);
			_output = wasapi;
			JsonLog.Write("status", "WASAPI event-sync playback initialized.", new Dictionary<string, object?>
			{
				["output_latency_ms"] = desiredLatency,
			});
		}
		catch (Exception ex)
		{
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
	}

	private sealed class LowLatencyFloatProvider : IWaveProvider
	{
		private const double DriftGain = 0.005;
		private const double DriftSmallErrorFrames = 50;
		private const double DriftMaxGainScale = 20.0;
		private const int DriftCrossfadeFrames = 8;
		private const int ConcealFadeFrames = 48;
		private const int MaxConsecutiveConcealmentReads = 8;

		[ThreadStatic]
		private static WindowsAudioThreadBoost? renderThreadBoost;

		private readonly int _sampleRate;
		private readonly int _channels;
		private readonly int _bytesPerFrame;
		private readonly int _bytesPerSecond;
		private readonly AudioRingBuffer _ring;
		private float[] _scratch = new float[8192];
		private volatile bool _armed;
		private volatile int _largestWriteMs;
		private double _driftAccumulatorFrames;
		private long _previousDriftTicks;
		private int _pendingDropFrames;
		private int _pendingRepeatFrames;
		private bool _inConcealment;
		private int _consecutiveEmptyReads;
		private float _lastSampleL;
		private float _lastSampleR;
		private long _trimDrops;
		private long _driftDrops;
		private long _driftRepeats;

		public LowLatencyFloatProvider(int sampleRate, int channels, int targetLatencyMs, int capacityMs)
		{
			_sampleRate = sampleRate;
			_channels = channels;
			TargetLatencyMs = targetLatencyMs;
			WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
			_bytesPerFrame = channels * sizeof(float);
			_bytesPerSecond = sampleRate * _bytesPerFrame;
			_ring = new AudioRingBuffer(MillisecondsToBytes(capacityMs));
		}

		public WaveFormat WaveFormat { get; }
		public int TargetLatencyMs { get; }
		public int CurrentBufferMs => _ring.BufferedBytes / Math.Max(1, _bytesPerFrame) * 1000 / _sampleRate;
		public long Underruns => _ring.Underruns;
		public long Drops => _ring.Drops;
		public long TrimDrops => Interlocked.Read(ref _trimDrops);
		public long DriftDrops => Interlocked.Read(ref _driftDrops);
		public long DriftRepeats => Interlocked.Read(ref _driftRepeats);

		public bool AddSamples(ReadOnlySpan<float> samples)
		{
			if (samples.IsEmpty)
			{
				return _armed;
			}

			var bytes = MemoryMarshal.AsBytes(samples);
			_ring.Write(bytes);
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
			UpdateDrift(outFrames);

			var dropThisCall = _pendingDropFrames > 0 && outFrames > DriftCrossfadeFrames * 2 ? 1 : 0;
			var repeatThisCall = _pendingRepeatFrames > 0 && outFrames > DriftCrossfadeFrames * 2 ? 1 : 0;
			if (dropThisCall > 0 && repeatThisCall > 0)
			{
				dropThisCall = 0;
				repeatThisCall = 0;
			}

			if (dropThisCall > 0)
			{
				var extraFloats = (outFrames + 1) * _channels;
				var scratch = Scratch(extraFloats);
				ReadInputWithConcealment(scratch);
				ApplyDropCrossfade(scratch, output, outFrames);
				_pendingDropFrames--;
				Interlocked.Increment(ref _driftDrops);
			}
			else if (repeatThisCall > 0)
			{
				var shortFloats = (outFrames - 1) * _channels;
				var scratch = Scratch(shortFloats);
				ReadInputWithConcealment(scratch);
				ApplyRepeatCrossfade(scratch, output, outFrames);
				_pendingRepeatFrames--;
				Interlocked.Increment(ref _driftRepeats);
			}
			else
			{
				ReadInputWithConcealment(output);
			}

			return count;
		}

		private Span<float> Scratch(int length)
		{
			if (_scratch.Length < length)
			{
				_scratch = new float[length];
			}

			return _scratch.AsSpan(0, length);
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

		private void UpdateDrift(int outFrames)
		{
			var now = System.Diagnostics.Stopwatch.GetTimestamp();
			if (_previousDriftTicks != 0)
			{
				var dtSec = (now - _previousDriftTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
				var errorFrames = ((double)_ring.BufferedBytes - MillisecondsToBytes(TargetLatencyMs)) / _bytesPerFrame;
				var absErrorFrames = Math.Abs(errorFrames);
				var gainScale = absErrorFrames <= DriftSmallErrorFrames
					? 1.0
					: Math.Min(absErrorFrames / DriftSmallErrorFrames, DriftMaxGainScale);
				_driftAccumulatorFrames += errorFrames * dtSec * DriftGain * gainScale;
				_driftAccumulatorFrames = Math.Clamp(_driftAccumulatorFrames, -100.0, 100.0);
			}

			_previousDriftTicks = now;
			if (_driftAccumulatorFrames >= 1.0)
			{
				_pendingDropFrames++;
				_driftAccumulatorFrames -= 1.0;
			}
			else if (_driftAccumulatorFrames <= -1.0 && outFrames > 1)
			{
				_pendingRepeatFrames++;
				_driftAccumulatorFrames += 1.0;
			}
		}

		private void ReadInputWithConcealment(Span<float> output)
		{
			var requestedFrames = output.Length / _channels;
			var floatsRead = _ring.ReadFloats(output);
			var framesRead = floatsRead / _channels;

			if (framesRead < requestedFrames)
			{
				_consecutiveEmptyReads = framesRead == 0 ? _consecutiveEmptyReads + 1 : 0;
				if (_consecutiveEmptyReads <= MaxConsecutiveConcealmentReads)
				{
					ApplyFadeOut(output, framesRead, requestedFrames - framesRead);
				}

				_inConcealment = true;
			}
			else if (_inConcealment)
			{
				ApplyFadeIn(output, requestedFrames);
				_inConcealment = false;
				_consecutiveEmptyReads = 0;
			}
			else
			{
				_consecutiveEmptyReads = 0;
			}

			if (framesRead > 0)
			{
				var lastIdx = (framesRead - 1) * _channels;
				_lastSampleL = output[lastIdx];
				_lastSampleR = _channels > 1 ? output[lastIdx + 1] : output[lastIdx];
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

		private void ApplyDropCrossfade(ReadOnlySpan<float> input, Span<float> output, int outFrames)
		{
			var spliceIdx = outFrames / 2;
			var halfWindow = DriftCrossfadeFrames / 2;
			var preEnd = spliceIdx - halfWindow;
			if (preEnd > 0)
			{
				input[..(preEnd * _channels)].CopyTo(output);
			}

			for (var frame = 0; frame < DriftCrossfadeFrames; frame++)
			{
				var t = (frame + 1) / (double)(DriftCrossfadeFrames + 1);
				var fadeIn = (float)((1.0 - Math.Cos(Math.PI * t)) * 0.5);
				var fadeOut = 1f - fadeIn;
				var beforeIdx = (preEnd + frame) * _channels;
				var afterIdx = (preEnd + 1 + frame) * _channels;
				var dstIdx = (preEnd + frame) * _channels;
				for (var ch = 0; ch < _channels; ch++)
				{
					output[dstIdx + ch] = input[beforeIdx + ch] * fadeOut + input[afterIdx + ch] * fadeIn;
				}
			}

			var postStartInput = spliceIdx + halfWindow + 1;
			var postStartOutput = spliceIdx + halfWindow;
			var postFrames = outFrames - postStartOutput;
			if (postFrames > 0)
			{
				input.Slice(postStartInput * _channels, postFrames * _channels)
					.CopyTo(output[(postStartOutput * _channels)..]);
			}
		}

		private void ApplyRepeatCrossfade(ReadOnlySpan<float> input, Span<float> output, int outFrames)
		{
			var spliceIdx = outFrames / 2;
			var halfWindow = DriftCrossfadeFrames / 2;
			var preEnd = spliceIdx - halfWindow;
			if (preEnd > 0)
			{
				input[..(preEnd * _channels)].CopyTo(output);
			}

			for (var frame = 0; frame <= DriftCrossfadeFrames; frame++)
			{
				var t = frame / (double)(DriftCrossfadeFrames + 1);
				var fadeIn = (float)((1.0 - Math.Cos(Math.PI * t)) * 0.5);
				var fadeOut = 1f - fadeIn;
				var leftFrame = Math.Max(0, preEnd + frame - 1);
				var rightFrame = Math.Min(input.Length / _channels - 1, preEnd + frame);
				var dstIdx = (preEnd + frame) * _channels;
				var leftIdx = leftFrame * _channels;
				var rightIdx = rightFrame * _channels;
				for (var ch = 0; ch < _channels; ch++)
				{
					output[dstIdx + ch] = input[leftIdx + ch] * fadeOut + input[rightIdx + ch] * fadeIn;
				}
			}

			var postStartInput = spliceIdx + halfWindow;
			var postStartOutput = spliceIdx + halfWindow + 1;
			var postFrames = outFrames - postStartOutput;
			if (postFrames > 0)
			{
				input.Slice(postStartInput * _channels, postFrames * _channels)
					.CopyTo(output[(postStartOutput * _channels)..]);
			}
		}

		private int MillisecondsToBytes(int milliseconds) =>
			AlignToFrame(Math.Max(_bytesPerFrame, milliseconds * _bytesPerSecond / 1000));

		private int AlignToFrame(int bytes) => bytes / _bytesPerFrame * _bytesPerFrame;
	}
}
