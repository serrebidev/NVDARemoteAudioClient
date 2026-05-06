using System.Runtime.InteropServices;
using NAudio.Wave;

namespace NVDARemoteAudioHelper;

internal sealed class PlaybackSink : IDisposable
{
	private readonly BufferedWaveProvider _buffer;
	private readonly WaveOutEvent _output;
	private readonly int _bytesPerMillisecond;
	private readonly int _prebufferMilliseconds;
	private bool _playing;

	public PlaybackSink(int sampleRate, int channels, int prebufferMilliseconds, int outputLatencyMilliseconds, int bufferMilliseconds)
	{
		_prebufferMilliseconds = Math.Clamp(prebufferMilliseconds, 30, 1000);
		_buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
		{
			BufferDuration = TimeSpan.FromMilliseconds(Math.Clamp(bufferMilliseconds, 100, 3000)),
			DiscardOnBufferOverflow = true,
			ReadFully = true,
		};
		_output = new WaveOutEvent
		{
			DesiredLatency = Math.Clamp(outputLatencyMilliseconds, 30, 1000),
			NumberOfBuffers = 3,
		};
		_output.Init(_buffer);
		_bytesPerMillisecond = sampleRate * channels * sizeof(short) / 1000;
	}

	public void AddSamples(ReadOnlySpan<short> samples)
	{
		var bytes = MemoryMarshal.AsBytes(samples);
		var copy = bytes.ToArray();
		_buffer.AddSamples(copy, 0, copy.Length);
		UpdatePlaybackState();
	}

	public void Dispose()
	{
		_output.Stop();
		_output.Dispose();
	}

	private void UpdatePlaybackState()
	{
		var bufferedMs = _buffer.BufferedBytes / Math.Max(1, _bytesPerMillisecond);
		if (!_playing)
		{
			if (bufferedMs >= _prebufferMilliseconds)
			{
				_output.Play();
				_playing = true;
				JsonLog.Write("status", "Playback started.");
			}

			return;
		}
	}
}
