using System.Globalization;
using System.Text;
using CaptionOverlay.Cli.Fixtures;
using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Pipeline;
using CaptionOverlay.Core.Segmentation;
using CaptionOverlay.Core.Settings;
using CaptionOverlay.Core.Tests.German;
using CaptionOverlay.Core.Transcription;
using CaptionOverlay.Core.Vad;
using static System.FormattableString;

namespace CaptionOverlay.Core.Tests.Transcription;

/// <summary>
/// Real calls to a transcription API. Opt-in only (they cost money): set <c>CAPTIONOVERLAY_TEST_API</c> to a provider id
/// (<c>openai</c>, <c>groq</c>, <c>speaches</c>). The key comes from <c>CAPTIONOVERLAY_TEST_API_KEY</c> or, if unset, from the key saved in the
/// app (Settings → API, DPAPI-encrypted for the current Windows user), so it never has to appear on a command line.
/// <c>CAPTIONOVERLAY_TEST_API_MODEL</c> overrides the provider's default model, <c>CAPTIONOVERLAY_TEST_API_URL</c> the base URL
/// (needed for a self-hosted <c>speaches</c> server).
/// </summary>
[Trait("Category", "RequiresApiKey")]
public class LiveApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BaseUrl(ApiProviderPreset preset) =>
        Environment.GetEnvironmentVariable("CAPTIONOVERLAY_TEST_API_URL") is { Length: > 0 } url ? url.Trim() : preset.BaseUrl;

    private static (ApiProviderPreset Preset, string Key, string Model)? Configured()
    {
        string? provider = Environment.GetEnvironmentVariable("CAPTIONOVERLAY_TEST_API");
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }
        var preset = ApiProviderPreset.Find(provider.Trim().ToLowerInvariant());
        string? key = Environment.GetEnvironmentVariable("CAPTIONOVERLAY_TEST_API_KEY") ?? new SecretStore().Get(SecretStore.ApiKeyName(preset.Id));
        string model = Environment.GetEnvironmentVariable("CAPTIONOVERLAY_TEST_API_MODEL") ?? preset.DefaultModel;
        return string.IsNullOrWhiteSpace(key) ? null : (preset, key, model);
    }

    private static (ApiProviderPreset Preset, string Key, string Model) Require()
    {
        var config = Configured();
        Assert.SkipWhen(config is null,
            "Live API tests are opt-in: set CAPTIONOVERLAY_TEST_API=openai (key from CAPTIONOVERLAY_TEST_API_KEY or saved in the app).");
        return config!.Value;
    }

    /// <summary>Like the app: streaming models (e.g. gpt-realtime-whisper) connect over the Realtime API, others upload each utterance.</summary>
    private static Task<IApiTranscriber> CreateAsync((ApiProviderPreset Preset, string Key, string Model) c, string? key = null, string? language = null) =>
        ApiTranscribers.CreateAsync(new ApiTranscriberOptions
        {
            BaseUrl = BaseUrl(c.Preset),
            Model = c.Model,
            ApiKey = key ?? c.Key,
            ProviderName = c.Preset.Name,
            Language = language,
            StreamingDelay = Environment.GetEnvironmentVariable("CAPTIONOVERLAY_TEST_API_DELAY"),
        }, ct: Ct);

    [Fact]
    public async Task Connection_test_succeeds()
    {
        var c = Require();
        await using var t = await CreateAsync(c, language: "de");
        var latency = await t.TestConnectionAsync(Ct);
        TestContext.Current.SendDiagnosticMessage(Invariant($"{c.Preset.Name} {c.Model}: round trip {latency.TotalMilliseconds:F0} ms"));
        latency.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Wrong_key_is_rejected_as_fatal_without_leaking_it()
    {
        var c = Require();
        var act = async () =>
        {
            await using var t = await CreateAsync(c, key: "sk-invalid-key-for-test");
            await t.TranscribeAsync(new float[16000], new TranscriptionOptions("en", null, false), Ct);
        };
        var ex = (await act.Should().ThrowAsync<TranscriptionException>()).Which;
        ex.IsFatal.Should().BeTrue();
        ex.Message.Should().Contain("API key rejected").And.NotContain("sk-invalid");
    }

    [Fact]
    public async Task Transcribes_english_speech_fixture()
    {
        var c = Require();
        await using var t = await CreateAsync(c, language: "en");
        var r = await t.TranscribeAsync(WavIO.ReadMono16k(Fixtures.Path("speech_en.wav")), new TranscriptionOptions("en", null, false), Ct);
        TestContext.Current.SendDiagnosticMessage(Invariant($"{c.Model} ({r.InferenceTime.TotalMilliseconds:F0} ms): \"{r.Text}\""));
        r.Text.ToLowerInvariant().Should().Contain("weather").And.Contain("umbrella");
    }

    [Fact]
    public async Task German_clips_wer()
    {
        var c = Require();
        await using var t = await CreateAsync(c, language: "de");
        int words = 0, errors = 0;
        var report = new StringBuilder($"{c.Preset.Name} {c.Model} German clips:\n");
        foreach (var clip in GermanFixtures.Clips)
        {
            float[] audio = GermanComposite.ReadClip(GermanFixtures.Folder, clip);
            var r = await t.TranscribeAsync(audio, new TranscriptionOptions("de", null, false), Ct);
            int e = Wer.Errors(clip.Text, r.Text);
            words += Wer.Words(clip.Text).Length;
            errors += e;
            report.Append(CultureInfo.InvariantCulture, $"  {clip.Id}  {e,2} errors  {r.InferenceTime.TotalMilliseconds,5:F0} ms  \"{r.Text}\"\n");
        }
        double wer = (double)errors / words;
        report.Append(Invariant($"  aggregate WER {wer:P1}"));
        TestContext.Current.SendDiagnosticMessage(report.ToString());
        wer.Should().BeLessThanOrEqualTo(0.20, "a hosted Whisper should be at least as good as the local small model (12.5 %)");
    }

    [Fact]
    public async Task Pipeline_with_api_on_german_composite()
    {
        var c = Require();
        var composite = GermanFixtures.Composite;
        bool streaming = OpenAiRealtimeTranscriber.IsStreamingModel(c.Model);
        var stereo = composite.ToStereo48k();
        var lines = new List<CaptionLine>();
        var pipeline = new CaptionPipeline();
        pipeline.LineCommitted += l =>
        {
            lock (lines)
            {
                lines.Add(l);
            }
        };
        var config = new PipelineConfig
        {
            AudioSourceFactory = () => new MemoryAudioSource(stereo, 48000, 2),
            VadFactory = () => new SileroVad(Fixtures.SileroModel),
            Language = "de",
            EnablePartials = streaming,
            Segmenter = streaming ? new SegmenterOptions { PartialIntervalMs = 250 } : new SegmenterOptions(),
        };
        await pipeline.StartAsync(config, async ct => new TranscriberSet(await CreateAsync(c, language: "de")), Ct);
        await pipeline.WaitForSourceCompletionAsync(Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);
        await pipeline.StopAsync(drain: true, TimeSpan.FromMinutes(3));

        string reference = string.Join(' ', composite.Cues.Select(q => q.Text));
        string hypothesis = string.Join(' ', lines.Select(l => l.Text));
        double wer = Wer.Rate(reference, hypothesis);
        TestContext.Current.SendDiagnosticMessage(
            Invariant($"{c.Model} end-to-end: {lines.Count} lines, WER {wer:P1}, status {pipeline.Status}\n") +
            string.Join('\n', lines.Select(l => Invariant($"  [{l.Start.TotalSeconds,6:F2} → {l.End.TotalSeconds,6:F2}] {l.Text}"))));

        pipeline.Status.State.Should().NotBe(PipelineState.Error, pipeline.Status.Message);
        foreach (var u in composite.ExpectedUtterances())
        {
            var first = lines.FirstOrDefault(l => l.End.TotalSeconds > u.SpeechStartSec && l.Start.TotalSeconds < u.SpeechEndSec);
            first.Should().NotBeNull(Invariant($"a committed line for the cue at {u.SpeechStartSec:F2} s"));
            first!.Start.TotalSeconds.Should().BeApproximately(u.SpeechStartSec, 0.5);
        }
        wer.Should().BeLessThanOrEqualTo(0.25);
    }
}
