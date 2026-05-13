using System.Runtime.InteropServices;

namespace NVDARemoteAudioHelper;

internal sealed class WindowsAudioThreadBoost : IDisposable
{
	private readonly IntPtr _avrtHandle;
	private readonly ThreadPriority _previousPriority;
	private readonly int _ownerThreadId;

	public WindowsAudioThreadBoost(string taskName)
	{
		_ownerThreadId = Environment.CurrentManagedThreadId;
		_previousPriority = Thread.CurrentThread.Priority;
		Thread.CurrentThread.Priority = ThreadPriority.Highest;
		Mode = "ThreadPriority.Highest";

		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		_avrtHandle = AvSetMmThreadCharacteristics(taskName, out _);
		if (_avrtHandle == IntPtr.Zero && !string.Equals(taskName, "Audio", StringComparison.OrdinalIgnoreCase))
		{
			_avrtHandle = AvSetMmThreadCharacteristics("Audio", out _);
			if (_avrtHandle != IntPtr.Zero)
			{
				taskName = "Audio";
			}
		}

		if (_avrtHandle != IntPtr.Zero)
		{
			AvSetMmThreadPriority(_avrtHandle, AvrtPriority.High);
			Mode = $"MMCSS {taskName}";
		}
	}

	public string Mode { get; }

	public void Dispose()
	{
		if (Environment.CurrentManagedThreadId != _ownerThreadId)
		{
			return;
		}

		if (_avrtHandle != IntPtr.Zero)
		{
			AvRevertMmThreadCharacteristics(_avrtHandle);
		}

		Thread.CurrentThread.Priority = _previousPriority;
	}

	[DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "AvSetMmThreadCharacteristicsW")]
	private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

	[DllImport("avrt.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, AvrtPriority priority);

	[DllImport("avrt.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

	private enum AvrtPriority
	{
		Low = -1,
		Normal = 0,
		High = 1,
		Critical = 2,
	}
}
