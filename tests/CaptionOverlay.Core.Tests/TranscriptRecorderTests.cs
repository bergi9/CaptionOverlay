using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Export;
using CaptionOverlay.Core.Settings;

namespace CaptionOverlay.Core.Tests;

public sealed class TranscriptRecorderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 14, 0, 0, TimeSpan.FromHours(2));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "co-recorder-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private TranscriptOptions Options(TranscriptSplit split, int breakMinutes = 30) =>
        new(_dir, Srt: true, Txt: true, split, TimeSpan.FromMinutes(breakMinutes));

    private static CaptionLine Line(string text, double startSec, double endSec) =>
        new(text, TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(endSec), Guid.NewGuid());

    private string[] TxtFiles() => [.. Directory.GetFiles(_dir, "*.txt").Order().Select(File.ReadAllText)];

    private string[] SrtFiles() => [.. Directory.GetFiles(_dir, "*.srt").Order().Select(File.ReadAllText)];

    [Fact]
    public void After_break_a_short_pause_and_restart_continue_the_same_file_with_continuous_times()
    {
        using var r = new TranscriptRecorder(_time);
        r.ListeningStarted(Options(TranscriptSplit.AfterBreak), T0);
        r.Append(Line("Before the break.", 10, 12));
        r.ListeningStopped();
        // Listening again 5 minutes later: a new pipeline session, its caption times start at zero again.
        r.ListeningStarted(Options(TranscriptSplit.AfterBreak), T0.AddMinutes(5));
        r.Append(Line("After the break.", 3, 5));
        r.Close();

        TxtFiles().Should().ContainSingle().Which.Should().Be("Before the break.\r\nAfter the break.\r\n");
        string srt = SrtFiles().Single();
        // The file starts at the first line (T0 + 10 s): the second line is 5 min − 7 s later.
        srt.Should().Contain("00:00:00,000 --> 00:00:02,000").And.Contain("00:04:53,000 --> 00:04:55,000").And.Contain("2\r\n");
    }

    [Fact]
    public void After_break_a_long_gap_starts_a_new_file()
    {
        using var r = new TranscriptRecorder(_time);
        r.ListeningStarted(Options(TranscriptSplit.AfterBreak, breakMinutes: 30), T0);
        r.Append(Line("Morning.", 0, 2));
        r.Append(Line("Still morning.", 29 * 60, 29 * 60 + 2)); // 29 min gap: same file
        r.Append(Line("Afternoon.", 70 * 60, 70 * 60 + 2));    // 41 min gap: new file
        r.Close();

        TxtFiles().Should().Equal("Morning.\r\nStill morning.\r\n", "Afternoon.\r\n");
        SrtFiles()[1].Should().StartWith("1\r\n00:00:00,000 --> 00:00:02,000");
        Directory.GetFiles(_dir, "*.txt").Select(Path.GetFileName).Should().Equal("2026-09-26_14-00-00.txt", "2026-09-26_15-10-00.txt");
    }

    [Fact]
    public void Each_start_opens_a_file_per_listening_session_aligned_to_its_start()
    {
        using var r = new TranscriptRecorder(_time);
        r.ListeningStarted(Options(TranscriptSplit.EachStart), T0);
        r.Append(Line("One.", 4, 5));
        r.ListeningStopped();
        r.ListeningStarted(Options(TranscriptSplit.EachStart), T0.AddMinutes(1));
        r.Append(Line("Two.", 4, 5));
        r.Close();

        TxtFiles().Should().Equal("One.\r\n", "Two.\r\n");
        SrtFiles().Should().AllSatisfy(s => s.Should().Contain("00:00:04,000 --> 00:00:05,000"));
    }

    [Fact]
    public void Manual_ignores_gaps_and_restarts_but_split_now_starts_a_new_file()
    {
        using var r = new TranscriptRecorder(_time);
        r.ListeningStarted(Options(TranscriptSplit.Manual), T0);
        r.Append(Line("A.", 0, 1));
        r.ListeningStopped();
        r.ListeningStarted(Options(TranscriptSplit.Manual), T0.AddHours(3));
        r.Append(Line("B.", 0, 1));
        _time.Now = T0.AddHours(3).AddMinutes(1);
        r.SplitNow().Should().BeTrue();
        r.CurrentBasePath.Should().EndWith("2026-09-26_17-01-00");
        r.Append(Line("C.", 65, 66)); // 5 s after the split
        r.Close();

        TxtFiles().Should().Equal("A.\r\nB.\r\n", "C.\r\n");
        SrtFiles()[1].Should().Contain("00:00:05,000 --> 00:00:06,000");
    }

    [Fact]
    public void Split_now_twice_in_one_second_never_overwrites()
    {
        using var r = new TranscriptRecorder(_time);
        _time.Now = T0;
        r.ListeningStarted(Options(TranscriptSplit.Manual), T0);
        r.SplitNow();
        r.Append(Line("First.", 0, 1));
        r.SplitNow();
        r.Append(Line("Second.", 0, 1));
        r.Close();

        Directory.GetFiles(_dir, "*.txt").Select(Path.GetFileName).Should().BeEquivalentTo("2026-09-26_14-00-00.txt", "2026-09-26_14-00-00_2.txt");
        TxtFiles().Should().BeEquivalentTo("First.\r\n", "Second.\r\n");
    }

    [Fact]
    public void Auto_save_off_writes_nothing_and_split_now_reports_it()
    {
        using var r = new TranscriptRecorder(_time);
        r.ListeningStarted(null, T0);
        r.Append(Line("Not saved.", 0, 1));
        r.SplitNow().Should().BeFalse();
        Directory.Exists(_dir).Should().BeFalse();
    }

    [Fact]
    public void Changing_the_folder_or_format_starts_new_files_changing_the_rule_does_not()
    {
        using var r = new TranscriptRecorder(_time);
        r.ListeningStarted(Options(TranscriptSplit.AfterBreak), T0);
        r.Append(Line("A.", 0, 1));
        r.Update(Options(TranscriptSplit.Manual));
        r.Append(Line("B.", 2, 3));
        r.Update(Options(TranscriptSplit.Manual) with { Srt = false });
        r.Append(Line("C.", 4, 5));
        r.Close();

        TxtFiles().Should().Equal("A.\r\nB.\r\n", "C.\r\n");
        SrtFiles().Should().ContainSingle();
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test");
    }
}
