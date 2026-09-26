using System.Globalization;
using System.Text;
using CaptionOverlay.Core.Captions;

namespace CaptionOverlay.Core.Export;

public interface ITranscriptWriter : IDisposable
{
    void Append(CaptionLine line);
}

/// <summary>Appends SubRip entries and flushes after each one, so a crash loses at most one line.</summary>
public sealed class SrtWriter : ITranscriptWriter
{
    private readonly TextWriter _writer;
    private int _index;

    public SrtWriter(string path)
        : this(TranscriptSession.OpenShared(path))
    {
    }

    public SrtWriter(TextWriter writer)
    {
        _writer = writer;
        _writer.NewLine = "\r\n";
    }

    public static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
    }

    public void Append(CaptionLine line)
    {
        var end = line.End > line.Start ? line.End : line.Start + TimeSpan.FromSeconds(1);
        _writer.WriteLine((++_index).ToString(CultureInfo.InvariantCulture));
        _writer.WriteLine($"{FormatTime(line.Start)} --> {FormatTime(end)}");
        _writer.WriteLine(line.Text);
        _writer.WriteLine();
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();
}

/// <summary>Plain text transcript, one caption per line.</summary>
public sealed class TxtWriter : ITranscriptWriter
{
    private readonly TextWriter _writer;

    public TxtWriter(string path)
        : this(TranscriptSession.OpenShared(path))
    {
    }

    public TxtWriter(TextWriter writer)
    {
        _writer = writer;
    }

    public void Append(CaptionLine line)
    {
        _writer.WriteLine(line.Text);
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();
}

/// <summary>Per-session auto-save: <c>yyyy-MM-dd_HH-mm-ss.srt</c> / <c>.txt</c> in the transcripts folder.</summary>
public sealed class TranscriptSession : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<ITranscriptWriter> _writers = [];

    public TranscriptSession(string folder, bool srt, bool txt, DateTime? startedAt = null)
    {
        Directory.CreateDirectory(folder);
        string baseName = (startedAt ?? DateTime.Now).ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        BasePath = Path.Combine(folder, baseName);
        // Two files in the same second ("Split now" twice): never overwrite, number the newer one.
        for (int n = 2; File.Exists(BasePath + ".srt") || File.Exists(BasePath + ".txt"); n++)
        {
            BasePath = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{baseName}_{n}"));
        }
        if (srt)
        {
            _writers.Add(new SrtWriter(BasePath + ".srt"));
        }
        if (txt)
        {
            _writers.Add(new TxtWriter(BasePath + ".txt"));
        }
    }

    public string BasePath { get; }

    /// <summary>Opens a transcript file that other apps (editors, OBS) can read while it is being written.</summary>
    internal static StreamWriter OpenShared(string path) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CaptionOverlay");

    public void Append(CaptionLine line)
    {
        lock (_gate)
        {
            foreach (var writer in _writers)
            {
                writer.Append(line);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var writer in _writers)
            {
                writer.Dispose();
            }
            _writers.Clear();
        }
    }
}
