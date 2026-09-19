namespace NVDARemoteAudioHelper;

/// <summary>
/// Chooses what a sending helper captures: the system mix without NVDA, one
/// application, a recording device, or a generated tone for tests.
/// </summary>
internal static class CaptureSources
{
	public static string Describe(HelperOptions options)
	{
		if (options.TestTone)
		{
			return "Test tone";
		}
		if (!string.IsNullOrWhiteSpace(options.CaptureDeviceId))
		{
			return "Recording device";
		}
		return string.IsNullOrWhiteSpace(options.CaptureProcessName)
			? "System audio (NVDA excluded)"
			: options.CaptureProcessName;
	}

	public static Task Start(HelperOptions options, AudioFrameQueue queue, int frameSamplesPerChannel, CancellationToken cancellationToken)
	{
		if (options.TestTone)
		{
			return ToneSource.RunAsync(queue, frameSamplesPerChannel, cancellationToken);
		}
		if (!string.IsNullOrWhiteSpace(options.CaptureDeviceId))
		{
			return new InputDeviceCapture(options.CaptureDeviceId, frameSamplesPerChannel).RunAsync(queue, cancellationToken);
		}
		if (!string.IsNullOrWhiteSpace(options.CaptureProcessName))
		{
			var pid = AudioDeviceCatalog.FindAudioAppPid(options.CaptureProcessName);
			return new ProcessLoopbackCapture(pid, includeTargetTree: true, frameSamplesPerChannel).RunAsync(queue, cancellationToken);
		}
		return new ProcessLoopbackCapture(options.ExcludePid, includeTargetTree: false, frameSamplesPerChannel).RunAsync(queue, cancellationToken);
	}
}

/// <summary>
/// Runs a RemSound-compatible connection: peer to peer, no audio server. It can send,
/// receive, or do both at once on the same socket, which is how a phone and a
/// computer talk to each other in both directions.
/// </summary>
internal static class RemSoundRunner
{
	private const int SampleRate = 48000;

	public static async Task RunAsync(HelperOptions options, CancellationToken cancellationToken)
	{
		var sends = options.Role is ConnectionRole.Publisher or ConnectionRole.Duplex;
		var receives = options.Role is ConnectionRole.Subscriber or ConnectionRole.Duplex;
		var roleText = options.Role switch
		{
			ConnectionRole.Publisher => "publisher",
			ConnectionRole.Duplex => "duplex",
			_ => "subscriber",
		};

		using var link = new RemSoundLink(new RemSoundLinkOptions
		{
			LocalPort = options.LocalPort,
			DiscoveryPort = options.DiscoveryPort,
			DeviceName = options.DeviceName,
			Peers = options.Peers,
			PeerNames = options.PeerNames,
			Password = options.Password,
			CanSend = sends,
			CanReceive = receives,
			AllowRemoteControl = options.AllowRemoteControl,
			Role = roleText,
		});

		PlaybackSink? playback = null;
		ReceivedAudioRecorder? recorder = null;
		RemSoundReceiver? receiver = null;
		IDisposable? playbackControls = null;
		try
		{
			if (receives)
			{
				playback = new PlaybackSink(
					SampleRate, 2,
					options.PrebufferMs,
					options.OutputLatencyMs,
					options.PlaybackBufferMs,
					options.OutputDeviceId,
					options.ReceiveVolume,
					options.ReceivePan,
					options.BassDb,
					options.MidDb,
					options.TrebleDb);
				recorder = new ReceivedAudioRecorder(options.RecordFolder, SampleRate, 2);
				receiver = new RemSoundReceiver(link, playback, recorder);
				link.AudioPacketReceived = receiver.OnPacket;
				playbackControls = LiveControls.RegisterPlayback(playback);
			}

			link.ControlReceived = (kind, delta, peer) => ApplyRemoteControl(kind, delta, peer, playback);
			using var controlCommands = LiveControls.Register((command, argument) =>
			{
				if (command != "control")
				{
					return false;
				}
				if (!LiveControls.TryParseControlKind(argument, out var kind))
				{
					return false;
				}
				var delta = kind switch
				{
					RemControlKind.VolumeUp => (sbyte)5,
					RemControlKind.VolumeDown => (sbyte)-5,
					_ => (sbyte)0,
				};
				var sentTo = link.SendControl(kind, delta);
				JsonLog.Write("control_sent", sentTo > 0
					? "Sent to the other device."
					: "No other device to send it to.", new Dictionary<string, object?>
				{
					["command"] = argument,
					["devices"] = sentTo,
				});
				return true;
			});

			await link.StartAsync(cancellationToken);
			if (receives)
			{
				JsonLog.Write("status", "Listening for remote audio.", new Dictionary<string, object?>
				{
					["transport"] = "remsound",
					["prebuffer_ms"] = options.PrebufferMs,
					["output_latency_ms"] = options.OutputLatencyMs,
					["recording"] = recorder!.IsRecording,
				});
			}

			if (!sends)
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
				return;
			}

			var frameSamples = SampleRate * options.OpusFrameMs / 1000;
			using var queue = new AudioFrameQueue(Math.Max(2, 40 / options.OpusFrameMs));
			using var sender = new RemSoundSender(link, options.Codec, options.OpusFrameMs, options.Bitrate, options.OpusFec);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			JsonLog.Write("status", "Starting capture.", new Dictionary<string, object?>
			{
				["capture_source"] = CaptureSources.Describe(options),
			});
			var captureTask = CaptureSources.Start(options, queue, frameSamples, linked.Token);
			var sendTask = sender.RunAsync(queue, linked.Token);
			var completed = await Task.WhenAny(captureTask, sendTask);
			try
			{
				await completed;
				// A capture source only ends on its own when something went wrong with it.
				if (!cancellationToken.IsCancellationRequested)
				{
					throw new InvalidOperationException("Audio capture stopped unexpectedly.");
				}
			}
			finally
			{
				linked.Cancel();
				queue.Complete();
				try
				{
					await Task.WhenAll(captureTask, sendTask).WaitAsync(TimeSpan.FromSeconds(2));
				}
				catch
				{
					// The first failure is the one worth reporting.
				}
			}
		}
		finally
		{
			playbackControls?.Dispose();
			link.AudioPacketReceived = null;
			link.ControlReceived = null;
			link.Dispose();
			receiver?.Dispose();
			recorder?.Dispose();
			playback?.Dispose();
		}
	}

	/// <summary>
	/// Carries out a remote volume command that has already been authenticated. App
	/// volume commands move this computer's receive volume; system commands move the
	/// Windows master volume by one native step, as RemSound does.
	/// </summary>
	private static void ApplyRemoteControl(RemControlKind kind, sbyte delta, string peer, PlaybackSink? playback)
	{
		switch (kind)
		{
			case RemControlKind.VolumeUp:
			case RemControlKind.VolumeDown:
			case RemControlKind.MuteToggle:
				if (playback is null)
				{
					JsonLog.Write("diagnostic", $"Ignored a receive-volume command from {peer}: this computer is not receiving audio.");
					return;
				}
				if (kind == RemControlKind.MuteToggle)
				{
					playback.Muted = !playback.Muted;
				}
				else
				{
					var step = Math.Abs((int)delta);
					if (step == 0)
					{
						step = 5;
					}
					playback.Volume += kind == RemControlKind.VolumeUp ? step : -step;
				}
				LiveControls.ReportVolume(playback, "remote", peer);
				return;
			default:
				var message = SystemVolume.Apply(kind);
				JsonLog.Write("volume", message, new Dictionary<string, object?>
				{
					["source"] = "remote",
					["system"] = true,
					["peer"] = peer,
				});
				return;
		}
	}
}
