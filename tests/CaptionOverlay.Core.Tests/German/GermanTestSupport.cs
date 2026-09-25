using System.Globalization;
using System.Text.Json;
using CaptionOverlay.Cli.Fixtures;
using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Models;
using CaptionOverlay.Core.Transcription;

namespace CaptionOverlay.Core.Tests.German;

/// <summary>Locates <c>tests/fixtures/de</c> (copied to the test output) and builds the derived data once.</summary>
internal static class GermanFixtures
{
    private static readonly Lazy<GermanComposite> LazyComposite = new(() =>
    {
        EnsureValid();
        return GermanComposite.Build(Folder);
    });

    public static string Folder => Fixtures.Path("de");

    public static GermanComposite Composite => LazyComposite.Value;

    public static IReadOnlyList<GermanClip> Clips => Composite.Clips;

    public static void EnsureValid()
    {
        var problems = FixtureManifest.Verify(Folder);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "German fixtures are missing or damaged: " + string.Join("; ", problems) +
                ". Regenerate with: dotnet run --project src/CaptionOverlay.Cli -- fixtures fetch-de");
        }
    }

    /// <summary>The repository's <c>tests/fixtures/de</c> (not the build-output copy), or null outside a checkout.</summary>
    public static string? SourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CaptionOverlay.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "fixtures", "de");
            }
        }
        return null;
    }
}

/// <summary>A finite, non-live source that delivers in-memory audio in 20 ms chunks as fast as possible.</summary>
internal sealed class MemoryAudioSource(float[] interleaved, int sampleRate, int channels) : IAudioSource
{
    private CancellationTokenSource? _cts;
    private Task? _task;

    public NAudio.Wave.WaveFormat? SourceFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

    public bool IsLive => false;

    public event Action<AudioChunk>? SamplesAvailable;

    public event Action<string>? StatusChanged
    {
        add { }
        remove { }
    }

    public event Action? Completed;

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _task = Task.Run(() =>
        {
            int chunk = sampleRate / 50 * channels;
            for (int i = 0; i < interleaved.Length && !token.IsCancellationRequested; i += chunk)
            {
                int n = Math.Min(chunk, interleaved.Length - i);
                SamplesAvailable?.Invoke(new AudioChunk(interleaved.AsSpan(i, n).ToArray(), sampleRate, channels));
            }
            if (!token.IsCancellationRequested)
            {
                Completed?.Invoke();
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }
        await _cts.CancelAsync();
        if (_task is not null)
        {
            await _task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}

/// <summary>
/// Returns the reference text of the cues a job covers. The transcriber only gets samples, so the job is located
/// in the 16 kHz composite by exact sample match (the 16 kHz mono path through the pipeline is bit-exact).
/// </summary>
internal sealed class ReferenceTranscriber(GermanComposite composite, TimeSpan? delay = null) : ITranscriber
{
    private const int MatchLength = 256;

    public int Calls;

    public string DisplayName => "Reference";

    public string RuntimeDescription => "Reference";

    public bool SupportsPartials => true;

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, TranscriptionOptions opts, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (delay is { } d && d > TimeSpan.Zero)
        {
            await Task.Delay(d, ct);
        }
        double offset = Locate(samples16kMono);
        double end = offset + samples16kMono.Length / (double)GermanComposite.SampleRate;
        var texts = composite.Cues.Where(c => c.StartSec < end && c.EndSec > offset).Select(c => c.Text);
        return new TranscriptionResult(string.Join(' ', texts), "de", [], delay ?? TimeSpan.Zero);
    }

    /// <summary>Composite time (seconds) of the job's first sample.</summary>
    public double Locate(float[] job)
    {
        int first = Array.FindIndex(job, s => s != 0);
        if (first < 0 || first + MatchLength > job.Length)
        {
            throw new InvalidOperationException($"Job of {job.Length} samples has no {MatchLength} samples of audio to match against the composite.");
        }
        var needle = job.AsSpan(first, MatchLength);
        var hay = composite.Samples;
        for (int p = 0; p + MatchLength <= hay.Length; p++)
        {
            if (hay[p] == needle[0] && hay.AsSpan(p, MatchLength).SequenceEqual(needle))
            {
                return (p - first) / (double)GermanComposite.SampleRate;
            }
        }
        throw new InvalidOperationException("Job audio not found in the composite: the 16 kHz path is expected to be bit-exact.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Resolves catalog models for the German model tests and holds their WER thresholds.</summary>
internal static class GermanModels
{
    /// <summary>Aggregate WER thresholds (start values from the plan). Null = measure and log only.</summary>
    public static readonly IReadOnlyDictionary<string, double?> Thresholds = new Dictionary<string, double?>
    {
        ["large-v3-turbo-german-q5_0"] = 0.12,
        ["large-v3-turbo-q5_0"] = 0.20,
        ["small-q5_1"] = 0.40,
        ["base-q5_1"] = null,
        ["tiny-q5_1"] = null,
    };

    public static TheoryData<string> Ids => [.. Thresholds.Keys];

    /// <summary><c>CAPTIONOVERLAY_TEST_MODEL_&lt;ID&gt;</c> (path), then the installed model; tiny also honours <c>CAPTIONOVERLAY_TEST_MODEL</c>.</summary>
    public static ResolvedModel? Resolve(string id)
    {
        var catalog = ModelCatalog.LoadBundled();
        var entry = catalog.Find(id) ?? throw new InvalidOperationException($"{id} is not in the bundled catalog");
        string envName = "CAPTIONOVERLAY_TEST_MODEL_" + id.ToUpperInvariant().Replace('-', '_').Replace('.', '_');
        string? env = Environment.GetEnvironmentVariable(envName);
        if (env is null && id == "tiny-q5_1")
        {
            env = Environment.GetEnvironmentVariable("CAPTIONOVERLAY_TEST_MODEL");
        }
        if (env is not null && File.Exists(env))
        {
            return new ResolvedModel(id, entry.DisplayName, env, entry.ForceLanguage, false);
        }
        return new ModelStore().Resolve(id, catalog);
    }

    public static Task<LocalWhisperTranscriber> LoadAsync(ResolvedModel model, CancellationToken ct) =>
        LocalWhisperTranscriber.LoadAsync(
            new LocalWhisperOptions { ModelPath = model.Path, DisplayName = model.DisplayName, ForcedLanguage = model.ForceLanguage },
            GpuPreference.Auto, ct: ct);
}

internal sealed record ClipResult(string Id, string Reference, string Hypothesis, int Words, int Errors, double InferenceSec, double AudioSec)
{
    public double Wer => Words == 0 ? 0 : Errors / (double)Words;
}

internal sealed record ModelRun(string ModelId, string Runtime, IReadOnlyList<ClipResult> Clips)
{
    public double AggregateWer => Clips.Sum(c => c.Errors) / (double)Clips.Sum(c => c.Words);

    public double RealTimeFactor => Clips.Sum(c => c.InferenceSec) / Clips.Sum(c => c.AudioSec);
}

internal sealed record WerBaselineEntry(
    string MeasuredAt,
    string Runtime,
    double AggregateWer,
    double RealTimeFactor,
    IReadOnlyDictionary<string, double> ClipWer,
    double? EndToEndWer);

/// <summary>
/// <c>tests/fixtures/de/wer-baseline.json</c>: measured WER per model. Tests read it; with
/// <c>CAPTIONOVERLAY_UPDATE_WER_BASELINE=1</c> they write fresh measurements back to the repository copy.
/// </summary>
internal static class WerBaseline
{
    private static readonly Lock Gate = new();

    public static bool UpdateRequested => Environment.GetEnvironmentVariable("CAPTIONOVERLAY_UPDATE_WER_BASELINE") == "1";

    private static string PathToRead => GermanFixtures.SourceFolder() is { } src && File.Exists(Path.Combine(src, "wer-baseline.json"))
        ? Path.Combine(src, "wer-baseline.json")
        : Path.Combine(GermanFixtures.Folder, "wer-baseline.json");

    public static WerBaselineEntry? Get(string modelId)
    {
        lock (Gate)
        {
            return Load(PathToRead).GetValueOrDefault(modelId);
        }
    }

    public static void Update(string modelId, Func<WerBaselineEntry?, WerBaselineEntry> update)
    {
        if (!UpdateRequested || GermanFixtures.SourceFolder() is not { } src)
        {
            return;
        }
        lock (Gate)
        {
            string path = Path.Combine(src, "wer-baseline.json");
            var all = Load(path);
            all[modelId] = update(all.GetValueOrDefault(modelId));
            var sorted = new SortedDictionary<string, WerBaselineEntry>(all, StringComparer.Ordinal);
            File.WriteAllText(path, JsonSerializer.Serialize(sorted, GermanComposite.Json).ReplaceLineEndings("\n") + "\n");
        }
    }

    public static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Dictionary<string, WerBaselineEntry> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, WerBaselineEntry>>(File.ReadAllText(path), GermanComposite.Json) ?? []
            : [];
}
