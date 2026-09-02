namespace NVDARemoteAudioHelper;

internal sealed class AudioRingBuffer
{
	private readonly byte[] _storage;
	private readonly int _mask;
	private int _head;
	private int _tail;
	private long _underruns;
	private long _drops;

	public AudioRingBuffer(int capacityBytes)
	{
		var capacity = 1;
		while (capacity < Math.Max(64, capacityBytes))
		{
			capacity <<= 1;
		}

		_storage = new byte[capacity];
		_mask = capacity - 1;
	}

	public int BufferedBytes => (Volatile.Read(ref _tail) - Volatile.Read(ref _head)) & 0x7FFFFFFF;
	public long Underruns => Interlocked.Read(ref _underruns);
	public long Drops => Interlocked.Read(ref _drops);

	public void Write(ReadOnlySpan<byte> source)
	{
		var currentTail = _tail;
		var currentHead = Volatile.Read(ref _head);
		var available = _storage.Length - ((currentTail - currentHead) & 0x7FFFFFFF);

		if (source.Length > available)
		{
			var deficit = source.Length - available;
			Volatile.Write(ref _head, (currentHead + deficit) & 0x7FFFFFFF);
			Interlocked.Add(ref _drops, deficit);
		}

		var writeIndex = currentTail & _mask;
		var firstChunk = Math.Min(source.Length, _storage.Length - writeIndex);
		source[..firstChunk].CopyTo(_storage.AsSpan(writeIndex));
		if (firstChunk < source.Length)
		{
			source[firstChunk..].CopyTo(_storage.AsSpan(0));
		}

		Volatile.Write(ref _tail, (currentTail + source.Length) & 0x7FFFFFFF);
	}

	public int Read(Span<byte> destination)
	{
		var currentHead = _head;
		var currentTail = Volatile.Read(ref _tail);
		var available = (currentTail - currentHead) & 0x7FFFFFFF;
		var toRead = Math.Min(destination.Length, available);

		if (toRead > 0)
		{
			var readIndex = currentHead & _mask;
			var firstChunk = Math.Min(toRead, _storage.Length - readIndex);
			_storage.AsSpan(readIndex, firstChunk).CopyTo(destination);
			if (firstChunk < toRead)
			{
				_storage.AsSpan(0, toRead - firstChunk).CopyTo(destination[firstChunk..]);
			}

			Volatile.Write(ref _head, (currentHead + toRead) & 0x7FFFFFFF);
		}

		if (toRead < destination.Length)
		{
			destination[toRead..].Clear();
			Interlocked.Increment(ref _underruns);
		}

		return toRead;
	}

	public int ReadFloats(Span<float> destination)
	{
		var bytesRead = Read(System.Runtime.InteropServices.MemoryMarshal.AsBytes(destination));
		return bytesRead / sizeof(float);
	}

	/// <summary>
	/// Discards everything buffered without counting it against <see cref="Drops"/>.
	///
	/// A drop means audio that arrived and could not be kept -- a real symptom.
	/// Deliberately emptying the buffer when playback moves to another device is
	/// not that, and counting it as one would make a healthy device switch read as
	/// buffer overflow in the diagnostics report.
	/// </summary>
	public void Clear() => Volatile.Write(ref _head, Volatile.Read(ref _tail));

	public void DropOldest(int bytesToDrop)
	{
		if (bytesToDrop <= 0)
		{
			return;
		}

		var currentHead = _head;
		var currentTail = Volatile.Read(ref _tail);
		var available = (currentTail - currentHead) & 0x7FFFFFFF;
		var actual = Math.Min(bytesToDrop, available);
		if (actual <= 0)
		{
			return;
		}

		Volatile.Write(ref _head, (currentHead + actual) & 0x7FFFFFFF);
		Interlocked.Add(ref _drops, actual);
	}
}
