using System.Text.Json;
using CaptionOverlay.Core.Transcription;

namespace CaptionOverlay.Core.Tests;

internal static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static string SileroModel => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");

    public static IReadOnlyList<Label> Labels(string wavName)
    {
        string json = File.ReadAllText(Path(System.IO.Path.ChangeExtension(wavName, ".labels.json")));
        return JsonSerializer.Deserialize<List<Label>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public sealed record Label(int StartMs, int EndMs, string Text);
}

internal static class Signals
{
    public static float[] Sine(double frequency, int sampleRate, double seconds, int channels = 1, float amplitude = 0.5f)
    {
        int frames = (int)(sampleRate * seconds);
        var data = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            float v = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
            for (int c = 0; c < channels; c++)
            {
                data[i * channels + c] = v;
            }
        }
        return data;
    }
}

/// <summary>Deterministic transcriber with configurable delay and failures, for scheduler/pipeline tests.</summary>
internal sealed class FakeTranscriber : ITranscriber
{
    private readonly Func<float[], TranscriptionOptions, string> _text;
    private int _calls;

    public FakeTranscriber(TimeSpan? delay = null, Func<float[], TranscriptionOptions, string>? text = null, bool partials = true)
    {
        Delay = delay ?? TimeSpan.Zero;
        _text = text ?? ((s, o) => $"{(o.IsPartial ? "partial" : "final")} {s.Length / 16000.0:F1}s");
        SupportsPartials = partials;
    }

    public TimeSpan Delay { get; set; }

    public Func<int, Exception?>? FailOnCall { get; set; }

    public List<(int Samples, TranscriptionOptions Options)> Calls { get; } = [];

    public bool Disposed { get; private set; }

    public string DisplayName => "Fake";

    public string RuntimeDescription => "Fake";

    public bool SupportsPartials { get; }

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, TranscriptionOptions opts, CancellationToken ct)
    {
        int call = Interlocked.Increment(ref _calls);
        lock (Calls)
        {
            Calls.Add((samples16kMono.Length, opts));
        }
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }
        if (FailOnCall?.Invoke(call) is { } ex)
        {
            throw ex;
        }
        return new TranscriptionResult(_text(samples16kMono, opts), "en", [], Delay);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
