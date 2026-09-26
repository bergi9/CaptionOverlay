namespace CaptionOverlay.Core.Segmentation;

public abstract record SegmenterEvent(Guid UtteranceId);

/// <summary>Snapshot of the whole in-progress utterance, for a tentative transcription.</summary>
public sealed record PartialSnapshot(Guid UtteranceId, float[] Samples, TimeSpan StartOffset, int Sequence)
    : SegmenterEvent(UtteranceId);

/// <summary>A finished utterance. Never dropped downstream.</summary>
public sealed record FinalUtterance(Guid UtteranceId, float[] Samples, TimeSpan StartOffset, TimeSpan Duration)
    : SegmenterEvent(UtteranceId)
{
    public TimeSpan End => StartOffset + Duration;

    /// <summary>Ended by the length limit: the audio right after <see cref="End"/> starts the next utterance (vs. trailing silence).</summary>
    public bool IsForcedCut { get; init; }
}

/// <summary>An utterance that started but turned out too short (click, breath). Clears any tentative text.</summary>
public sealed record UtteranceDiscarded(Guid UtteranceId) : SegmenterEvent(UtteranceId);
