using System.Text.Json;

namespace NVDARemoteAudioHelper;

internal static class JsonLog
{
	private static readonly object Lock = new();

	public static void Write(string eventName, string message, IDictionary<string, object?>? extra = null)
	{
		var payload = new Dictionary<string, object?>
		{
			["event"] = eventName,
			["message"] = message,
			["time"] = DateTimeOffset.UtcNow.ToString("O"),
		};

		if (extra is not null)
		{
			foreach (var pair in extra)
			{
				payload[pair.Key] = pair.Value;
			}
		}

		lock (Lock)
		{
			Console.WriteLine(JsonSerializer.Serialize(payload));
		}
	}
}
