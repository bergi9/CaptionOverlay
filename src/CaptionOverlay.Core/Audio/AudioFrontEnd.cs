namespace CaptionOverlay.Core.Audio;

/// <summary>
/// Turns source chunks into fixed-size 16 kHz mono frames on a continuous session timeline.
/// For live sources it fills gaps with synthetic silence, because WASAPI loopback delivers no
/// callbacks while nothing is playing and the segmenter still needs time to pass to end utterances.
/// Not thread-safe: owned by the single processing task.
/// </summary>
public sealed class AudioFrontEnd
{
    private readonly Resampler _resampler = new();
    private readonly int _frameSize;
    private readonly float[] _pending;
    private readonly TimeSpan _gapTolerance;
    private int _pendingCount;
    private long _samplesEmitted;
    private TimeSpan _clockOrigin;
    private bool _clockStarted;

    public AudioFrontEnd(int frameSize = 512, TimeSpan? gapTolerance = null)
    {
        _frameSize = frameSize;
        _pending = new float[frameSize];
        _gapTolerance = gapTolerance ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>Samples emitted so far (session timeline, 16 kHz).</summary>
    public long SamplesEmitted => _samplesEmitted;

    public TimeSpan Position => TimeSpan.FromSeconds((double)_samplesEmitted / Resampler.TargetSampleRate);

    /// <summary>Starts the wall-clock reference used for synthetic silence.</summary>
    public void StartClock(TimeSpan now)
    {
        _clockOrigin = now;
        _clockStarted = true;
    }

    /// <summary>Resamples a chunk and emits complete frames.</summary>
    public void Push(AudioChunk chunk, Action<float[]> onFrame)
    {
        float[] mono = _resampler.Process(chunk);
        Append(mono, onFrame);
    }

    /// <summary>
    /// Called periodically when no audio arrived. If the timeline lags the wall clock by more
    /// than the tolerance, inserts silence to catch up. Returns the number of samples injected.
    /// </summary>
    public int InjectSilenceIfIdle(TimeSpan now, Action<float[]> onFrame)
    {
        if (!_clockStarted)
        {
            return 0;
        }

        long expected = (long)((now - _clockOrigin).TotalSeconds * Resampler.TargetSampleRate);
        long tolerance = (long)(_gapTolerance.TotalSeconds * Resampler.TargetSampleRate);
        long current = _samplesEmitted + _pendingCount;
        long missing = expected - tolerance - current;
        if (missing <= 0)
        {
            return 0;
        }

        int toInject = (int)Math.Min(missing, int.MaxValue);
        Append(new float[toInject], onFrame);
        return toInject;
    }

    /// <summary>Pads and emits any partial frame (on stop).</summary>
    public void Flush(Action<float[]> onFrame)
    {
        if (_pendingCount == 0)
        {
            return;
        }
        Array.Clear(_pending, _pendingCount, _frameSize - _pendingCount);
        _pendingCount = _frameSize;
        EmitPending(onFrame);
    }

    public void Reset()
    {
        _resampler.Reset();
        _pendingCount = 0;
        _samplesEmitted = 0;
        _clockStarted = false;
    }

    private void Append(ReadOnlySpan<float> samples, Action<float[]> onFrame)
    {
        while (!samples.IsEmpty)
        {
            int n = Math.Min(_frameSize - _pendingCount, samples.Length);
            samples[..n].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += n;
            samples = samples[n..];
            if (_pendingCount == _frameSize)
            {
                EmitPending(onFrame);
            }
        }
    }

    private void EmitPending(Action<float[]> onFrame)
    {
        var frame = new float[_frameSize];
        _pending.AsSpan(0, _frameSize).CopyTo(frame);
        _pendingCount = 0;
        _samplesEmitted += _frameSize;
        onFrame(frame);
    }
}
