using System.Runtime.InteropServices;

namespace NVDARemoteAudioHelper;

internal sealed class SystemTimerResolution : IDisposable
{
	private readonly uint _periodMs;
	private readonly bool _acquired;

	public SystemTimerResolution(uint periodMs = 1)
	{
		_periodMs = periodMs;
		try
		{
			_acquired = timeBeginPeriod(periodMs) == 0;
		}
		catch
		{
			_acquired = false;
		}
	}

	public void Dispose()
	{
		if (!_acquired)
		{
			return;
		}

		try
		{
			timeEndPeriod(_periodMs);
		}
		catch
		{
			// Process exit reclaims the timer request if this best-effort release fails.
		}
	}

	[DllImport("winmm.dll")]
	private static extern uint timeBeginPeriod(uint uMilliseconds);

	[DllImport("winmm.dll")]
	private static extern uint timeEndPeriod(uint uMilliseconds);
}
