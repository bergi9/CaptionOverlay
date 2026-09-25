namespace CaptionOverlay.Core.Segmentation;

public enum SegmenterState
{
    Idle,
    Speaking,
    Trailing,
}

/// <summary>
/// Turns a stream of (frame, speech probability) pairs into utterances.
/// Idle → Speaking after enough speech; Speaking ↔ Trailing on silence; Trailing → Idle after
/// enough silence (emits a final). Long utterances are force-cut at the quietest point.
/// Not thread-safe: owned by the single processing task.
/// </summary>
public sealed class UtteranceSegmenter
{
    private const int SampleRate = 16000;

    private readonly SegmenterOptions _options;
    private readonly Queue<(long Start, float[] Samples)> _history = new();
    private readonly List<float> _utterance = new(SampleRate * 16);

    private long _position;          // absolute sample index of the next frame
    private int _candidateSamples;   // speech seen while idle
    private long _candidateStart;
    private Guid _utteranceId;
    private long _utteranceStart;    // absolute index of _utterance[0]
    private long _firstSpeech;       // absolute index of first speech frame in the current utterance
    private long _lastSpeechEnd;     // absolute index right after the last speech frame
    private int _silenceSamples;
    private int _samplesSincePartial;
    private int _partialSequence;

    public UtteranceSegmenter(SegmenterOptions? options = null)
    {
        _options = options ?? new SegmenterOptions();
    }

    public SegmenterState State { get; private set; }

    public SegmenterOptions Options => _options;

    /// <summary>Current utterance, if one is in progress.</summary>
    public Guid? CurrentUtteranceId => State == SegmenterState.Idle ? null : _utteranceId;

    public TimeSpan Position => ToTime(_position);

    public IReadOnlyList<SegmenterEvent> Process(float[] frame, float speechProbability)
    {
        List<SegmenterEvent>? events = null;
        long frameStart = _position;
        long frameEnd = frameStart + frame.Length;
        _position = frameEnd;

        if (State == SegmenterState.Idle)
        {
            ProcessIdle(frame, speechProbability, frameStart, frameEnd);
            return (IReadOnlyList<SegmenterEvent>?)events ?? [];
        }

        _utterance.AddRange(frame);
        _samplesSincePartial += frame.Length;

        if (speechProbability >= _options.SpeechThreshold)
        {
            State = SegmenterState.Speaking;
            _lastSpeechEnd = frameEnd;
            _silenceSamples = 0;
        }
        else if (State == SegmenterState.Speaking)
        {
            if (speechProbability < _options.SilenceThreshold)
            {
                State = SegmenterState.Trailing;
                _silenceSamples = frame.Length;
            }
            else
            {
                // Between thresholds while speaking: hysteresis keeps us in Speaking.
                _lastSpeechEnd = frameEnd;
            }
        }
        else
        {
            _silenceSamples += frame.Length;
        }

        if (State == SegmenterState.Trailing && _silenceSamples >= Ms(_options.EndSilenceMs))
        {
            (events ??= []).Add(FinishUtterance());
            return events;
        }

        if (_utterance.Count >= (int)(_options.MaxUtteranceSec * SampleRate))
        {
            (events ??= []).Add(ForceCut());
            return events;
        }

        if (_options.EmitPartials && _samplesSincePartial >= Ms(_options.PartialIntervalMs))
        {
            _samplesSincePartial = 0;
            (events ??= []).Add(new PartialSnapshot(_utteranceId, _utterance.ToArray(), ToTime(_utteranceStart), ++_partialSequence));
        }

        return (IReadOnlyList<SegmenterEvent>?)events ?? [];
    }

    /// <summary>Ends any in-progress utterance (call on stop / end of input).</summary>
    public IReadOnlyList<SegmenterEvent> Flush()
    {
        if (State == SegmenterState.Idle)
        {
            return [];
        }
        return [FinishUtterance()];
    }

    public void Reset()
    {
        State = SegmenterState.Idle;
        _history.Clear();
        _utterance.Clear();
        _position = 0;
        _candidateSamples = 0;
        _silenceSamples = 0;
        _samplesSincePartial = 0;
    }

    private void ProcessIdle(float[] frame, float probability, long frameStart, long frameEnd)
    {
        _history.Enqueue((frameStart, frame));
        int keep = Ms(_options.PreRollMs) + Ms(_options.MinSpeechMs) + frame.Length * 2;
        while (_history.Count > 0 && frameEnd - _history.Peek().Start > keep)
        {
            _history.Dequeue();
        }

        if (probability >= _options.SpeechThreshold)
        {
            if (_candidateSamples == 0)
            {
                _candidateStart = frameStart;
            }
            _candidateSamples += frame.Length;
        }
        else if (probability < _options.SilenceThreshold)
        {
            _candidateSamples = 0;
        }

        if (_candidateSamples > 0 && _candidateSamples >= Ms(_options.MinSpeechMs))
        {
            BeginUtterance(_candidateStart - Ms(_options.PreRollMs), _candidateStart, frameEnd);
        }
    }

    private void BeginUtterance(long desiredStart, long firstSpeech, long lastSpeechEnd)
    {
        _utteranceId = Guid.NewGuid();
        _utterance.Clear();
        bool started = false;
        foreach (var (start, samples) in _history)
        {
            if (start + samples.Length <= desiredStart)
            {
                continue;
            }
            if (!started)
            {
                _utteranceStart = start;
                started = true;
            }
            _utterance.AddRange(samples);
        }
        _history.Clear();
        _firstSpeech = firstSpeech;
        _lastSpeechEnd = lastSpeechEnd;
        _candidateSamples = 0;
        _silenceSamples = 0;
        _samplesSincePartial = _utterance.Count;
        State = SegmenterState.Speaking;
    }

    private SegmenterEvent FinishUtterance()
    {
        var id = _utteranceId;
        long voiced = _lastSpeechEnd - _firstSpeech;
        long keepUntil = Math.Min(_lastSpeechEnd + Ms(_options.PostRollMs), _utteranceStart + _utterance.Count);
        int keepCount = (int)Math.Max(0, keepUntil - _utteranceStart);

        // Frames after the kept region become history, so they can serve as pre-roll for the next utterance.
        var tail = _utterance.Count > keepCount ? _utterance.GetRange(keepCount, _utterance.Count - keepCount).ToArray() : [];
        float[] samples = _utterance.GetRange(0, keepCount).ToArray();
        var start = ToTime(_utteranceStart);
        long tailStart = _utteranceStart + keepCount;

        _utterance.Clear();
        State = SegmenterState.Idle;
        _silenceSamples = 0;
        _candidateSamples = 0;
        _history.Clear();
        if (tail.Length > 0)
        {
            _history.Enqueue((tailStart, tail));
        }

        if (voiced < Ms(_options.MinUtteranceMs))
        {
            return new UtteranceDiscarded(id);
        }
        return new FinalUtterance(id, samples, start, ToTime(samples.Length));
    }

    private FinalUtterance ForceCut()
    {
        int cut = FindQuietCut();
        var id = _utteranceId;
        float[] samples = _utterance.GetRange(0, cut).ToArray();
        var start = ToTime(_utteranceStart);

        // The remainder starts a new utterance immediately: no audio is lost.
        _utterance.RemoveRange(0, cut);
        _utteranceStart += cut;
        _utteranceId = Guid.NewGuid();
        _firstSpeech = _utteranceStart;
        _lastSpeechEnd = Math.Max(_lastSpeechEnd, _utteranceStart);
        _samplesSincePartial = _utterance.Count;

        return new FinalUtterance(id, samples, start, ToTime(samples.Length));
    }

    /// <summary>Index into the current utterance at the centre of the lowest-energy window near the end.</summary>
    private int FindQuietCut()
    {
        int count = _utterance.Count;
        int window = Math.Max(1, Ms(_options.CutWindowMs));
        int searchStart = Math.Max(0, count - Ms(_options.CutSearchMs));
        const int step = 160; // 10 ms

        if (count - searchStart < window)
        {
            return count;
        }

        // Prefix sums of energy for O(1) window sums.
        var prefix = new double[count - searchStart + 1];
        for (int i = searchStart; i < count; i++)
        {
            float s = _utterance[i];
            prefix[i - searchStart + 1] = prefix[i - searchStart] + s * s;
        }

        double best = double.MaxValue;
        int bestStart = count - window;
        for (int w = 0; w + window <= count - searchStart; w += step)
        {
            double energy = prefix[w + window] - prefix[w];
            // Ties (e.g. digital silence) resolve to the latest window, keeping more audio in this utterance.
            if (energy <= best)
            {
                best = energy;
                bestStart = searchStart + w;
            }
        }
        return Math.Clamp(bestStart + window / 2, 1, count);
    }

    private static int Ms(int milliseconds) => milliseconds * SampleRate / 1000;

    // 10,000,000 ticks / 16,000 Hz = exactly 625 ticks per sample.
    private static TimeSpan ToTime(long samples) => TimeSpan.FromTicks(samples * (TimeSpan.TicksPerSecond / SampleRate));
}
