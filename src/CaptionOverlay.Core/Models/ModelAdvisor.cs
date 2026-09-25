using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Diagnostics;
using CaptionOverlay.Core.Transcription;

namespace CaptionOverlay.Core.Models;

public sealed record ModelRecommendation(string? ModelId, bool SuggestApi, string Reason);

public enum BenchmarkRating
{
    GoodForLive,
    Usable,
    TooSlow,
}

public sealed record BenchmarkResult(double RealTimeFactor, TimeSpan InferenceTime, TimeSpan AudioDuration, string Runtime, string Text)
{
    public BenchmarkRating Rating => RealTimeFactor switch
    {
        < 0.3 => BenchmarkRating.GoodForLive,
        <= 0.7 => BenchmarkRating.Usable,
        _ => BenchmarkRating.TooSlow,
    };

    public string RatingText => Rating switch
    {
        BenchmarkRating.GoodForLive => "Good for live captions",
        BenchmarkRating.Usable => "Usable, some lag",
        _ => "Too slow: use a smaller model or API mode",
    };
}

public static class ModelAdvisor
{
    public const string BenchmarkFixture = "benchmark.wav";

    /// <summary>Suggests a model based on hardware. German users get the German fine-tune when a GPU is available.</summary>
    public static ModelRecommendation Recommend(HardwareSummary hw, string? language)
    {
        bool german = language == "de";
        var gpu = hw.BestGpu;
        if (gpu is { IsLikelyDiscrete: true } && gpu.DedicatedMemoryBytes >= 4L * 1024 * 1024 * 1024)
        {
            return new ModelRecommendation(
                german ? "large-v3-turbo-german-q5_0" : "large-v3-turbo-q5_0",
                false,
                $"Dedicated GPU with {gpu.DedicatedMemoryBytes / (1024 * 1024 * 1024)} GB VRAM ({gpu.Name}): the turbo model runs well in real time.");
        }
        if (hw.PhysicalCores >= 8)
        {
            return new ModelRecommendation("small-q5_1", false,
                $"No suitable GPU, but {hw.PhysicalCores} CPU cores: the small model should keep up.");
        }
        if (hw.PhysicalCores >= 4)
        {
            return new ModelRecommendation("base-q5_1", true,
                $"{hw.PhysicalCores} CPU cores and no suitable GPU: use the base model, or API mode for better quality.");
        }
        return new ModelRecommendation("tiny-q5_1", true,
            "Limited hardware: API mode is recommended; the tiny model works but quality is low.");
    }

    public static string DefaultBenchmarkPath => Path.Combine(AppContext.BaseDirectory, "Assets", BenchmarkFixture);

    /// <summary>Transcribes the bundled ~10 s fixture once (after a short warm-up) and reports the real-time factor.</summary>
    public static async Task<BenchmarkResult> BenchmarkAsync(ITranscriber transcriber, string? fixturePath = null, CancellationToken ct = default)
    {
        float[] samples = WavIO.ReadMono16k(fixturePath ?? DefaultBenchmarkPath);
        // Warm-up on 1 s: first inference includes one-off GPU pipeline/shader setup.
        await transcriber.TranscribeAsync(samples[..Math.Min(samples.Length, 16000)], new TranscriptionOptions("en", null, false), ct).ConfigureAwait(false);
        var result = await transcriber.TranscribeAsync(samples, new TranscriptionOptions("en", null, false), ct).ConfigureAwait(false);
        var audio = TimeSpan.FromSeconds(samples.Length / 16000.0);
        return new BenchmarkResult(result.InferenceTime / audio, result.InferenceTime, audio, transcriber.RuntimeDescription, result.Text);
    }
}
