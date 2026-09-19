using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace NVDARemoteAudioHelper;

internal sealed record InputDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Captures a microphone or other recording device and cuts it into the same 48 kHz
/// stereo 16-bit frames the loopback capture produces, so every sender downstream
/// treats the two alike. Devices run at whatever their Windows mix format is; this
/// converts the sample type, folds or spreads channels to stereo, and resamples.
/// </summary>
internal sealed class InputDeviceCapture
{
	private const int SampleRate = 48000;
	private const int Channels = 2;
	public const string DefaultDeviceId = "default";

	private readonly string _deviceId;
	private readonly short[] _frameBuffer;
	private int _frameOffset;

	public InputDeviceCapture(string deviceId, int frameSamplesPerChannel)
	{
		_deviceId = (deviceId ?? "").Trim();
		_frameBuffer = new short[Math.Clamp(frameSamplesPerChannel, 120, 960) * Channels];
	}

	public static IReadOnlyList<InputDeviceInfo> Snapshot()
	{
		var devices = new List<InputDeviceInfo>();
		using var enumerator = new MMDeviceEnumerator();
		string defaultId = "";
		try
		{
			if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
			{
				using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
				defaultId = defaultDevice.ID;
			}
		}
		catch
		{
			// A machine with no recording device has no default either.
		}
		foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
		{
			try
			{
				devices.Add(new InputDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
			}
			finally
			{
				device.Dispose();
			}
		}
		return devices;
	}

	public static void WriteInputDevices()
	{
		var devices = Snapshot();
		JsonLog.Write("input_devices", $"Found {devices.Count} recording devices.", new Dictionary<string, object?>
		{
			["devices"] = devices,
		});
	}

	public async Task RunAsync(AudioFrameQueue writer, CancellationToken cancellationToken)
	{
		using var enumerator = new MMDeviceEnumerator();
		MMDevice device;
		try
		{
			device = _deviceId.Length == 0 || _deviceId.Equals(DefaultDeviceId, StringComparison.OrdinalIgnoreCase)
				? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
				: enumerator.GetDevice(_deviceId);
		}
		catch (Exception ex)
		{
			writer.Complete();
			throw new InvalidOperationException("The chosen microphone or recording device is not available. Plug it in or choose another one in settings.", ex);
		}

		using (device)
		using (var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20))
		{
			var format = capture.WaveFormat;
			var converter = new Converter(format);
			var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
			capture.DataAvailable += (_, args) =>
			{
				try
				{
					converter.Convert(args.Buffer.AsSpan(0, args.BytesRecorded), samples => AppendSamples(samples, writer));
				}
				catch (Exception ex)
				{
					stopped.TrySetResult(ex);
				}
			};
			capture.RecordingStopped += (_, args) => stopped.TrySetResult(args.Exception);
			capture.StartRecording();
			JsonLog.Write("status", "Capture started.", new Dictionary<string, object?>
			{
				["capture_device"] = device.FriendlyName,
				["device_format"] = format.ToString(),
				["frame_ms"] = _frameBuffer.Length / Channels * 1000 / SampleRate,
			});
			try
			{
				var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
				var finished = await Task.WhenAny(stopped.Task, cancelled);
				if (finished == stopped.Task)
				{
					var error = await stopped.Task;
					throw new InvalidOperationException(
						"The recording device stopped" + (error is null ? "." : ": " + error.Message), error);
				}
			}
			finally
			{
				try
				{
					capture.StopRecording();
				}
				catch
				{
					// Already stopped.
				}
				writer.Complete();
			}
		}
	}

	private void AppendSamples(ReadOnlySpan<short> samples, AudioFrameQueue writer)
	{
		while (!samples.IsEmpty)
		{
			var copy = Math.Min(_frameBuffer.Length - _frameOffset, samples.Length);
			samples[..copy].CopyTo(_frameBuffer.AsSpan(_frameOffset, copy));
			_frameOffset += copy;
			samples = samples[copy..];
			if (_frameOffset < _frameBuffer.Length)
			{
				continue;
			}
			var frame = new PooledAudioFrame(_frameBuffer.Length);
			_frameBuffer.AsSpan().CopyTo(frame.Span);
			if (!writer.TryWrite(frame))
			{
				frame.Dispose();
			}
			_frameOffset = 0;
		}
	}

	/// <summary>Turns device-format bytes into 48 kHz stereo 16-bit samples.</summary>
	internal sealed class Converter
	{
		/// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, how most devices describe their float mix format.</summary>
		private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
		private readonly int _inChannels;
		private readonly int _bytesPerSample;
		private readonly bool _isFloat;
		private readonly WdlResampler? _resampler;
		private float[] _stereo = new float[4096];
		private float[] _resampled = new float[8192];
		private short[] _output = new short[8192];

		public Converter(WaveFormat format)
		{
			_inChannels = Math.Max(1, format.Channels);
			_bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
			_isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
				(format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat);
			if (!_isFloat && _bytesPerSample is not (2 or 3 or 4))
			{
				throw new InvalidOperationException($"The recording device uses an unsupported format: {format}.");
			}
			if (format.SampleRate != SampleRate)
			{
				_resampler = new WdlResampler();
				_resampler.SetMode(true, 2, false);
				_resampler.SetFilterParms();
				_resampler.SetFeedMode(true);
				_resampler.SetRates(format.SampleRate, SampleRate);
			}
		}

		public void Convert(ReadOnlySpan<byte> input, Action<ReadOnlySpan<short>> output)
		{
			var frameBytes = _bytesPerSample * _inChannels;
			var frames = input.Length / frameBytes;
			if (frames == 0)
			{
				return;
			}
			if (_stereo.Length < frames * Channels)
			{
				_stereo = new float[frames * Channels * 2];
			}
			for (var frame = 0; frame < frames; frame++)
			{
				var offset = frame * frameBytes;
				var left = ReadSample(input, offset);
				// Mono is spread to both ears; anything wider keeps its first two channels.
				var right = _inChannels > 1 ? ReadSample(input, offset + _bytesPerSample) : left;
				_stereo[frame * 2] = left;
				_stereo[(frame * 2) + 1] = right;
			}

			ReadOnlySpan<float> stereo = _stereo.AsSpan(0, frames * Channels);
			if (_resampler is not null)
			{
				var needed = _resampler.ResamplePrepare(frames, Channels, out var inBuffer, out var inOffset);
				stereo[..(needed * Channels)].CopyTo(inBuffer.AsSpan(inOffset));
				var maxOut = (frames * 4) + 64;
				if (_resampled.Length < maxOut * Channels)
				{
					_resampled = new float[maxOut * Channels];
				}
				var produced = _resampler.ResampleOut(_resampled, 0, needed, maxOut, Channels);
				stereo = _resampled.AsSpan(0, produced * Channels);
			}

			if (_output.Length < stereo.Length)
			{
				_output = new short[stereo.Length * 2];
			}
			for (var i = 0; i < stereo.Length; i++)
			{
				_output[i] = (short)Math.Clamp((int)Math.Round(stereo[i] * 32767f), short.MinValue, short.MaxValue);
			}
			output(_output.AsSpan(0, stereo.Length));
		}

		private float ReadSample(ReadOnlySpan<byte> input, int offset)
		{
			if (_isFloat)
			{
				return BitConverter.ToSingle(input.Slice(offset, 4));
			}
			return _bytesPerSample switch
			{
				2 => BitConverter.ToInt16(input.Slice(offset, 2)) / 32768f,
				3 => ((input[offset] | (input[offset + 1] << 8) | (input[offset + 2] << 16)) << 8 >> 8) / 8388608f,
				_ => BitConverter.ToInt32(input.Slice(offset, 4)) / 2147483648f,
			};
		}
	}
}

/// <summary>A 440 Hz tone at real-time pace, for tests that must not depend on a sound card.</summary>
internal static class ToneSource
{
	public static async Task RunAsync(AudioFrameQueue writer, int frameSamplesPerChannel, CancellationToken cancellationToken)
	{
		const int SampleRate = 48000;
		var phase = 0.0;
		var phaseStep = 2.0 * Math.PI * 440.0 / SampleRate;
		var frameDuration = TimeSpan.FromSeconds(frameSamplesPerChannel / (double)SampleRate);
		var start = System.Diagnostics.Stopwatch.StartNew();
		var next = TimeSpan.Zero;
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var frame = new PooledAudioFrame(frameSamplesPerChannel * 2);
				var span = frame.Span;
				for (var i = 0; i < frameSamplesPerChannel; i++)
				{
					var value = (short)(Math.Sin(phase) * short.MaxValue * 0.12);
					span[i * 2] = value;
					span[(i * 2) + 1] = value;
					phase += phaseStep;
					if (phase >= Math.PI * 2.0)
					{
						phase -= Math.PI * 2.0;
					}
				}
				if (!writer.TryWrite(frame))
				{
					frame.Dispose();
				}
				next += frameDuration;
				var delay = next - start.Elapsed;
				if (delay > TimeSpan.Zero)
				{
					await Task.Delay(delay, cancellationToken);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			writer.Complete();
		}
	}
}
