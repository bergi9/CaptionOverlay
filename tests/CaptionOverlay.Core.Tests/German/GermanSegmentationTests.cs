using System.Globalization;
using static System.FormattableString;
using System.Text.RegularExpressions;
using CaptionOverlay.Cli.Fixtures;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Export;
using CaptionOverlay.Core.Pipeline;
using CaptionOverlay.Core.Segmentation;
using CaptionOverlay.Core.Transcription;
using CaptionOverlay.Core.Vad;

namespace CaptionOverlay.Core.Tests.German;

/// <summary>German FLEURS composite: segmentation, resampling and SRT timing. No Whisper model needed.</summary>
public partial class GermanSegmentationTests
{
    private const double BoundaryToleranceSec = 0.2;
    private static readonly SegmenterOptions Defaults = new();
    private static readonly double PreRoll = Defaults.PreRollMs / 1000.0;
    private static readonly double PostRoll = Defaults.PostRollMs / 1000.0;

    private static readonly Lazy<IReadOnlyList<FinalUtterance>> Finals16k = new(() =>
        FixtureSegmentation.Run(GermanFixtures.Composite.Samples, 16000, 1, Defaults, Fixtures.SileroModel).Finals);

    /// <summary>Speech span the segmenter detected: utterance audio minus pre-/post-roll.</summary>
    private static (double Start, double End) Speech(FinalUtterance f) => (f.StartOffset.TotalSeconds + PreRoll, f.End.TotalSeconds - PostRoll);

    private static bool Overlaps(FinalUtterance f, ExpectedUtterance u) => Speech(f).Start < u.SpeechEndSec && Speech(f).End > u.SpeechStartSec;

    private static List<FinalUtterance> Matching(IEnumerable<FinalUtterance> finals, ExpectedUtterance u) =>
        [.. finals.Where(f => Overlaps(f, u))];

    private static string Expected(IEnumerable<ExpectedUtterance> utterances) =>
        string.Join("; ", utterances.Select(u => string.Create(CultureInfo.InvariantCulture, $"{u.SpeechStartSec:F2}–{u.SpeechEndSec:F2}")));

    private static string Actual(IEnumerable<FinalUtterance> finals) =>
        string.Join("; ", finals.Select(f => string.Create(CultureInfo.InvariantCulture, $"{Speech(f).Start:F2}–{Speech(f).End:F2}")));

    [Fact]
    public void Fixtures_ManifestHashesMatch()
    {
        FixtureManifest.Verify(GermanFixtures.Folder).Should().BeEmpty("every file in manifest.json must exist with its SHA-256");
        GermanFixtures.Clips.Should().HaveCount(8);
        GermanFixtures.Clips.Should().OnlyContain(c => c.SpeechStartSec < c.SpeechEndSec && c.SpeechEndSec <= c.DurationSec);
    }

    [Fact]
    public void Segmenter_FindsUtteranceBoundaries()
    {
        var expected = GermanFixtures.Composite.ExpectedUtterances();
        var finals = Finals16k.Value;
        string context = $"expected speech {Expected(expected)} | detected {Actual(finals)}";

        foreach (var u in expected)
        {
            var hits = Matching(finals, u);
            if (u.IsLong)
            {
                hits.Should().HaveCountGreaterThanOrEqualTo(2, $"the long clip is force-cut ({context})");
            }
            else
            {
                hits.Should().ContainSingle(Invariant($"cue at {u.SpeechStartSec:F2} s must be exactly one utterance ({context})"));
            }
            Speech(hits[0]).Start.Should().BeApproximately(u.SpeechStartSec, BoundaryToleranceSec, Invariant($"speech start of cue at {u.SpeechStartSec:F2} s ({context})"));
            Speech(hits[^1]).End.Should().BeApproximately(u.SpeechEndSec, BoundaryToleranceSec, Invariant($"speech end of cue ending at {u.SpeechEndSec:F2} s ({context})"));
        }
        finals.Should().OnlyContain(f => expected.Any(u => Overlaps(f, u)), $"no utterance outside the reference cues ({context})");
    }

    [Fact]
    public void Segmenter_MergesShortGap()
    {
        var pair = GermanFixtures.Composite.ExpectedUtterances().Single(u => u.Cues.Count == 2);
        pair.Cues[1].GapBeforeSec.Should().Be(GermanComposite.MergeGapSec);
        var hits = Matching(Finals16k.Value, pair);
        hits.Should().ContainSingle(Invariant($"a {GermanComposite.MergeGapSec} s gap is shorter than EndSilenceMs {Defaults.EndSilenceMs} ms (detected {Actual(Finals16k.Value)})"));
    }

    [Fact]
    public void Segmenter_ForceCutsLongUtterance()
    {
        var longClip = GermanFixtures.Composite.ExpectedUtterances().Single(u => u.IsLong);
        var parts = Matching(Finals16k.Value, longClip);

        parts.Should().HaveCountGreaterThanOrEqualTo(2);
        parts.Should().OnlyContain(f => f.Duration.TotalSeconds <= Defaults.MaxUtteranceSec + 0.1);
        for (int i = 1; i < parts.Count; i++)
        {
            parts[i].StartOffset.Should().Be(parts[i - 1].End, "a force cut continues seamlessly (no audio lost)");
        }
        double total = parts.Sum(f => f.Duration.TotalSeconds);
        double speech = longClip.SpeechEndSec - longClip.SpeechStartSec;
        total.Should().BeApproximately(speech + PreRoll + PostRoll, 2 * BoundaryToleranceSec, "the parts cover the whole clip plus pre-/post-roll");
    }

    [Fact]
    public void Segmenter_SilenceProducesNothing()
    {
        var run = FixtureSegmentation.Run(GermanComposite.Silence20s(), 16000, 1, Defaults, Fixtures.SileroModel);
        run.Finals.Should().BeEmpty();
    }

    [Fact]
    public void Resampler_48kStereoMatches16kMono()
    {
        var stereo = GermanFixtures.Composite.ToStereo48k();
        var finals48 = FixtureSegmentation.Run(stereo, 48000, 2, Defaults, Fixtures.SileroModel).Finals;
        var finals16 = Finals16k.Value;
        string context = $"16 kHz {Actual(finals16)} | 48 kHz {Actual(finals48)}";

        finals48.Should().HaveCount(finals16.Count, context);
        foreach (var (a, b) in finals16.Zip(finals48))
        {
            Speech(b).Start.Should().BeApproximately(Speech(a).Start, 0.05, context);
            Speech(b).End.Should().BeApproximately(Speech(a).End, 0.05, context);
        }
    }

    public static TheoryData<int> TranscriberDelaysMs => [0, 250];

    [Theory]
    [MemberData(nameof(TranscriberDelaysMs))]
    public async Task Pipeline_FakeTranscriber_SrtTiming(int delayMs)
    {
        var composite = GermanFixtures.Composite;
        var transcriber = new ReferenceTranscriber(composite, TimeSpan.FromMilliseconds(delayMs));
        var config = new PipelineConfig
        {
            AudioSourceFactory = () => new MemoryAudioSource(composite.Samples, 16000, 1),
            VadFactory = () => new SileroVad(Fixtures.SileroModel),
            Language = "de",
            EnablePartials = true,
        };

        var srtText = new StringWriter(CultureInfo.InvariantCulture);
        var pipeline = new CaptionPipeline();
        using (var srt = new SrtWriter(srtText))
        {
            pipeline.LineCommitted += line =>
            {
                lock (srt)
                {
                    srt.Append(line);
                }
            };
            await pipeline.StartAsync(config, _ => Task.FromResult(new TranscriberSet(transcriber)), TestContext.Current.CancellationToken);
            await pipeline.WaitForSourceCompletionAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            await pipeline.StopAsync(drain: true, TimeSpan.FromSeconds(60));
        }

        var actual = SrtCue.Parse(srtText.ToString());
        var reference = SrtCue.Parse(composite.ReferenceSrt());
        var longCue = reference[composite.ExpectedUtterances().ToList().FindIndex(u => u.IsLong)];
        int longParts = actual.Count(c => c.Start < longCue.End && c.End > longCue.Start);
        string context = $"reference starts {string.Join(", ", reference.Select(c => c.Start.ToString("F2", CultureInfo.InvariantCulture)))} | " +
            $"actual {string.Join(", ", actual.Select(c => c.Start.ToString("F2", CultureInfo.InvariantCulture)))}";

        longParts.Should().BeGreaterThanOrEqualTo(2, context);
        actual.Should().HaveCount(reference.Count + longParts - 1, $"one cue per reference cue, the long one split in {longParts} ({context})");
        foreach (var r in reference)
        {
            var first = actual.First(c => c.End > r.Start && c.Start < r.End);
            first.Start.Should().BeApproximately(r.Start, 0.5, Invariant($"cue at {r.Start:F2} s ({context})"));
            if (r != longCue)
            {
                first.Text.Should().Be(r.Text);
            }
        }
        TestContext.Current.SendDiagnosticMessage($"delay {delayMs} ms: {transcriber.Calls} transcriber calls, dropped partials {pipeline.Metrics.DroppedPartials}");
    }

    /// <summary>Minimal SRT reader for comparing exported cues.</summary>
    private sealed partial record SrtCue(double Start, double End, string Text)
    {
        public static List<SrtCue> Parse(string srt) =>
            [.. Cue().Matches(srt.ReplaceLineEndings("\n")).Select(m => new SrtCue(Time(m.Groups[1].Value), Time(m.Groups[2].Value), m.Groups[3].Value.Trim()))];

        private static double Time(string t) => TimeSpan.ParseExact(t, @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture).TotalSeconds;

        [GeneratedRegex(@"^\d+\n(\S+) --> (\S+)\n(.+?)(?:\n\n|\n?$)", RegexOptions.Multiline | RegexOptions.Singleline)]
        private static partial Regex Cue();
    }
}

public class WerTests
{
    [Theory]
    [InlineData("T-Rex", "t rex")]
    [InlineData("„Hallo“, sagte er; dann ging er.", "hallo sagte er dann ging er")]
    [InlineData("Straße  und  Öl/Gas", "straße und öl gas")]
    [InlineData("am 15. August 1940", "am 15 august 1940")]
    [InlineData("Kühe", "kühe")]
    public void Normalize_handles_punctuation_case_and_umlauts(string input, string expected) =>
        Wer.Normalize(input).Should().Be(expected);

    [Fact]
    public void Rate_counts_substitutions_insertions_and_deletions()
    {
        Wer.Rate("der t rex war groß", "Der T-Rex war groß.").Should().Be(0);
        Wer.Rate("a b c d", "a x c d").Should().Be(0.25);
        Wer.Rate("a b c d", "a b c d e").Should().Be(0.25);
        Wer.Rate("a b c d", "a c d").Should().Be(0.25);
        Wer.Rate("a b", "").Should().Be(1);
    }
}
