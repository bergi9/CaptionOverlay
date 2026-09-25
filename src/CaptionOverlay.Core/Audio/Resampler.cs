using NAudio.Dsp;

namespace CaptionOverlay.Core.Audio;

/// <summary>
/// Streaming downmix + resample to 16 kHz mono float. Keeps filter state between calls so
/// chunk boundaries are seamless. Reconfigures itself if the input rate changes (device switch).
/// </summary>
public sealed class Resampler
{
    public const int TargetSampleRate = 16000;

    private readonly int _targetRate;
    private WdlResampler? _wdl;
    private int _inputRate;
    private float[] _mono = [];
    private float[] _out = [];

    public Resampler(int targetRate = TargetSampleRate)
    {
        _targetRate = targetRate;
    }

    /// <summary>Downmixes and resamples one chunk. Returns the produced samples (possibly empty).</summary>
    public float[] Process(AudioChunk chunk) => Process(chunk.Samples, chunk.SampleRate, chunk.Channels);

    public float[] Process(ReadOnlySpan<float> interleaved, int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        int frames = interleaved.Length / channels;
        if (frames == 0)
        {
            return [];
        }

        EnsureCapacity(ref _mono, frames);
        Downmix(interleaved, channels, _mono.AsSpan(0, frames));

        if (sampleRate == _targetRate)
        {
            _wdl = null;
            _inputRate = sampleRate;
            return _mono.AsSpan(0, frames).ToArray();
        }

        if (_wdl is null || _inputRate != sampleRate)
        {
            _wdl = CreateWdl(sampleRate, _targetRate);
            _inputRate = sampleRate;
        }

        // Input-driven (feed) mode: we hand over all input, the resampler tells us how much it wants.
        int maxOut = (int)Math.Ceiling(frames * (double)_targetRate / sampleRate) + 64;
        EnsureCapacity(ref _out, maxOut);

        int wanted = _wdl.ResamplePrepare(frames, 1, out Span<float> inBuffer);
        int toCopy = Math.Min(wanted, frames);
        _mono.AsSpan(0, toCopy).CopyTo(inBuffer);
        int produced = _wdl.ResampleOut(_out.AsSpan(0, maxOut), toCopy, maxOut, 1);
        return _out.AsSpan(0, produced).ToArray();
    }

    public void Reset()
    {
        _wdl = null;
        _inputRate = 0;
    }

    private static WdlResampler CreateWdl(int inputRate, int outputRate)
    {
        var wdl = new WdlResampler();
        // Windowed-sinc interpolation gives proper anti-aliasing when downsampling 48k -> 16k.
        wdl.SetMode(true, 0, true, 64, 32);
        wdl.SetFeedMode(true);
        wdl.SetRates(inputRate, outputRate);
        return wdl;
    }

    private static void Downmix(ReadOnlySpan<float> interleaved, int channels, Span<float> mono)
    {
        if (channels == 1)
        {
            interleaved[..mono.Length].CopyTo(mono);
            return;
        }

        float scale = 1f / channels;
        for (int f = 0; f < mono.Length; f++)
        {
            float sum = 0;
            int o = f * channels;
            for (int c = 0; c < channels; c++)
            {
                sum += interleaved[o + c];
            }
            mono[f] = sum * scale;
        }
    }

    private static void EnsureCapacity(ref float[] buffer, int size)
    {
        if (buffer.Length < size)
        {
            buffer = new float[Math.Max(size, buffer.Length * 2)];
        }
    }
}
