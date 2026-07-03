using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NVDARemoteAudioHelper;

[SupportedOSPlatform("windows")]
internal sealed class ProcessLoopbackCapture
{
	private const int SampleRate = 48000;
	private const int Channels = 2;
	private const int BitsPerSample = 16;
	private const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";
	private readonly int _excludePid;
	private readonly short[] _frameBuffer;
	private int _frameOffset;

	public ProcessLoopbackCapture(int excludePid, int frameSamplesPerChannel)
	{
		_excludePid = excludePid;
		_frameBuffer = new short[Math.Clamp(frameSamplesPerChannel, 120, 960) * Channels];
	}

	public async Task RunAsync(AudioFrameQueue writer, CancellationToken cancellationToken)
	{
		IAudioClient? audioClient = null;
		IAudioCaptureClient? captureClient = null;
		using var sampleReady = new AutoResetEvent(false);

		try
		{
			audioClient = await ActivateAudioClientAsync(_excludePid, cancellationToken);
			InitializeAudioClient(audioClient, sampleReady.SafeWaitHandle);
			captureClient = GetCaptureClient(audioClient);
			using var threadBoost = new WindowsAudioThreadBoost("Capture");

			HResult.ThrowIfFailed(audioClient.Start(), "IAudioClient.Start");
			JsonLog.Write("status", "Capture started.", new Dictionary<string, object?>
			{
				["frame_ms"] = _frameBuffer.Length / Channels * 1000 / SampleRate,
				["thread_boost"] = threadBoost.Mode,
			});

			var waitHandles = new WaitHandle[] { sampleReady, cancellationToken.WaitHandle };
			while (!cancellationToken.IsCancellationRequested)
			{
				var waitResult = WaitHandle.WaitAny(waitHandles, 1000);
				if (waitResult == 0)
				{
					DrainCapturePackets(captureClient, writer);
				}
			}
		}
		finally
		{
			writer.Complete();
			if (audioClient is not null)
			{
				try
				{
					audioClient.Stop();
				}
				catch
				{
					// Ignore shutdown races.
				}
			}

			ReleaseComObject(captureClient);
			ReleaseComObject(audioClient);
		}
	}

	private static async Task<IAudioClient> ActivateAudioClientAsync(int excludePid, CancellationToken cancellationToken)
	{
		var activation = new AudioClientActivationParams
		{
			ActivationType = AudioClientActivationType.ProcessLoopback,
			ProcessLoopbackParams = new AudioClientProcessLoopbackParams
			{
				TargetProcessId = (uint)excludePid,
				ProcessLoopbackMode = ProcessLoopbackMode.ExcludeTargetProcessTree,
			},
		};

		var activationSize = Marshal.SizeOf<AudioClientActivationParams>();
		var activationPtr = Marshal.AllocHGlobal(activationSize);
		var propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
		IActivateAudioInterfaceAsyncOperation? asyncOperation = null;

		try
		{
			Marshal.StructureToPtr(activation, activationPtr, false);
			var propVariant = new PropVariant
			{
				Vt = 65,
				Blob = new Blob
				{
					Size = activationSize,
					Data = activationPtr,
				},
			};
			Marshal.StructureToPtr(propVariant, propVariantPtr, false);

			var handler = new ActivateAudioInterfaceCompletionHandler();
			var audioClientGuid = ComGuids.IAudioClient;
			var hr = NativeMethods.ActivateAudioInterfaceAsync(
				VirtualAudioDeviceProcessLoopback,
				ref audioClientGuid,
				propVariantPtr,
				handler,
				out asyncOperation);
			HResult.ThrowIfFailed(hr, "ActivateAudioInterfaceAsync");

			var activated = await handler.Task.WaitAsync(cancellationToken);
			return (IAudioClient)activated;
		}
		finally
		{
			GC.KeepAlive(asyncOperation);
			Marshal.FreeHGlobal(propVariantPtr);
			Marshal.FreeHGlobal(activationPtr);
		}
	}

	private static void InitializeAudioClient(IAudioClient audioClient, SafeWaitHandle sampleReadyHandle)
	{
		var format = WaveFormatEx.CreatePcm(SampleRate, Channels, BitsPerSample);
		var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());

		try
		{
			Marshal.StructureToPtr(format, formatPtr, false);
			var flags =
				AudioClientStreamFlags.Loopback |
				AudioClientStreamFlags.EventCallback |
				AudioClientStreamFlags.AutoConvertPcm |
				AudioClientStreamFlags.SrcDefaultQuality;

			var hr = audioClient.Initialize(
				AudioClientShareMode.Shared,
				flags,
				0,
				0,
				formatPtr,
				IntPtr.Zero);
			HResult.ThrowIfFailed(hr, "IAudioClient.Initialize");

			hr = audioClient.SetEventHandle(sampleReadyHandle.DangerousGetHandle());
			HResult.ThrowIfFailed(hr, "IAudioClient.SetEventHandle");
		}
		finally
		{
			Marshal.FreeHGlobal(formatPtr);
		}
	}

	private static IAudioCaptureClient GetCaptureClient(IAudioClient audioClient)
	{
		var captureClientGuid = ComGuids.IAudioCaptureClient;
		var hr = audioClient.GetService(ref captureClientGuid, out var service);
		HResult.ThrowIfFailed(hr, "IAudioClient.GetService(IAudioCaptureClient)");
		return (IAudioCaptureClient)service;
	}

	private unsafe void DrainCapturePackets(IAudioCaptureClient captureClient, AudioFrameQueue writer)
	{
		while (true)
		{
			var hr = captureClient.GetNextPacketSize(out var framesAvailable);
			HResult.ThrowIfFailed(hr, "IAudioCaptureClient.GetNextPacketSize");
			if (framesAvailable == 0)
			{
				return;
			}

			hr = captureClient.GetBuffer(
				out var data,
				out framesAvailable,
				out var flags,
				out _,
				out _);
			HResult.ThrowIfFailed(hr, "IAudioCaptureClient.GetBuffer");

			try
			{
				var sampleCount = checked((int)framesAvailable * Channels);
				if ((flags & AudioClientBufferFlags.Silent) != 0 || data == IntPtr.Zero)
				{
					AppendSilence(sampleCount, writer);
				}
				else
				{
					var samples = new ReadOnlySpan<short>((void*)data, sampleCount);
					AppendSamples(samples, writer);
				}
			}
			finally
			{
				HResult.ThrowIfFailed(captureClient.ReleaseBuffer(framesAvailable), "IAudioCaptureClient.ReleaseBuffer");
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
			PublishFrameIfReady(writer);
		}
	}

	private void AppendSilence(int sampleCount, AudioFrameQueue writer)
	{
		while (sampleCount > 0)
		{
			var copy = Math.Min(_frameBuffer.Length - _frameOffset, sampleCount);
			_frameBuffer.AsSpan(_frameOffset, copy).Clear();
			_frameOffset += copy;
			sampleCount -= copy;
			PublishFrameIfReady(writer);
		}
	}

	private void PublishFrameIfReady(AudioFrameQueue writer)
	{
		if (_frameOffset < _frameBuffer.Length)
		{
			return;
		}

		var frame = new PooledAudioFrame(_frameBuffer.Length);
		_frameBuffer.AsSpan().CopyTo(frame.Span);
		if (!writer.TryWrite(frame))
		{
			frame.Dispose();
		}
		_frameOffset = 0;
	}

	private static void ReleaseComObject(object? value)
	{
		if (value is not null && Marshal.IsComObject(value))
		{
			Marshal.FinalReleaseComObject(value);
		}
	}
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ActivateAudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler
{
	private readonly TaskCompletionSource<object> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public Task<object> Task => _completion.Task;

	public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
	{
		try
		{
			var hr = operation.GetActivateResult(out var activateResult, out var activatedInterface);
			HResult.ThrowIfFailed(hr, "IActivateAudioInterfaceAsyncOperation.GetActivateResult");
			HResult.ThrowIfFailed(activateResult, "Audio interface activation");
			_completion.TrySetResult(activatedInterface);
		}
		catch (Exception ex)
		{
			_completion.TrySetException(ex);
		}

		return 0;
	}
}

internal static class NativeMethods
{
	[DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, PreserveSig = true)]
	public static extern int ActivateAudioInterfaceAsync(
		[MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
		ref Guid riid,
		IntPtr activationParams,
		IActivateAudioInterfaceCompletionHandler completionHandler,
		out IActivateAudioInterfaceAsyncOperation activationOperation);
}

internal static class ComGuids
{
	public static readonly Guid IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
	public static readonly Guid IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
	[PreserveSig]
	int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
	[PreserveSig]
	int GetActivateResult(
		out int activateResult,
		[MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
	[PreserveSig]
	int Initialize(
		AudioClientShareMode shareMode,
		AudioClientStreamFlags streamFlags,
		long hnsBufferDuration,
		long hnsPeriodicity,
		IntPtr format,
		IntPtr audioSessionGuid);

	[PreserveSig]
	int GetBufferSize(out uint bufferSize);

	[PreserveSig]
	int GetStreamLatency(out long latency);

	[PreserveSig]
	int GetCurrentPadding(out uint currentPadding);

	[PreserveSig]
	int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch);

	[PreserveSig]
	int GetMixFormat(out IntPtr deviceFormat);

	[PreserveSig]
	int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

	[PreserveSig]
	int Start();

	[PreserveSig]
	int Stop();

	[PreserveSig]
	int Reset();

	[PreserveSig]
	int SetEventHandle(IntPtr eventHandle);

	[PreserveSig]
	int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
	[PreserveSig]
	int GetBuffer(
		out IntPtr data,
		out uint framesAvailable,
		out AudioClientBufferFlags flags,
		out ulong devicePosition,
		out ulong qpcPosition);

	[PreserveSig]
	int ReleaseBuffer(uint framesRead);

	[PreserveSig]
	int GetNextPacketSize(out uint framesInNextPacket);
}

internal enum AudioClientShareMode
{
	Shared = 0,
	Exclusive = 1,
}

[Flags]
internal enum AudioClientStreamFlags : uint
{
	Loopback = 0x00020000,
	EventCallback = 0x00040000,
	SrcDefaultQuality = 0x08000000,
	AutoConvertPcm = 0x80000000,
}

[Flags]
internal enum AudioClientBufferFlags : uint
{
	None = 0,
	DataDiscontinuity = 0x1,
	Silent = 0x2,
	TimestampError = 0x4,
}

internal enum AudioClientActivationType
{
	Default = 0,
	ProcessLoopback = 1,
}

internal enum ProcessLoopbackMode
{
	IncludeTargetProcessTree = 0,
	ExcludeTargetProcessTree = 1,
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
	public AudioClientActivationType ActivationType;
	public AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
	public uint TargetProcessId;
	public ProcessLoopbackMode ProcessLoopbackMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Blob
{
	public int Size;
	public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
	public ushort Vt;
	public ushort Reserved1;
	public ushort Reserved2;
	public ushort Reserved3;
	public Blob Blob;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
	public ushort FormatTag;
	public ushort Channels;
	public uint SamplesPerSec;
	public uint AvgBytesPerSec;
	public ushort BlockAlign;
	public ushort BitsPerSample;
	public ushort Size;

	public static WaveFormatEx CreatePcm(int sampleRate, int channels, int bitsPerSample)
	{
		var blockAlign = (ushort)(channels * bitsPerSample / 8);
		return new WaveFormatEx
		{
			FormatTag = 1,
			Channels = (ushort)channels,
			SamplesPerSec = (uint)sampleRate,
			AvgBytesPerSec = (uint)(sampleRate * blockAlign),
			BlockAlign = blockAlign,
			BitsPerSample = (ushort)bitsPerSample,
			Size = 0,
		};
	}
}
