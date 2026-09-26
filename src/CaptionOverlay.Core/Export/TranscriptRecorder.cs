using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Settings;

namespace CaptionOverlay.Core.Export;

public sealed record TranscriptOptions(string Folder, bool Srt, bool Txt, TranscriptSplit Split, TimeSpan Break)
{
    public bool Enabled => Srt || Txt;

    /// <summary>Same files: changing only the split rule keeps the current file.</summary>
    public bool SameFiles(TranscriptOptions other) =>
        string.Equals(Folder, other.Folder, StringComparison.OrdinalIgnoreCase) && Srt == other.Srt && Txt == other.Txt;
}

/// <summary>
/// Owns the auto-saved transcript files and decides when a new pair starts (<see cref="TranscriptSplit"/>, or
/// <see cref="SplitNow"/>). A file can span several listening sessions: caption times (relative to each session's
/// start) are converted to times relative to the file's start, so the SRT stays continuous across a stop and start.
/// Not thread-safe; call from one thread.
/// </summary>
public sealed class TranscriptRecorder : IDisposable
{
    private readonly TimeProvider _time;
    private TranscriptOptions? _options;
    private TranscriptSession? _session;
    private DateTimeOffset _fileStart;
    private DateTimeOffset _sessionStart;
    private DateTimeOffset? _lastLineEnd;

    public TranscriptRecorder(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The open files without extension, or null while none is open.</summary>
    public string? CurrentBasePath => _session?.BasePath;

    /// <summary>
    /// Listening started; <paramref name="sessionStart"/> is the wall-clock time of caption time zero. Null options:
    /// auto-save is off.
    /// </summary>
    public void ListeningStarted(TranscriptOptions? options, DateTimeOffset sessionStart)
    {
        _sessionStart = sessionStart;
        ApplyOptions(options);
        if (_options?.Split == TranscriptSplit.EachStart)
        {
            // One file per listening session, its times aligned to the session (as a recording of it would be).
            Close();
            Open(sessionStart);
        }
    }

    public void ListeningStopped()
    {
        if (_options?.Split == TranscriptSplit.EachStart)
        {
            Close();
        }
    }

    /// <summary>Settings changed while listening: applies the new rule; a different folder or format starts new files.</summary>
    public void Update(TranscriptOptions? options) => ApplyOptions(options);

    public void Append(CaptionLine line)
    {
        if (_options is not { } options)
        {
            return;
        }
        var start = _sessionStart + line.Start;
        var end = _sessionStart + line.End;
        if (_session is not null && options.Split == TranscriptSplit.AfterBreak && _lastLineEnd is { } last && start - last >= options.Break)
        {
            Close();
        }
        if (_session is null)
        {
            Open(start);
        }
        _lastLineEnd = end;
        _session?.Append(line with { Start = start - _fileStart, End = end - _fileStart });
    }

    /// <summary>Closes the current files and starts new ones now. False when auto-save is off.</summary>
    public bool SplitNow()
    {
        if (_options is null)
        {
            return false;
        }
        Close();
        Open(_time.GetLocalNow());
        return _session is not null;
    }

    public void Close()
    {
        _session?.Dispose();
        _session = null;
        _lastLineEnd = null;
    }

    public void Dispose() => Close();

    private void ApplyOptions(TranscriptOptions? options)
    {
        if (options is null || !options.Enabled || (_options is { } old && !old.SameFiles(options)))
        {
            Close();
        }
        _options = options is { Enabled: true } ? options : null;
    }

    private void Open(DateTimeOffset fileStart)
    {
        if (_options is not { } options)
        {
            return;
        }
        _session = new TranscriptSession(options.Folder, options.Srt, options.Txt, fileStart.DateTime); // named in the clock time of its own offset (local)
        _fileStart = fileStart;
        _lastLineEnd = null;
    }
}
