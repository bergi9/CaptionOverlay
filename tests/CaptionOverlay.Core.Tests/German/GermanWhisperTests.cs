using System.Collections.Concurrent;
using System.Globalization;
using static System.FormattableString;
using System.Text;
using CaptionOverlay.Cli.Fixtures;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Models;
using CaptionOverlay.Core.Pipeline;
using CaptionOverlay.Core.Transcription;
using CaptionOverlay.Core.Vad;

namespace CaptionOverlay.Core.Tests.German;

/// <summary>
/// German WER per catalog model. Each theory row skips unless that model is installed (or given by
/// <c>CAPTIONOVERLAY_TEST_MODEL_&lt;ID&gt;</c>). One class, so the models run one after another on the GPU.
/// </summary>
[Trait("Category", "RequiresModel")]
public class GermanWhisperTests
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ModelRun>>> Runs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ResolvedModel Require(string id)
    {
        var model = GermanModels.Resolve(id);
        Assert.SkipWhen(model is null, $"{id} is not installed (download it with: captionoverlay-cli download {id})");
        return model!;
    }

    /// <summary>Transcribes every clip once per model; the per-clip and aggregate tests share the result.</summary>
    private static Task<ModelRun> RunAsync(string id, ResolvedModel model) =>
        Runs.GetOrAdd(id, _ => new Lazy<Task<ModelRun>>(async () =>
        {
            await using var transcriber = await GermanModels.LoadAsync(model, CancellationToken.None);
            var results = new List<ClipResult>();
            foreach (var clip in GermanFixtures.Clips)
            {
                float[] audio = GermanComposite.ReadClip(GermanFixtures.Folder, clip);
                var r = await transcriber.TranscribeAsync(audio, new TranscriptionOptions("de", null, false), CancellationToken.None);
                results.Add(new ClipResult(clip.Id, clip.Text, r.Text, Wer.Words(clip.Text).Length, Wer.Errors(clip.Text, r.Text),
                    r.InferenceTime.TotalSeconds, audio.Length / 16000.0));
            }
            return new ModelRun(id, transcriber.RuntimeDescription, results);
        })).Value;

    [Theory]
    [MemberData(nameof(GermanModels.Ids), MemberType = typeof(GermanModels))]
    public async Task LocalWhisper_German_PerClipWer(string modelId)
    {
        var model = Require(modelId);
        var run = await RunAsync(modelId, model).WaitAsync(Ct);

        var report = new StringBuilder($"{modelId} on {run.Runtime}:\n");
        foreach (var c in run.Clips)
        {
            report.Append(CultureInfo.InvariantCulture, $"  {c.Id}  WER {c.Wer,6:P1}  {c.InferenceSec * 1000,6:F0} ms  \"{c.Hypothesis}\"\n");
        }
        TestContext.Current.SendDiagnosticMessage(report.ToString());
        run.Clips.Should().HaveCount(GermanFixtures.Clips.Count);
        run.Clips.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Hypothesis), "every clip is speech");
    }

    [Theory]
    [MemberData(nameof(GermanModels.Ids), MemberType = typeof(GermanModels))]
    public async Task LocalWhisper_German_AggregateWer(string modelId)
    {
        var model = Require(modelId);
        var run = await RunAsync(modelId, model).WaitAsync(Ct);
        TestContext.Current.SendDiagnosticMessage(
            Invariant($"{modelId}: aggregate WER {run.AggregateWer:P1}, RTF {run.RealTimeFactor:F3}, runtime {run.Runtime}"));

        WerBaseline.Update(modelId, previous => ToBaseline(run, previous?.EndToEndWer));

        if (GermanModels.Thresholds[modelId] is { } threshold)
        {
            run.AggregateWer.Should().BeLessThanOrEqualTo(threshold,
                Invariant($"{modelId} per-clip WER: {string.Join(", ", run.Clips.Select(c => c.Id + " " + c.Wer.ToString("P0", CultureInfo.InvariantCulture)))}"));
        }
    }

    [Theory]
    [MemberData(nameof(GermanModels.Ids), MemberType = typeof(GermanModels))]
    public async Task LocalWhisper_SilenceNoHallucination(string modelId)
    {
        var model = Require(modelId);
        await using var transcriber = await GermanModels.LoadAsync(model, Ct);
        var filter = new HallucinationFilter();
        float[] silence = GermanComposite.Silence20s();
        int half = silence.Length / 2;

        foreach (var (name, audio) in new[] { ("20 s", silence), ("digital zero", silence[..half]), ("-60 dBFS noise", silence[half..]) })
        {
            var r = await transcriber.TranscribeAsync(audio, new TranscriptionOptions("de", null, false), Ct);
            var verdict = filter.Apply(new FilterInput(r.Text, "de", null, HallucinationFilter.Rms(audio), r.AverageProbability));
            TestContext.Current.SendDiagnosticMessage($"{modelId} {name}: raw \"{r.Text}\" → {(verdict.Keep ? "KEPT" : verdict.Reason)}");
            verdict.Keep.Should().BeFalse($"{name} must not produce a caption (raw text \"{r.Text}\")");
        }
    }

    [Theory]
    [MemberData(nameof(GermanModels.Ids), MemberType = typeof(GermanModels))]
    public async Task Pipeline_Local_EndToEnd_Composite(string modelId)
    {
        var model = Require(modelId);
        var composite = GermanFixtures.Composite;
        var stereo = composite.ToStereo48k();
        var config = new PipelineConfig
        {
            AudioSourceFactory = () => new MemoryAudioSource(stereo, 48000, 2),
            VadFactory = () => new SileroVad(Fixtures.SileroModel),
            Language = "de",
            EnablePartials = false,
        };
        var lines = new List<CaptionLine>();
        var pipeline = new CaptionPipeline();
        pipeline.LineCommitted += l =>
        {
            lock (lines)
            {
                lines.Add(l);
            }
        };
        await pipeline.StartAsync(config, async ct => new TranscriberSet(await GermanModels.LoadAsync(model, ct)), Ct);
        await pipeline.WaitForSourceCompletionAsync(Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);
        await pipeline.StopAsync(drain: true, TimeSpan.FromMinutes(5));

        string reference = string.Join(' ', composite.Cues.Select(c => c.Text));
        string hypothesis = string.Join(' ', lines.Select(l => l.Text));
        double wer = Wer.Rate(reference, hypothesis);
        var metrics = pipeline.Metrics;
        TestContext.Current.SendDiagnosticMessage(
            Invariant($"{modelId} end-to-end (48 kHz stereo): {lines.Count} lines, WER {wer:P1}, RTF {metrics.RealTimeFactor:F3}\n") +
            string.Join('\n', lines.Select(l => Invariant($"  [{l.Start.TotalSeconds,6:F2} → {l.End.TotalSeconds,6:F2}] {l.Text}"))));
        var perClip = await RunAsync(modelId, model).WaitAsync(Ct);
        WerBaseline.Update(modelId, previous => (previous ?? ToBaseline(perClip, null)) with { EndToEndWer = Math.Round(wer, 4) });

        foreach (var u in composite.ExpectedUtterances())
        {
            var first = lines.FirstOrDefault(l => l.End.TotalSeconds > u.SpeechStartSec && l.Start.TotalSeconds < u.SpeechEndSec);
            first.Should().NotBeNull(Invariant($"a committed line for the cue at {u.SpeechStartSec:F2} s"));
            first!.Start.TotalSeconds.Should().BeApproximately(u.SpeechStartSec, 0.5, Invariant($"SRT start of the cue at {u.SpeechStartSec:F2} s"));
        }

        if (GermanModels.Thresholds[modelId] is not null)
        {
            double baseline = WerBaseline.Get(modelId)?.AggregateWer ?? perClip.AggregateWer;
            wer.Should().BeLessThanOrEqualTo(baseline + 0.05, Invariant($"end-to-end WER vs the per-clip baseline {baseline:P1} + 5 points; hypothesis \"{hypothesis}\""));
        }
    }

    private static WerBaselineEntry ToBaseline(ModelRun run, double? endToEnd) => new(
        WerBaseline.Today,
        run.Runtime,
        Math.Round(run.AggregateWer, 4),
        Math.Round(run.RealTimeFactor, 4),
        run.Clips.ToDictionary(c => c.Id, c => Math.Round(c.Wer, 4)),
        endToEnd);
}
