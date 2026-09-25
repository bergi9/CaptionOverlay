namespace CaptionOverlay.Core.Segmentation;

public sealed record SegmenterOptions
{
    public float SpeechThreshold { get; init; } = 0.5f;
    public float SilenceThreshold { get; init; } = 0.35f;
    public int PreRollMs { get; init; } = 300;
    public int MinSpeechMs { get; init; } = 250;
    public int PartialIntervalMs { get; init; } = 500;
    public int EndSilenceMs { get; init; } = 600;
    public int PostRollMs { get; init; } = 200;
    public double MaxUtteranceSec { get; init; } = 12;
    public int MinUtteranceMs { get; init; } = 400;

    /// <summary>When a forced cut is needed, search the last N ms for the quietest window.</summary>
    public int CutSearchMs { get; init; } = 2000;
    public int CutWindowMs { get; init; } = 200;

    /// <summary>Emit partial snapshots at all (false = finals only).</summary>
    public bool EmitPartials { get; init; } = true;

    /// <summary>Fixed-window mode used when VAD is disabled: 5 s windows, no silence detection.</summary>
    public static SegmenterOptions FixedWindows(SegmenterOptions baseOptions) => baseOptions with
    {
        MaxUtteranceSec = 5,
        MinSpeechMs = 0,
        PreRollMs = 0,
        CutSearchMs = 1000,
    };
}
