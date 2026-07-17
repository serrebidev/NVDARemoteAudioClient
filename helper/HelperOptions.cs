namespace NVDARemoteAudioHelper;

internal sealed class HelperOptions
{
	public ConnectionRole Role { get; private init; } = ConnectionRole.Subscriber;
	public string Host { get; private init; } = "127.0.0.1";
	public int Port { get; private init; } = 6838;
	public string Key { get; private init; } = "";
	public int ExcludePid { get; private init; }
	public string CaptureProcessName { get; private init; } = "";
	public string OutputDeviceId { get; private init; } = "";
	public int ReceiveVolume { get; private init; } = 100;
	public int ReceivePan { get; private init; }
	public int BassDb { get; private init; }
	public int MidDb { get; private init; }
	public int TrebleDb { get; private init; }
	public string Password { get; private init; } = "";
	public AudioPayloadCodec Codec { get; private init; } = AudioPayloadCodec.Opus;
	public string RecordFolder { get; private init; } = "";
	public int Bitrate { get; private init; } = 96000;
	public int PrebufferMs { get; private init; } = 90;
	public int OutputLatencyMs { get; private init; } = 80;
	public int PlaybackBufferMs { get; private init; } = 450;
	public int OpusFrameMs { get; private init; } = 10;
	public bool OpusFec { get; private init; } = true;
	public bool TestTone { get; private init; }
	public bool ListAudioApps { get; private init; }
	public bool ListOutputDevices { get; private init; }
	public bool SelfTest { get; private init; }
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
		  --opus-frame-ms <ms>   Opus packet duration: 5, 10, or 20. Default: 10
		  --disable-fec          Disable Opus in-band forward error correction
		  --password-env <name> Read the end-to-end AES-GCM password from this environment variable
		  --password <password> Direct password value for manual testing; environment variables are safer
		  --codec opus|pcm       Audio transport codec. Default: opus
		  --prebuffer-ms <ms>    Subscriber startup jitter buffer. Default: 90
		  --output-latency-ms <ms>
		                        Subscriber output device latency. Default: 80
		  --buffer-ms <ms>       Subscriber maximum playback buffer. Default: 450
		  --list-audio-apps      List applications with active audio sessions as JSON
		  --list-output-devices  List active playback devices as JSON

		Publisher:
		  --exclude-pid <pid>    NVDA process ID to exclude from captured system audio
		  --include-process-name <name>
		                        Send only this application's audio (process name, no .exe)
		  --test-tone            Send a generated tone instead of capturing audio

		Subscriber:
		  --output-device-id <id>
		                        Playback endpoint ID. Empty uses the Windows default
		  --receive-volume <pct> Playback volume from 0 to 200. Default: 100
		  --receive-pan <value>  Stereo pan from -100 (left) to 100 (right)
		  --bass-db <db>         Low-shelf gain from -12 to 12 dB
		  --mid-db <db>          Mid peaking gain from -12 to 12 dB
		  --treble-db <db>       High-shelf gain from -12 to 12 dB
		  --record-folder <path> Record received audio to a timestamped WAV file

		Diagnostics:
		  --self-test            Run protocol and encryption self-tests, then exit
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
			if (optionName is "help" or "test-tone" or "disable-fec" or "list-audio-apps" or "list-output-devices" or "self-test")
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
		if (flags.Contains("list-audio-apps"))
		{
			return new HelperOptions { ListAudioApps = true };
		}
		if (flags.Contains("list-output-devices"))
		{
			return new HelperOptions { ListOutputDevices = true };
		}
		if (flags.Contains("self-test"))
		{
			return new HelperOptions { SelfTest = true };
		}

		var roleText = Required(values, "role");
		var role = roleText.Equals("publisher", StringComparison.OrdinalIgnoreCase)
			? ConnectionRole.Publisher
			: roleText.Equals("subscriber", StringComparison.OrdinalIgnoreCase)
				? ConnectionRole.Subscriber
				: throw new ArgumentException("--role must be publisher or subscriber.");

		var port = ParseInt(values, "port", 6838, 1, 65535);
		var bitrate = ParseInt(values, "bitrate", 96000, 16000, 510000);
		var prebufferMs = ParseInt(values, "prebuffer-ms", 90, 5, 1000);
		var outputLatencyMs = ParseInt(values, "output-latency-ms", 80, 5, 1000);
		var playbackBufferMs = ParseInt(values, "buffer-ms", 450, 40, 3000);
		var opusFrameMs = ParseOpusFrameMilliseconds(values);
		var excludePid = ParseInt(values, "exclude-pid", 0, 0, int.MaxValue);
		var captureProcessName = values.GetValueOrDefault("include-process-name", "").Trim();
		var outputDeviceId = values.GetValueOrDefault("output-device-id", "").Trim();
		var receiveVolume = ParseInt(values, "receive-volume", 100, 0, 200);
		var receivePan = ParseInt(values, "receive-pan", 0, -100, 100);
		var bassDb = ParseInt(values, "bass-db", 0, -12, 12);
		var midDb = ParseInt(values, "mid-db", 0, -12, 12);
		var trebleDb = ParseInt(values, "treble-db", 0, -12, 12);
		var passwordEnvironmentName = values.GetValueOrDefault("password-env", "").Trim();
		var password = passwordEnvironmentName.Length > 0
			? Environment.GetEnvironmentVariable(passwordEnvironmentName) ?? ""
			: values.GetValueOrDefault("password", "");
		if (passwordEnvironmentName.Length > 0 && string.IsNullOrEmpty(password))
		{
			throw new ArgumentException($"The password environment variable '{passwordEnvironmentName}' is empty or missing.");
		}
		if (password.Length > 512)
		{
			throw new ArgumentException("--password must be at most 512 characters.");
		}
		var codecText = values.GetValueOrDefault("codec", "opus");
		var codec = codecText.Equals("opus", StringComparison.OrdinalIgnoreCase)
			? AudioPayloadCodec.Opus
			: codecText.Equals("pcm", StringComparison.OrdinalIgnoreCase)
				? AudioPayloadCodec.Pcm16
				: throw new ArgumentException("--codec must be opus or pcm.");
		var recordFolder = values.GetValueOrDefault("record-folder", "").Trim();
		var testTone = flags.Contains("test-tone");
		var opusFec = !flags.Contains("disable-fec");

		if (captureProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			captureProcessName = captureProcessName[..^4];
		}
		if (captureProcessName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || captureProcessName.Contains(Path.DirectorySeparatorChar))
		{
			throw new ArgumentException("--include-process-name must be a process name without a path.");
		}

		if (role == ConnectionRole.Publisher && !testTone && excludePid <= 0 && string.IsNullOrWhiteSpace(captureProcessName))
		{
			throw new ArgumentException("Publisher mode requires --exclude-pid or --include-process-name unless --test-tone is used.");
		}

		var key = Required(values, "key");
		if (string.IsNullOrWhiteSpace(key))
		{
			throw new ArgumentException("--key cannot be empty.");
		}
		if (codec == AudioPayloadCodec.Pcm16 && opusFrameMs != 5)
		{
			throw new ArgumentException("PCM mode requires --opus-frame-ms 5 so packets stay within the relay MTU.");
		}

		return new HelperOptions
		{
			Role = role,
			Host = Required(values, "host"),
			Port = port,
			Key = key,
			ExcludePid = excludePid,
			CaptureProcessName = captureProcessName,
			OutputDeviceId = outputDeviceId,
			ReceiveVolume = receiveVolume,
			ReceivePan = receivePan,
			BassDb = bassDb,
			MidDb = midDb,
			TrebleDb = trebleDb,
			Password = password,
			Codec = codec,
			RecordFolder = recordFolder,
			Bitrate = bitrate,
			PrebufferMs = prebufferMs,
			OutputLatencyMs = outputLatencyMs,
			PlaybackBufferMs = playbackBufferMs,
			OpusFrameMs = opusFrameMs,
			OpusFec = opusFec,
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

	private static int ParseOpusFrameMilliseconds(Dictionary<string, string> values)
	{
		var value = ParseInt(values, "opus-frame-ms", 10, 5, 20);
		return value is 5 or 10 or 20
			? value
			: throw new ArgumentException("--opus-frame-ms must be 5, 10, or 20.");
	}
}
