using System.Runtime.InteropServices;

namespace NVDARemoteAudioHelper;

internal static class HResult
{
	public static void ThrowIfFailed(int hr, string action)
	{
		if (hr < 0)
		{
			throw new COMException($"{action} failed with HRESULT 0x{unchecked((uint)hr):X8}.", hr);
		}
	}
}
