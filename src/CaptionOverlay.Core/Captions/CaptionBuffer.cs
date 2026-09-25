namespace CaptionOverlay.Core.Captions;

public sealed record CaptionLine(string Text, TimeSpan Start, TimeSpan End, Guid UtteranceId);

public sealed record TentativeCaption(Guid UtteranceId, string Text, int Sequence);

public enum CaptionChangeKind
{
    Committed,
    Tentative,
    Cleared,
}

public sealed class CaptionChangedEventArgs(CaptionChangeKind kind, CaptionLine? committedLine) : EventArgs
{
    public CaptionChangeKind Kind { get; } = kind;

    public CaptionLine? CommittedLine { get; } = committedLine;
}

public sealed record CaptionSnapshot(IReadOnlyList<CaptionLine> Lines, TentativeCaption? Tentative);

/// <summary>
/// Committed caption lines plus one tentative line for the in-progress utterance.
/// Thread-safe; raises exactly one <see cref="Changed"/> event per effective update
/// (on the caller's thread, outside the lock).
/// </summary>
public sealed class CaptionBuffer
{
    private readonly Lock _gate = new();
    private readonly List<CaptionLine> _committed = [];
    private readonly HashSet<Guid> _closedUtterances = [];
    private TentativeCaption? _tentative;
    private Guid? _activeUtterance;
    private int _lastAppliedSequence;
    private int _displayStart;

    public event EventHandler<CaptionChangedEventArgs>? Changed;

    public TentativeCaption? Tentative
    {
        get
        {
            lock (_gate)
            {
                return _tentative;
            }
        }
    }

    public IReadOnlyList<CaptionLine> Committed
    {
        get
        {
            lock (_gate)
            {
                return _committed.ToArray();
            }
        }
    }

    /// <summary>Marks which utterance is currently being spoken; partials for other utterances are ignored.</summary>
    public void SetActiveUtterance(Guid utteranceId)
    {
        lock (_gate)
        {
            if (!_closedUtterances.Contains(utteranceId))
            {
                _activeUtterance = utteranceId;
            }
        }
    }

    /// <summary>Applies a partial result if it is for the active utterance and newer than the last applied one.</summary>
    public bool ApplyPartial(Guid utteranceId, int sequence, string text)
    {
        lock (_gate)
        {
            if (_activeUtterance != utteranceId || _closedUtterances.Contains(utteranceId) || sequence <= _lastAppliedSequence)
            {
                return false;
            }
            _lastAppliedSequence = sequence;
            _tentative = string.IsNullOrWhiteSpace(text) ? null : new TentativeCaption(utteranceId, text.Trim(), sequence);
        }
        Raise(CaptionChangeKind.Tentative, null);
        return true;
    }

    /// <summary>Commits a final result. Empty text just closes the utterance (and clears its tentative line).</summary>
    public CaptionLine? CommitFinal(Guid utteranceId, string? text, TimeSpan start, TimeSpan end)
    {
        CaptionLine? line = null;
        bool changed;
        lock (_gate)
        {
            CloseLocked(utteranceId);
            changed = _tentative?.UtteranceId == utteranceId;
            if (changed)
            {
                _tentative = null;
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                line = new CaptionLine(text.Trim(), start, end, utteranceId);
                _committed.Add(line);
                changed = true;
            }
        }
        if (changed)
        {
            Raise(line is null ? CaptionChangeKind.Tentative : CaptionChangeKind.Committed, line);
        }
        return line;
    }

    /// <summary>Drops an utterance that turned out to be noise.</summary>
    public void Discard(Guid utteranceId)
    {
        bool changed;
        lock (_gate)
        {
            CloseLocked(utteranceId);
            changed = _tentative?.UtteranceId == utteranceId;
            if (changed)
            {
                _tentative = null;
            }
        }
        if (changed)
        {
            Raise(CaptionChangeKind.Tentative, null);
        }
    }

    /// <summary>Clears what the overlay shows. The full transcript is kept for export/copy.</summary>
    public void ClearDisplay()
    {
        lock (_gate)
        {
            _displayStart = _committed.Count;
            _tentative = null;
        }
        Raise(CaptionChangeKind.Cleared, null);
    }

    /// <summary>Clears everything (new session).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _committed.Clear();
            _closedUtterances.Clear();
            _tentative = null;
            _activeUtterance = null;
            _lastAppliedSequence = 0;
            _displayStart = 0;
        }
        Raise(CaptionChangeKind.Cleared, null);
    }

    public CaptionSnapshot GetSnapshot(int maxLines)
    {
        lock (_gate)
        {
            int visible = _committed.Count - _displayStart;
            int take = Math.Min(Math.Max(0, maxLines), visible);
            return new CaptionSnapshot(_committed.GetRange(_committed.Count - take, take), _tentative);
        }
    }

    /// <summary>The last <paramref name="maxChars"/> characters of committed text, for prompting the model.</summary>
    public string GetRecentText(int maxChars)
    {
        lock (_gate)
        {
            var parts = new List<string>();
            int length = 0;
            for (int i = _committed.Count - 1; i >= 0 && length < maxChars; i--)
            {
                parts.Insert(0, _committed[i].Text);
                length += _committed[i].Text.Length + 1;
            }
            string text = string.Join(' ', parts);
            if (text.Length <= maxChars)
            {
                return text;
            }
            // Cut at a word boundary so the prompt never starts with half a word.
            int cut = text.Length - maxChars;
            if (text[cut - 1] != ' ')
            {
                int space = text.IndexOf(' ', cut);
                cut = space < 0 ? text.Length : space + 1;
            }
            return text[cut..];
        }
    }

    public string GetTranscriptText()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _committed.Select(l => l.Text));
        }
    }

    public CaptionLine? LastCommitted
    {
        get
        {
            lock (_gate)
            {
                return _committed.Count == 0 ? null : _committed[^1];
            }
        }
    }

    private void CloseLocked(Guid utteranceId)
    {
        if (_closedUtterances.Count > 1000)
        {
            _closedUtterances.Clear();
        }
        _closedUtterances.Add(utteranceId);
        if (_activeUtterance == utteranceId)
        {
            _activeUtterance = null;
        }
    }

    private void Raise(CaptionChangeKind kind, CaptionLine? line) => Changed?.Invoke(this, new CaptionChangedEventArgs(kind, line));
}
