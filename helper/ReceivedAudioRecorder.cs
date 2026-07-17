using NAudio.Wave;

namespace NVDARemoteAudioHelper;

internal sealed class ReceivedAudioRecorder : IDisposable
{
	private readonly WaveFileWriter? _writer;

	public ReceivedAudioRecorder(string folder, int sampleRate, int channels)
	{
		if (string.IsNullOrWhiteSpace(folder))
		{
			return;
		}

		Directory.CreateDirectory(folder);
		var path = Path.Combine(folder, $"NVDA-Remote-Audio-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
		_writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
		JsonLog.Write("recording", "Recording received audio.", new Dictionary<string, object?>
		{
			["path"] = path,
		});
	}

	public bool IsRecording => _writer is not null;

	public void Write(float[] samples, int count)
	{
		_writer?.WriteSamples(samples, 0, count);
	}

	public void Dispose()
	{
		if (_writer is null)
		{
			return;
		}

		_writer.Dispose();
		JsonLog.Write("recording", "Received-audio recording saved.");
	}
}
