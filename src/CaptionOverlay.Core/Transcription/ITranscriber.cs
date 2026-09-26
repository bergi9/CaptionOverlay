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

/// <param name="UtteranceId">The segmenter utterance this pass belongs to (partials and the final share it); streaming transcribers use it to continue the same turn.</param>
/// <param name="ForcedCut">The final ends at a forced cut, so audio after it (possibly already streamed) belongs to the next utterance.</param>
/// <param name="StableSamples">Partial passes: how many leading samples can no longer move to the next utterance (see <see cref="Segmentation.PartialSnapshot.StableSamples"/>).</param>
public sealed record TranscriptionOptions(
    string? Language,
    string? Prompt,
    bool IsPartial,
    Guid? UtteranceId = null,
    bool ForcedCut = false,
    int? StableSamples = null);

/// <summary>A transcriber behind a network API; the connection test is shown in Settings and the wizard.</summary>
public interface IApiTranscriber : ITranscriber
{
    /// <summary>Checks key, endpoint and model; returns the round-trip time. Throws <see cref="TranscriptionException"/>.</summary>
    Task<TimeSpan> TestConnectionAsync(CancellationToken ct);
}

/// <summary>A transcriber that keeps per-utterance state across calls (audio already streamed to a server).</summary>
public interface IStreamingTranscriber : ITranscriber
{
    /// <summary>The utterance was discarded before its final pass (too short, paused): forget its state.</summary>
    void CancelUtterance(Guid utteranceId);
}

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
