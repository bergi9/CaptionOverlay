using NAudio.Wave;

namespace CaptionOverlay.Core.Audio;

/// <summary>
/// Plays a WAV file into the pipeline, either in real time (to simulate loopback) or as fast as
/// possible (tests, offline segmentation).
/// </summary>
public sealed class WavFileAudioSource : IAudioSource
{
    private readonly string _path;
    private readonly bool _realtime;
    private readonly TimeSpan _chunk;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public WavFileAudioSource(string path, bool realtime, TimeSpan? chunk = null)
    {
        _path = path;
        _realtime = realtime;
        _chunk = chunk ?? TimeSpan.FromMilliseconds(20);
        using var reader = new WaveFileReader(path);
        SourceFormat = reader.WaveFormat;
    }

    public WaveFormat? SourceFormat { get; }

    public bool IsLive => false;

    public event Action<AudioChunk>? SamplesAvailable;

    public event Action<string>? StatusChanged;

    public event Action? Completed;

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _task = Task.Run(() => PumpAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_task is not null)
        {
            try
            {
                await _task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cts.Dispose();
        _cts = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task PumpAsync(CancellationToken ct)
    {
        using var reader = new WaveFileReader(_path);
        var format = reader.WaveFormat;
        StatusChanged?.Invoke($"Playing file: {Path.GetFileName(_path)}");
        int blockBytes = (int)(format.AverageBytesPerSecond * _chunk.TotalSeconds);
        blockBytes -= blockBytes % format.BlockAlign;
        var buffer = new byte[blockBytes];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long framesSent = 0;
        int read;
        while (!ct.IsCancellationRequested && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            float[] samples = SampleConverter.ToFloat(buffer.AsSpan(0, read), format);
            SamplesAvailable?.Invoke(new AudioChunk(samples, format.SampleRate, format.Channels));
            framesSent += samples.Length / format.Channels;
            if (_realtime)
            {
                var due = TimeSpan.FromSeconds((double)framesSent / format.SampleRate);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }
        }
        if (!ct.IsCancellationRequested)
        {
            Completed?.Invoke();
        }
    }
}
