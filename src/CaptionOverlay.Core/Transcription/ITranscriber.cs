namespace CaptionOverlay.Core.Transcription;

public interface ITranscriber : IAsyncDisposable
{
    string DisplayName { get; }

    /// <summary>Short description of the backend in use, e.g. "GPU (Vulkan)", "CPU", "API (Groq)".</summary>
    string RuntimeDescription { get; }

    /// <summary>Whether partial (tentative) passes should be run with this transcriber.</summary>
    bool SupportsPartials { get; }

    Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, TranscriptionOptions opts, CancellationToken ct);
}

public sealed record TranscriptionOptions(
    string? Language,
    string? Prompt,
    bool IsPartial);

public sealed record TranscriptionResult(
    string Text,
    string? DetectedLanguage,
    IReadOnlyList<TranscribedSegment> Segments,
    TimeSpan InferenceTime)
{
    /// <summary>Average segment probability, if the backend reports one.</summary>
    public float? AverageProbability
    {
        get
        {
            var probs = Segments.Where(s => s.Probability.HasValue).Select(s => s.Probability!.Value).ToList();
            return probs.Count == 0 ? null : probs.Average();
        }
    }
}

public sealed record TranscribedSegment(string Text, TimeSpan Start, TimeSpan End, float? Probability);

/// <summary>Base for errors a transcriber reports in a user-presentable way.</summary>
public class TranscriptionException : Exception
{
    public TranscriptionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    /// <summary>If true, the pipeline should stop rather than keep retrying (e.g. rejected API key).</summary>
    public bool IsFatal { get; init; }

    /// <summary>Temporary problem (network, 5xx, rate limit): the job can be retried later.</summary>
    public bool IsTransient { get; init; }

    /// <summary>Server-requested back-off (HTTP 429), if any.</summary>
    public TimeSpan? RetryAfter { get; init; }
}
