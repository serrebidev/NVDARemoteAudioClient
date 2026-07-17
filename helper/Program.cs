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
			if (options.SelfTest)
			{
				HelperSelfTest.Run();
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
			await RunWithReconnectAsync(options, cts.Token);
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

	private static async Task RunWithReconnectAsync(HelperOptions options, CancellationToken shutdownToken)
	{
		const int ReconnectDelayMs = 2000;
		const int ReconnectAttemptTimeoutMs = 2000;
		var hasConnected = false;

		while (true)
		{
			shutdownToken.ThrowIfCancellationRequested();
			RemoteAudioSession? session = null;
			try
			{
				using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
				if (hasConnected)
				{
					connectionCts.CancelAfter(ReconnectAttemptTimeoutMs);
				}
				session = await RemoteAudioSession.ConnectAsync(
					options.Host,
					options.Port,
					options.Key,
					options.Role,
					connectionCts.Token,
					shutdownToken);
				hasConnected = true;

				using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(
					shutdownToken,
					session.LifetimeToken);
				await RunAudioAsync(options, session, sessionCts.Token);
				return;
			}
			catch (OperationCanceledException) when (
				session is not null &&
				session.LifetimeToken.IsCancellationRequested &&
				!shutdownToken.IsCancellationRequested)
			{
				// A failed TCP or UDP heartbeat cancels the session. Tear down capture or
				// playback, dispose its sockets, and reconnect without requiring an NVDA restart.
			}
			catch (System.Net.Sockets.SocketException) when (
				hasConnected && !shutdownToken.IsCancellationRequested)
			{
				// The relay may still be starting. Once a session has connected successfully,
				// keep retrying transient socket failures until the parent asks us to stop.
			}
			catch (IOException) when (
				session is null && hasConnected && !shutdownToken.IsCancellationRequested)
			{
				// The relay can disappear during its handshake or UDP registration while an
				// update is replacing it. Treat that as another transient reconnect attempt.
			}
			catch (OperationCanceledException) when (
				session is null && hasConnected && !shutdownToken.IsCancellationRequested)
			{
				// Bound each reconnect attempt so a half-open route cannot stall recovery.
			}
			finally
			{
				if (session is not null)
				{
					await session.DisposeAsync();
				}
			}

			JsonLog.Write(
				"status",
				$"Connection lost. Reconnecting in {ReconnectDelayMs / 1000} seconds.",
				new Dictionary<string, object?> { ["reconnecting"] = true });
			await Task.Delay(ReconnectDelayMs, shutdownToken);
			JsonLog.Write("status", $"Reconnecting to {options.Host}:{options.Port}.");
		}
	}

	private static async Task RunAudioAsync(
		HelperOptions options,
		RemoteAudioSession session,
		CancellationToken cancellationToken)
	{
		if (options.Role == ConnectionRole.Publisher)
		{
			if (options.TestTone)
			{
				await AudioPublisher.RunTestToneAsync(session, options.Bitrate, options.OpusFrameMs, options.OpusFec, options.Codec, options.Password, options.Key, cancellationToken);
				return;
			}

			var targetPid = options.ExcludePid;
			var includeTargetTree = false;
			var captureLabel = "System audio (NVDA excluded)";
			if (!string.IsNullOrWhiteSpace(options.CaptureProcessName))
			{
				targetPid = AudioDeviceCatalog.FindAudioAppPid(options.CaptureProcessName);
				includeTargetTree = true;
				captureLabel = options.CaptureProcessName;
			}
			await AudioPublisher.RunCaptureAsync(session, targetPid, includeTargetTree, captureLabel, options.Bitrate, options.OpusFrameMs, options.OpusFec, options.Codec, options.Password, options.Key, cancellationToken);
			return;
		}

		await AudioSubscriber.RunAsync(
			session,
			options.PrebufferMs,
			options.OutputLatencyMs,
			options.PlaybackBufferMs,
			options.OpusFrameMs,
			options.OutputDeviceId,
			options.ReceiveVolume,
			options.ReceivePan,
			options.BassDb,
			options.MidDb,
			options.TrebleDb,
			options.Password,
			options.Key,
			options.RecordFolder,
			cancellationToken);
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
