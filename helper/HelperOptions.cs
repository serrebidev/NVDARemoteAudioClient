namespace NVDARemoteAudioHelper;

internal sealed class HelperOptions
{
	public ConnectionRole Role { get; private init; } = ConnectionRole.Subscriber;
	public string Host { get; private init; } = "127.0.0.1";
	public int Port { get; private init; } = 6838;
	public string Key { get; private init; } = "";
	public int ExcludePid { get; private init; }
	public int Bitrate { get; private init; } = 96000;
	public int PrebufferMs { get; private init; } = 90;
	public int OutputLatencyMs { get; private init; } = 80;
	public int PlaybackBufferMs { get; private init; } = 450;
	public bool TestTone { get; private init; }
	public bool ShowHelp { get; private init; }

	public const string Usage =
		"""
		NVDARemoteAudioHelper

		Required:
		  --role publisher|subscriber
		  --host <server host>
		  --key <NVDA Remote key>

		Common:
		  --port <port>          Default: 6838
		  --bitrate <bits/sec>   Publisher Opus bitrate. Default: 96000
		  --prebuffer-ms <ms>    Subscriber startup jitter buffer. Default: 90
		  --output-latency-ms <ms>
		                        Subscriber output device latency. Default: 80
		  --buffer-ms <ms>       Subscriber maximum playback buffer. Default: 450

		Publisher:
		  --exclude-pid <pid>    NVDA process ID to exclude from captured system audio
		  --test-tone            Send a generated tone instead of capturing audio
		""";

	public static HelperOptions Parse(string[] args)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			if (!arg.StartsWith("--", StringComparison.Ordinal))
			{
				throw new ArgumentException($"Unexpected argument: {arg}");
			}

			var optionName = arg[2..];
			if (optionName is "help" or "test-tone")
			{
				flags.Add(optionName);
				continue;
			}

			if (i + 1 >= args.Length)
			{
				throw new ArgumentException($"Missing value for {arg}.");
			}

			values[optionName] = args[++i];
		}

		if (flags.Contains("help"))
		{
			return new HelperOptions { ShowHelp = true };
		}

		var roleText = Required(values, "role");
		var role = roleText.Equals("publisher", StringComparison.OrdinalIgnoreCase)
			? ConnectionRole.Publisher
			: roleText.Equals("subscriber", StringComparison.OrdinalIgnoreCase)
				? ConnectionRole.Subscriber
				: throw new ArgumentException("--role must be publisher or subscriber.");

		var port = ParseInt(values, "port", 6838, 1, 65535);
		var bitrate = ParseInt(values, "bitrate", 96000, 16000, 510000);
		var prebufferMs = ParseInt(values, "prebuffer-ms", 90, 30, 1000);
		var outputLatencyMs = ParseInt(values, "output-latency-ms", 80, 30, 1000);
		var playbackBufferMs = ParseInt(values, "buffer-ms", 450, 100, 3000);
		var excludePid = ParseInt(values, "exclude-pid", 0, 0, int.MaxValue);
		var testTone = flags.Contains("test-tone");

		if (role == ConnectionRole.Publisher && !testTone && excludePid <= 0)
		{
			throw new ArgumentException("Publisher mode requires --exclude-pid unless --test-tone is used.");
		}

		var key = Required(values, "key");
		if (string.IsNullOrWhiteSpace(key))
		{
			throw new ArgumentException("--key cannot be empty.");
		}

		return new HelperOptions
		{
			Role = role,
			Host = Required(values, "host"),
			Port = port,
			Key = key,
			ExcludePid = excludePid,
			Bitrate = bitrate,
			PrebufferMs = prebufferMs,
			OutputLatencyMs = outputLatencyMs,
			PlaybackBufferMs = playbackBufferMs,
			TestTone = testTone,
		};
	}

	private static string Required(Dictionary<string, string> values, string key)
	{
		if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException($"Missing --{key}.");
		}

		return value;
	}

	private static int ParseInt(Dictionary<string, string> values, string key, int defaultValue, int min, int max)
	{
		if (!values.TryGetValue(key, out var raw))
		{
			return defaultValue;
		}

		if (!int.TryParse(raw, out var value) || value < min || value > max)
		{
			throw new ArgumentException($"--{key} must be between {min} and {max}.");
		}

		return value;
	}
}
