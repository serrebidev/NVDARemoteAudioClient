using System.Text;

namespace NVDARemoteAudioHelper;

internal static class Program
{
	public static async Task<int> Main(string[] args)
	{
		try
		{
			Console.OutputEncoding = new UTF8Encoding(false);
			var options = HelperOptions.Parse(args);
			if (options.ShowHelp)
			{
				Console.WriteLine(HelperOptions.Usage);
				return 0;
			}
			if (options.ListAudioApps)
			{
				AudioDeviceCatalog.WriteAudioApps();
				return 0;
			}
			if (options.ListOutputDevices)
			{
				AudioDeviceCatalog.WriteOutputDevices();
				return 0;
			}

			using var timerResolution = new SystemTimerResolution();
			JsonLog.Write("status", $"Starting {options.Role} connection to {options.Host}:{options.Port}.");

			using var cts = new CancellationTokenSource();
			Console.CancelKeyPress += (_, eventArgs) =>
			{
				eventArgs.Cancel = true;
				cts.Cancel();
			};

			// Watch the parent process via stdin. When NVDA closes stdin (or writes any
			// byte) we treat it as a graceful shutdown request, so the session's
			// IAsyncDisposable.DisposeAsync runs and WASAPI / sockets are released cleanly.
			_ = Task.Run(() => WatchParentShutdownAsync(cts));

			await using var session = await RemoteAudioSession.ConnectAsync(
				options.Host,
				options.Port,
				options.Key,
				options.Role,
				cts.Token);

			if (options.Role == ConnectionRole.Publisher)
			{
				if (options.TestTone)
				{
					await AudioPublisher.RunTestToneAsync(session, options.Bitrate, options.OpusFrameMs, options.OpusFec, cts.Token);
				}
				else
				{
					var targetPid = options.ExcludePid;
					var includeTargetTree = false;
					var captureLabel = "System audio (NVDA excluded)";
					if (!string.IsNullOrWhiteSpace(options.CaptureProcessName))
					{
						targetPid = AudioDeviceCatalog.FindAudioAppPid(options.CaptureProcessName);
						includeTargetTree = true;
						captureLabel = options.CaptureProcessName;
					}
					await AudioPublisher.RunCaptureAsync(session, targetPid, includeTargetTree, captureLabel, options.Bitrate, options.OpusFrameMs, options.OpusFec, cts.Token);
				}
			}
			else
			{
				await AudioSubscriber.RunAsync(
					session,
					options.PrebufferMs,
					options.OutputLatencyMs,
					options.PlaybackBufferMs,
					options.OpusFrameMs,
					options.OutputDeviceId,
					options.ReceiveVolume,
					cts.Token);
			}

			return 0;
		}
		catch (OperationCanceledException)
		{
			JsonLog.Write("status", "Stopped.");
			return 0;
		}
		catch (Exception ex)
		{
			JsonLog.Write("error", ex.Message, new Dictionary<string, object?>
			{
				["type"] = ex.GetType().Name,
			});
			return 1;
		}
	}

	private static async Task WatchParentShutdownAsync(CancellationTokenSource cts)
	{
		try
		{
			using var stdin = Console.OpenStandardInput();
			var buffer = new byte[16];
			while (!cts.IsCancellationRequested)
			{
				int read;
				try
				{
					read = await stdin.ReadAsync(buffer.AsMemory(), cts.Token);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (IOException)
				{
					// Pipe broke (parent died). Treat as shutdown.
					read = 0;
				}

				if (read == 0)
				{
					// EOF on stdin: NVDA closed the pipe to ask us to stop gracefully.
					cts.Cancel();
					return;
				}

				// Any byte from the parent is also a stop signal.
				cts.Cancel();
				return;
			}
		}
		catch
		{
			// Best effort. If this path fails, the parent can still TerminateProcess us.
		}
	}
}
