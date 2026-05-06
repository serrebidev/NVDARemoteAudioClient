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

			JsonLog.Write("status", $"Starting {options.Role} connection to {options.Host}:{options.Port}.");

			using var cts = new CancellationTokenSource();
			Console.CancelKeyPress += (_, eventArgs) =>
			{
				eventArgs.Cancel = true;
				cts.Cancel();
			};

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
					await AudioPublisher.RunTestToneAsync(session, options.Bitrate, cts.Token);
				}
				else
				{
					await AudioPublisher.RunCaptureAsync(session, options.ExcludePid, options.Bitrate, cts.Token);
				}
			}
			else
			{
				await AudioSubscriber.RunAsync(
					session,
					options.PrebufferMs,
					options.OutputLatencyMs,
					options.PlaybackBufferMs,
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
}
