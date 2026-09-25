using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Segmentation;
using CaptionOverlay.Core.Transcription;
using CaptionOverlay.Core.Vad;

namespace CaptionOverlay.Core.Pipeline;

public enum PipelineState
{
    Idle,
    LoadingModel,
    Listening,
    Transcribing,
    Paused,
    Error,
}

public sealed record PipelineStatus(PipelineState State, string? Message = null)
{
    public static readonly PipelineStatus Idle = new(PipelineState.Idle);

    public override string ToString() => Message is null ? State.ToString() : $"{State}: {Message}";
}

public sealed record PipelineMetrics(
    double? RealTimeFactor,
    double LagSeconds,
    string? Runtime,
    int DroppedPartials,
    TimeSpan SessionTime)
{
    public static readonly PipelineMetrics Empty = new(null, 0, null, 0, TimeSpan.Zero);

    /// <summary>Lag above which the UI suggests a smaller model or API mode.</summary>
    public const double LagWarningSeconds = 10;

    public bool IsLagging => LagSeconds > LagWarningSeconds;
}

public sealed record PipelineConfig
{
    public required Func<IAudioSource> AudioSourceFactory { get; init; }

    /// <summary>VAD factory; null disables VAD (fixed 5 s windows for music-heavy content).</summary>
    public Func<IVoiceActivityDetector>? VadFactory { get; init; }

    public SegmenterOptions Segmenter { get; init; } = new();

    /// <summary>ISO language code or null/"auto" for detection.</summary>
    public string? Language { get; init; }

    public bool EnablePartials { get; init; } = true;

    public SchedulerOptions Scheduler { get; init; } = new();

    /// <summary>Characters of committed text passed as prompt for continuity.</summary>
    public int PromptChars { get; init; } = 200;
}

/// <summary>Creates the transcriber(s) for a session; may take seconds (model load).</summary>
public delegate Task<TranscriberSet> TranscriberFactory(CancellationToken ct);
