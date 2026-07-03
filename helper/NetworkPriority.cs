using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NVDARemoteAudioHelper;

internal sealed class NetworkPriority : IDisposable
{
	private IntPtr _qosHandle;
	private Socket? _attachedSocket;
	private uint _flowId;

	public bool Attach(Socket socket, Action<string>? onDiagnostic = null)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		try
		{
			var version = new QosVersion { MajorVersion = 1, MinorVersion = 0 };
			if (!QOSCreateHandle(ref version, out _qosHandle))
			{
				onDiagnostic?.Invoke("qWAVE priority unavailable: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
				return false;
			}

			var id = 0u;
			if (!QOSAddSocketToFlow(_qosHandle, socket.Handle, IntPtr.Zero, QosTrafficTypeVoice, QosNonAdaptiveFlow, ref id))
			{
				onDiagnostic?.Invoke("qWAVE priority attach failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
				QOSCloseHandle(_qosHandle);
				_qosHandle = IntPtr.Zero;
				return false;
			}

			_attachedSocket = socket;
			_flowId = id;
			onDiagnostic?.Invoke("qWAVE voice priority attached.");
			return true;
		}
		catch (Exception ex)
		{
			onDiagnostic?.Invoke($"qWAVE priority attach failed: {ex.GetType().Name}: {ex.Message}");
			if (_qosHandle != IntPtr.Zero)
			{
				try
				{
					QOSCloseHandle(_qosHandle);
				}
				catch
				{
				}

				_qosHandle = IntPtr.Zero;
			}

			return false;
		}
	}

	public void Dispose()
	{
		if (_qosHandle == IntPtr.Zero)
		{
			return;
		}

		try
		{
			if (_attachedSocket is not null && _flowId != 0)
			{
				QOSRemoveSocketFromFlow(_qosHandle, _attachedSocket.Handle, _flowId, 0);
			}
		}
		catch
		{
		}

		try
		{
			QOSCloseHandle(_qosHandle);
		}
		catch
		{
		}

		_qosHandle = IntPtr.Zero;
		_attachedSocket = null;
		_flowId = 0;
	}

	private const int QosTrafficTypeVoice = 4;
	private const uint QosNonAdaptiveFlow = 0x2;

	[StructLayout(LayoutKind.Sequential)]
	private struct QosVersion
	{
		public ushort MajorVersion;
		public ushort MinorVersion;
	}

	[DllImport("qwave.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QOSCreateHandle(ref QosVersion version, out IntPtr qosHandle);

	[DllImport("qwave.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QOSCloseHandle(IntPtr qosHandle);

	[DllImport("qwave.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QOSAddSocketToFlow(
		IntPtr qosHandle,
		IntPtr socket,
		IntPtr destAddr,
		int trafficType,
		uint flags,
		ref uint flowId);

	[DllImport("qwave.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QOSRemoveSocketFromFlow(
		IntPtr qosHandle,
		IntPtr socket,
		uint flowId,
		uint flags);
}
