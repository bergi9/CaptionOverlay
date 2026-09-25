namespace CaptionOverlay.Core.Vad;

public interface IVoiceActivityDetector : IDisposable
{
    /// <summary>Number of 16 kHz samples per frame this detector expects.</summary>
    int FrameSize { get; }

    /// <summary>Returns the speech probability (0..1) for one frame. Carries state between frames.</summary>
    float Process(ReadOnlySpan<float> frame);

    void Reset();
}

/// <summary>Treats everything as speech. Used when VAD is disabled (fixed-window mode for music-heavy content).</summary>
public sealed class AlwaysSpeechVad : IVoiceActivityDetector
{
    public AlwaysSpeechVad(int frameSize = SileroVad.FrameSamples)
    {
        FrameSize = frameSize;
    }

    public int FrameSize { get; }

    public float Process(ReadOnlySpan<float> frame) => 1f;

    public void Reset()
    {
    }

    public void Dispose()
    {
    }
}
