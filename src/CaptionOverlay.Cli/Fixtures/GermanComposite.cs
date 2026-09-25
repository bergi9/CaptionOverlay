using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Export;
using CaptionOverlay.Core.Segmentation;
using CaptionOverlay.Core.Vad;

// Test tooling shared by the CLI (`fixtures fetch-de`) and the test project (linked source file).
// Everything derived from the committed FLEURS clips is built here in memory, never stored.
namespace CaptionOverlay.Cli.Fixtures;

/// <summary>One committed clip, as listed in <c>clips/transcripts.json</c>. Times are in seconds.</summary>
public sealed record GermanClip(
    string Id,
    string File,
    string Text,
    double DurationSec,
    double SpeechStartSec,
    double SpeechEndSec,
    string Bucket)
{
    public const string Normal = "normal";
    public const string Long = "long";
    public const string Short = "short";

    [JsonIgnore]
    public double SpeechSec => SpeechEndSec - SpeechStartSec;
}

/// <summary>A clip placed in the composite. Times are composite seconds (sample-accurate).</summary>
public sealed record ReferenceCue(
    int Index,
    string SourceId,
    double StartSec,
    double EndSec,
    double SpeechStartSec,
    double SpeechEndSec,
    string Text,
    double GapBeforeSec,
    bool ExpectMerged,
    string Bucket);

/// <summary>What the segmenter should produce: one per cue, except the short-gap pair, which is one.</summary>
public sealed record ExpectedUtterance(double SpeechStartSec, double SpeechEndSec, string Text, IReadOnlyList<ReferenceCue> Cues)
{
    public bool IsLong => Cues.Any(c => c.Bucket == GermanClip.Long);
}

/// <summary>
/// Builds the German test composite from the committed clips:
/// <c>[1.5 s] c1 [2.0] c2 [1.0] c3 [3.0] c4(long) [1.5] c5 [0.3] c6 [2.0] c7(short) [1.5] c8 [2.0 s]</c>.
/// Each clip is trimmed to its speech span ± 50 ms first, so the gaps are the real pauses.
/// </summary>
public sealed class GermanComposite
{
    public const int SampleRate = 16000;
    public const double TrimMarginSec = 0.05;
    public const double LeadingSilenceSec = 1.5;
    public const double TrailingSilenceSec = 2.0;
    public const double MergeGapSec = 0.3;

    /// <summary>Gaps after slot 1..7 (slot 4 is the long clip, 5+6 the merge pair, 7 the short clip).</summary>
    public static readonly double[] Gaps = [2.0, 1.0, 3.0, 1.5, MergeGapSec, 2.0, 1.5];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private GermanComposite(float[] samples, IReadOnlyList<ReferenceCue> cues, IReadOnlyList<GermanClip> clips)
    {
        Samples = samples;
        Cues = cues;
        Clips = clips;
    }

    /// <summary>16 kHz mono composite.</summary>
    public float[] Samples { get; }

    public IReadOnlyList<ReferenceCue> Cues { get; }

    public IReadOnlyList<GermanClip> Clips { get; }

    public double DurationSec => Samples.Length / (double)SampleRate;

    public static string TranscriptsPath(string folder) => Path.Combine(folder, "clips", "transcripts.json");

    public static IReadOnlyList<GermanClip> LoadClips(string folder)
    {
        string path = TranscriptsPath(folder);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"German fixtures missing: {path}. Run: captionoverlay-cli fixtures fetch-de", path);
        }
        return JsonSerializer.Deserialize<List<GermanClip>>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is empty");
    }

    public static float[] ReadClip(string folder, GermanClip clip) => WavIO.ReadMono16k(Path.Combine(folder, "clips", clip.File));

    /// <summary>
    /// Slot order: four normal clips in id order in slots 1, 2, 3, 8; the long clip in 4; the two normal clips
    /// with the shortest speech (in id order) as the merge pair in 5 + 6; the short clip in 7.
    /// </summary>
    public static IReadOnlyList<GermanClip> Arrange(IReadOnlyList<GermanClip> clips, SegmenterOptions? options = null)
    {
        options ??= new SegmenterOptions();
        var normal = clips.Where(c => c.Bucket == GermanClip.Normal).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        var longClips = clips.Where(c => c.Bucket == GermanClip.Long).ToList();
        var shortClips = clips.Where(c => c.Bucket == GermanClip.Short).ToList();
        if (normal.Count != 6 || longClips.Count != 1 || shortClips.Count != 1)
        {
            throw new InvalidDataException($"Expected 6 normal, 1 long and 1 short clip; got {normal.Count}/{longClips.Count}/{shortClips.Count}.");
        }

        var pair = normal.OrderBy(c => c.SpeechSec).ThenBy(c => c.Id, StringComparer.Ordinal).Take(2)
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        // The merged pair must stay one utterance: pre-roll + both trimmed clips + gap + post-roll under the force-cut limit.
        double merged = options.PreRollMs / 1000.0 + pair.Sum(c => c.SpeechSec + 2 * TrimMarginSec) + MergeGapSec + options.PostRollMs / 1000.0;
        if (merged > options.MaxUtteranceSec - 0.5)
        {
            throw new InvalidDataException(
                $"Merge pair {pair[0].Id} + {pair[1].Id} would be {merged:F2} s, too close to MaxUtteranceSec {options.MaxUtteranceSec} s. Pick shorter normal clips.");
        }

        var rest = normal.Except(pair).ToList();
        return [rest[0], rest[1], rest[2], longClips[0], pair[0], pair[1], shortClips[0], rest[3]];
    }

    public static GermanComposite Build(string folder, SegmenterOptions? options = null)
    {
        var clips = LoadClips(folder);
        var ordered = Arrange(clips, options);
        var samples = new List<float>(SampleRate * 120);
        var cues = new List<ReferenceCue>();

        AddSilence(samples, LeadingSilenceSec);
        double gapBefore = LeadingSilenceSec;
        for (int i = 0; i < ordered.Count; i++)
        {
            var clip = ordered[i];
            float[] audio = ReadClip(folder, clip);
            int from = Math.Max(0, ToSamples(clip.SpeechStartSec - TrimMarginSec));
            int to = Math.Min(audio.Length, ToSamples(clip.SpeechEndSec + TrimMarginSec));
            int start = samples.Count;
            samples.AddRange(audio.AsSpan(from, to - from));
            double startSec = start / (double)SampleRate;
            bool merged = i is 4 or 5;
            cues.Add(new ReferenceCue(
                i + 1,
                clip.Id,
                startSec,
                samples.Count / (double)SampleRate,
                startSec + (clip.SpeechStartSec - from / (double)SampleRate),
                startSec + (clip.SpeechEndSec - from / (double)SampleRate),
                clip.Text,
                gapBefore,
                merged,
                clip.Bucket));

            gapBefore = i < Gaps.Length ? Gaps[i] : TrailingSilenceSec;
            AddSilence(samples, gapBefore);
        }

        return new GermanComposite([.. samples], cues, clips);
    }

    /// <summary>Reference cues grouped the way the segmenter should group them (the short-gap pair merges).</summary>
    public IReadOnlyList<ExpectedUtterance> ExpectedUtterances()
    {
        var result = new List<ExpectedUtterance>();
        for (int i = 0; i < Cues.Count; i++)
        {
            var group = Cues[i].ExpectMerged && i + 1 < Cues.Count && Cues[i + 1].ExpectMerged
                ? new[] { Cues[i], Cues[++i] }
                : new[] { Cues[i] };
            result.Add(new ExpectedUtterance(group[0].SpeechStartSec, group[^1].SpeechEndSec, string.Join(' ', group.Select(c => c.Text)), group));
        }
        return result;
    }

    /// <summary>Reference SRT: one cue per expected utterance at the speech span (the long clip is one cue here).</summary>
    public string ReferenceSrt()
    {
        var sw = new StringWriter(CultureInfo.InvariantCulture);
        using (var srt = new SrtWriter(sw))
        {
            foreach (var u in ExpectedUtterances())
            {
                srt.Append(new CaptionLine(u.Text, TimeSpan.FromSeconds(u.SpeechStartSec), TimeSpan.FromSeconds(u.SpeechEndSec), Guid.Empty));
            }
        }
        return sw.ToString();
    }

    /// <summary>The composite upsampled to 48 kHz and duplicated into interleaved stereo, like real loopback audio.</summary>
    public float[] ToStereo48k()
    {
        float[] mono = new Resampler(48000).Process(Samples, SampleRate, 1);
        var stereo = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            stereo[2 * i] = mono[i];
            stereo[2 * i + 1] = mono[i];
        }
        return stereo;
    }

    /// <summary>20 s: 10 s digital zero, then 10 s white noise at about −60 dBFS (fixed seed).</summary>
    public static float[] Silence20s()
    {
        var samples = new float[20 * SampleRate];
        var rng = new Random(20250925);
        // Uniform noise in [-a, a] has RMS a/√3; −60 dBFS RMS = 0.001.
        float a = 0.001f * MathF.Sqrt(3);
        for (int i = 10 * SampleRate; i < samples.Length; i++)
        {
            samples[i] = (float)(rng.NextDouble() * 2 - 1) * a;
        }
        return samples;
    }

    private static void AddSilence(List<float> samples, double seconds) => samples.AddRange(new float[ToSamples(seconds)]);

    private static int ToSamples(double seconds) => (int)Math.Round(seconds * SampleRate);
}

/// <summary>Runs Silero VAD + the app's segmenter over audio, the same way the pipeline does (via <see cref="AudioFrontEnd"/>).</summary>
public static class FixtureSegmentation
{
    public sealed record Result(IReadOnlyList<FinalUtterance> Finals, int Discarded);

    /// <param name="interleaved">Samples at any rate / channel count; delivered in 20 ms chunks like a capture device.</param>
    public static Result Run(float[] interleaved, int sampleRate, int channels, SegmenterOptions options, string vadModelPath)
    {
        using var vad = new SileroVad(vadModelPath);
        var segmenter = new UtteranceSegmenter(options with { EmitPartials = false });
        var frontEnd = new AudioFrontEnd(vad.FrameSize);
        var finals = new List<FinalUtterance>();
        int discarded = 0;

        void Collect(IEnumerable<SegmenterEvent> events)
        {
            foreach (var e in events)
            {
                if (e is FinalUtterance f)
                {
                    finals.Add(f);
                }
                else if (e is UtteranceDiscarded)
                {
                    discarded++;
                }
            }
        }

        void OnFrame(float[] frame) => Collect(segmenter.Process(frame, vad.Process(frame)));

        int chunk = sampleRate / 50 * channels;
        for (int i = 0; i < interleaved.Length; i += chunk)
        {
            int n = Math.Min(chunk, interleaved.Length - i);
            frontEnd.Push(new AudioChunk(interleaved.AsSpan(i, n).ToArray(), sampleRate, channels), OnFrame);
        }
        frontEnd.Flush(OnFrame);
        Collect(segmenter.Flush());
        return new Result(finals, discarded);
    }

    /// <summary>Human-readable list of utterances, for assertion messages.</summary>
    public static string Describe(IEnumerable<FinalUtterance> finals) =>
        string.Join("; ", finals.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.StartOffset.TotalSeconds:F2}–{f.End.TotalSeconds:F2}")));
}

/// <summary>WER helpers; the normalization is shared by the tests and the CLI.</summary>
public static class Wer
{
    /// <summary>NFC, lowercase, hyphens/slashes → space, other punctuation removed, whitespace collapsed; umlauts/ß kept.</summary>
    public static string Normalize(string text)
    {
        string s = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '-' or '/' or '‐' or '–' or '—' || char.IsWhiteSpace(c))
            {
                sb.Append(' ');
            }
            else if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string[] Words(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Word-level edit distance (substitutions + insertions + deletions).</summary>
    public static int Errors(string reference, string hypothesis)
    {
        var r = Words(reference);
        var h = Words(hypothesis);
        var prev = new int[h.Length + 1];
        var cur = new int[h.Length + 1];
        for (int j = 0; j <= h.Length; j++)
        {
            prev[j] = j;
        }
        for (int i = 1; i <= r.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= h.Length; j++)
            {
                int sub = prev[j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1);
                cur[j] = Math.Min(sub, Math.Min(prev[j] + 1, cur[j - 1] + 1));
            }
            (prev, cur) = (cur, prev);
        }
        return prev[h.Length];
    }

    public static double Rate(string reference, string hypothesis)
    {
        int n = Words(reference).Length;
        return n == 0 ? (Words(hypothesis).Length == 0 ? 0 : 1) : Errors(reference, hypothesis) / (double)n;
    }
}
