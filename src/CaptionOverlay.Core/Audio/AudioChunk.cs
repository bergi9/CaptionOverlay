namespace CaptionOverlay.Core.Audio;

/// <summary>
/// A block of interleaved float samples as delivered by an <see cref="IAudioSource"/>.
/// The format travels with the samples because it can change mid-session (device switch).
/// </summary>
public sealed record AudioChunk(float[] Samples, int SampleRate, int Channels)
{
    public int FrameCount => Samples.Length / Channels;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / SampleRate);
}
