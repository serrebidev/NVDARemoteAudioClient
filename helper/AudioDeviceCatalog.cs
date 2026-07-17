using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace NVDARemoteAudioHelper;

internal sealed record AudioAppInfo(string ProcessName, string DisplayName, int Pid, bool Playing);
internal sealed record OutputDeviceInfo(string Id, string Name);

internal static class AudioDeviceCatalog
{
	public static void WriteAudioApps()
	{
		var apps = SnapshotAudioApps();
		JsonLog.Write("audio_apps", $"Found {apps.Count} audio applications.", new Dictionary<string, object?>
		{
			["apps"] = apps,
		});
	}

	public static void WriteOutputDevices()
	{
		var devices = SnapshotOutputDevices();
		JsonLog.Write("output_devices", $"Found {devices.Count} playback devices.", new Dictionary<string, object?>
		{
			["devices"] = devices,
		});
	}

	public static int FindAudioAppPid(string processName)
	{
		if (processName.Equals("nvda", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("NVDA cannot be selected as the application audio source because NVDA Remote already carries its speech.");
		}

		var matches = SnapshotAudioApps()
			.Where(app => app.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(app => app.Playing)
			.ToList();
		if (matches.Count == 0)
		{
			throw new InvalidOperationException($"The selected audio application '{processName}' is not running with an audio session. Start it or choose System audio in settings.");
		}

		// Prefer the oldest live process with this name. Multi-process applications
		// usually put their audio session on a renderer child; targeting the oldest
		// process and including its tree captures those renderer descendants too.
		var candidates = new List<(DateTime Started, int Pid)>();
		foreach (var process in Process.GetProcessesByName(processName))
		{
			try
			{
				candidates.Add((process.StartTime, process.Id));
			}
			catch
			{
				// Fall back to the audio-session PID if process metadata is inaccessible.
			}
			finally
			{
				process.Dispose();
			}
		}

		return candidates.Count > 0
			? candidates.OrderBy(candidate => candidate.Started).First().Pid
			: matches[0].Pid;
	}

	private static IReadOnlyList<AudioAppInfo> SnapshotAudioApps()
	{
		var byName = new Dictionary<string, AudioAppInfo>(StringComparer.OrdinalIgnoreCase);
		using var enumerator = new MMDeviceEnumerator();
		foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
		{
			try
			{
				var manager = device.AudioSessionManager;
				manager.RefreshSessions();
				var sessions = manager.Sessions;
				for (var index = 0; index < sessions.Count; index++)
				{
					var session = sessions[index];
					try
					{
						if (session.IsSystemSoundsSession)
						{
							continue;
						}
						var pid = checked((int)session.GetProcessID);
						if (pid <= 0)
						{
							continue;
						}

						var app = ResolveProcess(pid, session.State == AudioSessionState.AudioSessionStateActive);
						if (app is null ||
							app.ProcessName.Equals("nvda", StringComparison.OrdinalIgnoreCase) ||
							app.ProcessName.Equals("nvdaremoteaudiohelper", StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						if (!byName.TryGetValue(app.ProcessName, out var existing) || (app.Playing && !existing.Playing))
						{
							byName[app.ProcessName] = app;
						}
					}
					catch
					{
						// Audio sessions can disappear while being enumerated.
					}
				}
			}
			catch
			{
				// Skip endpoints that cannot expose an audio session manager.
			}
			finally
			{
				device.Dispose();
			}
		}

		return byName.Values
			.OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
			.ToList();
	}

	private static IReadOnlyList<OutputDeviceInfo> SnapshotOutputDevices()
	{
		var result = new List<OutputDeviceInfo>();
		using var enumerator = new MMDeviceEnumerator();
		foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
		{
			try
			{
				result.Add(new OutputDeviceInfo(device.ID, device.FriendlyName));
			}
			finally
			{
				device.Dispose();
			}
		}

		return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
	}

	private static AudioAppInfo? ResolveProcess(int pid, bool playing)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			var processName = process.ProcessName;
			if (string.IsNullOrWhiteSpace(processName))
			{
				return null;
			}

			var displayName = processName;
			try
			{
				var description = process.MainModule?.FileVersionInfo.FileDescription;
				if (!string.IsNullOrWhiteSpace(description))
				{
					displayName = description;
				}
			}
			catch
			{
				// Access to MainModule can be denied across process boundaries.
			}

			return new AudioAppInfo(processName.ToLowerInvariant(), displayName, pid, playing);
		}
		catch
		{
			return null;
		}
	}
}
