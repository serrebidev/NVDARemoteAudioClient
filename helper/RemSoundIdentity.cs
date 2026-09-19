namespace NVDARemoteAudioHelper;

/// <summary>
/// The identity this computer announces to other RemSound devices.
///
/// RemSound's own ports reroll this on every start, and the iPhone app documents the
/// consequence: it falls back to re-identifying a discovered peer by IP address,
/// because "the address is the only stable key". That loses the association the
/// moment the address changes — a laptop that is 192.168.1.64 on the LAN today and a
/// Tailscale address tomorrow looks like a different computer, and the user has to
/// find and tick it again on the phone.
///
/// Keeping one ID per installation for the life of the installation costs nothing and
/// gives every device on the network something stable to key on. An explicit value
/// still wins, so a caller can pin an identity when it has its own reason to.
/// </summary>
internal static class RemSoundIdentity
{
	private const string FolderName = "NVDARemoteAudioHelper";
	private const string FileName = "remSoundInstanceId";

	/// <summary>
	/// The identity to announce: the preferred value if it is usable, else the one
	/// remembered from a previous run, else a new one which is then remembered.
	/// </summary>
	public static string Resolve(string? preferred = null, string? folder = null)
	{
		if (Normalize(preferred) is { } chosen)
		{
			return chosen;
		}

		var path = Path.Combine(folder ?? DefaultFolder(), FileName);
		try
		{
			if (File.Exists(path) && Normalize(File.ReadAllText(path)) is { } stored)
			{
				return stored;
			}

			var generated = Guid.NewGuid().ToString("D");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, generated);
			return generated;
		}
		catch (Exception ex)
		{
			// A read-only profile, a full disk, or a locked-down machine must not stop
			// audio. A fresh identity per run is what every other RemSound port does.
			JsonLog.Write("diagnostic", $"Could not keep a RemSound device identity: {ex.Message}");
			return Guid.NewGuid().ToString("D");
		}
	}

	/// <summary>
	/// A usable instance ID as the canonical "D" form, or null for anything that must
	/// not be announced — a peer ignores an announcement it cannot parse anyway, but a
	/// self-generated blank or empty GUID would make this computer unfindable.
	/// </summary>
	internal static string? Normalize(string? text) =>
		Guid.TryParse((text ?? "").Trim(), out var id) && id != Guid.Empty ? id.ToString("D") : null;

	private static string DefaultFolder() =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
}
