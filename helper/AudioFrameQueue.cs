using System.Buffers;
using System.Runtime.CompilerServices;

namespace NVDARemoteAudioHelper;

internal sealed class PooledAudioFrame : IDisposable
{
	private short[]? _buffer;

	public PooledAudioFrame(int length)
	{
		_buffer = ArrayPool<short>.Shared.Rent(length);
		Length = length;
	}

	public int Length { get; }

	public Span<short> Span => Buffer.AsSpan(0, Length);

	public ReadOnlySpan<short> ReadOnlySpan => Buffer.AsSpan(0, Length);

	private short[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PooledAudioFrame));

	public void Dispose()
	{
		var buffer = Interlocked.Exchange(ref _buffer, null);
		if (buffer is not null)
		{
			ArrayPool<short>.Shared.Return(buffer);
		}
	}
}

internal sealed class AudioFrameQueue : IDisposable
{
	private readonly object _gate = new();
	private readonly Queue<PooledAudioFrame> _frames = new();
	private readonly SemaphoreSlim _available = new(0);
	private readonly int _capacity;
	private bool _completed;
	private long _droppedFrames;

	public AudioFrameQueue(int capacity)
	{
		_capacity = Math.Max(1, capacity);
	}

	public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

	public bool TryWrite(PooledAudioFrame frame)
	{
		PooledAudioFrame? dropped = null;
		var shouldRelease = true;
		var accepted = false;
		lock (_gate)
		{
			if (_completed)
			{
				dropped = frame;
				shouldRelease = false;
			}
			else
			{
				if (_frames.Count >= _capacity)
				{
					dropped = _frames.Dequeue();
					Interlocked.Increment(ref _droppedFrames);
					shouldRelease = false;
				}

				_frames.Enqueue(frame);
				accepted = true;
			}
		}

		dropped?.Dispose();
		if (shouldRelease)
		{
			_available.Release();
		}

		return accepted;
	}

	public void Complete()
	{
		lock (_gate)
		{
			_completed = true;
		}

		_available.Release();
	}

	public async IAsyncEnumerable<PooledAudioFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		while (true)
		{
			await _available.WaitAsync(cancellationToken);
			PooledAudioFrame? frame = null;
			lock (_gate)
			{
				if (_frames.Count > 0)
				{
					frame = _frames.Dequeue();
				}
				else if (_completed)
				{
					yield break;
				}
			}

			if (frame is not null)
			{
				yield return frame;
			}
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_completed = true;
			while (_frames.Count > 0)
			{
				_frames.Dequeue().Dispose();
			}
		}

		_available.Dispose();
	}
}
