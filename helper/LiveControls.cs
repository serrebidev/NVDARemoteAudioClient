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

/// <summary>
/// What a control command that arrived from a peer is allowed to do on this computer.
/// </summary>
internal static class RemControlPolicy
{
	public static bool IsSystemVolume(RemControlKind kind) =>
		kind is RemControlKind.SystemVolumeUp or RemControlKind.SystemVolumeDown or RemControlKind.SystemMuteToggle;

	/// <summary>
	/// Whether an authenticated command from a peer may be acted on.
	///
	/// A peer may change the volume of the audio it is sending here, and only when the
	/// user has allowed remote control at all. It may never change this computer's
	/// Windows volume, whether or not that switch is on: a speaker volume that moves
	/// on its own, at a moment the user did not ask for and from a device they are not
	/// looking at, is alarming rather than useful, and by ear there is nothing to tell
	/// the sender apart from a fault. Sending that command outward is still offered,
	/// because that is the user deliberately acting on the other device.
	/// </summary>
	public static bool AllowsInbound(RemControlKind kind, bool allowRemoteControl) =>
		allowRemoteControl && !IsSystemVolume(kind);
}
