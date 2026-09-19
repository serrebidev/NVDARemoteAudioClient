using NAudio.CoreAudioApi;

namespace NVDARemoteAudioHelper;

/// <summary>
/// Commands the add-on sends on the helper's standard input while it runs, so a
/// volume change or a remote-control press takes effect at once instead of costing a
/// reconnect. A command is one line starting with '!'. Anything else, or the pipe
/// closing, still means "shut down", which keeps the old contract that any byte
/// stops the helper.
///
///   !volume 120          set the receive volume, 0 to 200 percent
///   !volume-step -5      nudge it
///   !mute on|off|toggle  mute or unmute received audio
///   !control volume-up   ask the other device to change its volume (RemSound only)
/// </summary>
internal static class LiveControls
{
	private static readonly object Gate = new();
	private static readonly List<Func<string, string, bool>> Handlers = [];

	public const char CommandPrefix = '!';

	public static IDisposable Register(Func<string, string, bool> handler)
	{
		lock (Gate)
		{
			Handlers.Add(handler);
		}
		return new Registration(handler);
	}

	/// <summary>Runs one command line. Returns false when nothing understood it.</summary>
	public static bool Dispatch(string line)
	{
		line = line.Trim();
		if (line.Length > 0 && line[0] == CommandPrefix)
		{
			line = line[1..];
		}
		var space = line.IndexOf(' ');
		var command = (space < 0 ? line : line[..space]).Trim().ToLowerInvariant();
		var argument = space < 0 ? "" : line[(space + 1)..].Trim();
		if (command.Length == 0)
		{
			return false;
		}
		List<Func<string, string, bool>> handlers;
		lock (Gate)
		{
			handlers = [.. Handlers];
		}
		var handled = false;
		foreach (var handler in handlers)
		{
			try
			{
				handled |= handler(command, argument);
			}
			catch (Exception ex)
			{
				JsonLog.Write("diagnostic", $"Live command '{command}' failed: {ex.Message}");
			}
		}
		if (!handled)
		{
			JsonLog.Write("diagnostic", $"Live command '{command}' is not available in this connection.");
		}
		return handled;
	}

	/// <summary>Parses "on", "off" or "toggle" against the current state.</summary>
	public static bool? ParseSwitch(string argument, bool current) => argument.ToLowerInvariant() switch
	{
		"on" or "true" or "1" => true,
		"off" or "false" or "0" => false,
		"" or "toggle" => !current,
		_ => null,
	};

	public static bool TryParseControlKind(string text, out RemControlKind kind)
	{
		switch (text.Trim().ToLowerInvariant())
		{
			case "volume-up":
				kind = RemControlKind.VolumeUp;
				return true;
			case "volume-down":
				kind = RemControlKind.VolumeDown;
				return true;
			case "mute":
				kind = RemControlKind.MuteToggle;
				return true;
			case "system-volume-up":
				kind = RemControlKind.SystemVolumeUp;
				return true;
			case "system-volume-down":
				kind = RemControlKind.SystemVolumeDown;
				return true;
			case "system-mute":
				kind = RemControlKind.SystemMuteToggle;
				return true;
			default:
				kind = RemControlKind.VolumeUp;
				return false;
		}
	}

	/// <summary>
	/// Registers the receive-volume commands against a playback sink and reports every
	/// change, so the add-on can speak it and remember it.
	/// </summary>
	public static IDisposable RegisterPlayback(PlaybackSink playback)
	{
		return Register((command, argument) =>
		{
			switch (command)
			{
				case "volume":
					if (!int.TryParse(argument, out var volume))
					{
						return false;
					}
					playback.Volume = volume;
					ReportVolume(playback, "local");
					return true;
				case "volume-step":
					if (!int.TryParse(argument, out var step))
					{
						return false;
					}
					playback.Volume += step;
					ReportVolume(playback, "local");
					return true;
				case "mute":
					var muted = ParseSwitch(argument, playback.Muted);
					if (muted is null)
					{
						return false;
					}
					playback.Muted = muted.Value;
					ReportVolume(playback, "local");
					return true;
				default:
					return false;
			}
		});
	}

	public static void ReportVolume(PlaybackSink playback, string source, string? peer = null)
	{
		var message = playback.Muted
			? $"Received audio muted, volume {playback.Volume} percent."
			: $"Receive volume {playback.Volume} percent.";
		JsonLog.Write("volume", message, new Dictionary<string, object?>
		{
			["volume"] = playback.Volume,
			["muted"] = playback.Muted,
			["source"] = source,
			["peer"] = peer,
		});
	}

	private sealed class Registration(Func<string, string, bool> handler) : IDisposable
	{
		public void Dispose()
		{
			lock (Gate)
			{
				Handlers.Remove(handler);
			}
		}
	}
}

/// <summary>The Windows master volume, for RemSound's "system volume" remote commands.</summary>
internal static class SystemVolume
{
	public static string Apply(RemControlKind kind)
	{
		using var enumerator = new MMDeviceEnumerator();
		using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
		var volume = device.AudioEndpointVolume;
		switch (kind)
		{
			case RemControlKind.SystemVolumeUp:
				// One native step, exactly what a keyboard volume key does.
				volume.VolumeStepUp();
				break;
			case RemControlKind.SystemVolumeDown:
				volume.VolumeStepDown();
				break;
			case RemControlKind.SystemMuteToggle:
				volume.Mute = !volume.Mute;
				break;
		}
		return volume.Mute
			? "Windows volume muted."
			: $"Windows volume {(int)Math.Round(volume.MasterVolumeLevelScalar * 100)} percent.";
	}
}
