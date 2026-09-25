using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Models;
using CaptionOverlay.Core.Pipeline;
using CaptionOverlay.Core.Transcription;
using CaptionOverlay.Core.Vad;

namespace CaptionOverlay.Core.Tests.Pipeline;

public class CaptionPipelineTests
{
    private static PipelineConfig Config(string wav, bool vad = true, bool partials = true) => new()
    {
        AudioSourceFactory = () => new WavFileAudioSource(Fixtures.Path(wav), realtime: false),
        VadFactory = vad ? () => new SileroVad(Fixtures.SileroModel) : null,
        EnablePartials = partials,
    };

    private static async Task<(CaptionPipeline Pipeline, List<CaptionLine> Lines)> RunAsync(PipelineConfig config, ITranscriber transcriber)
    {
        var pipeline = new CaptionPipeline();
        var lines = new List<CaptionLine>();
        pipeline.LineCommitted += l =>
        {
            lock (lines)
            {
                lines.Add(l);
            }
        };
        await pipeline.StartAsync(config, _ => Task.FromResult(new TranscriberSet(transcriber)), TestContext.Current.CancellationToken);
        await pipeline.WaitForSourceCompletionAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await pipeline.StopAsync(drain: true);
        return (pipeline, lines);
    }

    [Fact]
    public async Task Speech_fixture_produces_one_line_per_sentence_with_timestamps()
    {
        var labels = Fixtures.Labels("speech_en.wav");
        var fake = new FakeTranscriber(text: (s, o) => o.IsPartial ? "…" : $"utterance of {s.Length / 16000.0:F1} seconds");
        var (pipeline, lines) = await RunAsync(Config("speech_en.wav"), fake);

        lines.Should().HaveCount(labels.Count);
        foreach (var (label, line) in labels.Zip(lines))
        {
            line.Start.TotalMilliseconds.Should().BeApproximately(label.StartMs - 300, 250);
            line.End.TotalMilliseconds.Should().BeApproximately(label.EndMs + 200, 250);
        }
        fake.Calls.Should().Contain(c => c.Options.IsPartial, "partials are requested while speaking");
        pipeline.Captions.Tentative.Should().BeNull();
        pipeline.Status.State.Should().Be(PipelineState.Idle);
    }

    [Fact]
    public async Task Silence_produces_no_captions_and_no_transcription()
    {
        var fake = new FakeTranscriber();
        var (_, lines) = await RunAsync(Config("silence_10s.wav"), fake);
        lines.Should().BeEmpty();
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Hallucinations_are_filtered_before_commit()
    {
        var fake = new FakeTranscriber(text: (_, _) => "Untertitel im Auftrag des ZDF, 2021");
        var (pipeline, lines) = await RunAsync(Config("speech_en.wav", partials: false), fake);
        lines.Should().BeEmpty();
        pipeline.Captions.Committed.Should().BeEmpty();
    }

    [Fact]
    public async Task Prompt_carries_previous_committed_text()
    {
        int n = 0;
        var fake = new FakeTranscriber(text: (_, o) => o.IsPartial ? "" : $"Sentence number {++n}.");
        await RunAsync(Config("speech_en.wav", partials: false), fake);
        fake.Calls.Last().Options.Prompt.Should().Contain("Sentence number 1.").And.Contain("Sentence number 2.");
    }

    [Fact]
    public async Task Vad_disabled_uses_fixed_windows_and_skips_digital_silence()
    {
        var fake = new FakeTranscriber();
        var (_, lines) = await RunAsync(Config("monologue_en_30s.wav", vad: false, partials: false), fake);
        lines.Count.Should().BeGreaterThanOrEqualTo(6);
        fake.Calls.Should().OnlyContain(c => c.Samples <= 5 * 16000 + 1);

        var silentFake = new FakeTranscriber();
        await RunAsync(Config("silence_10s.wav", vad: false, partials: false), silentFake);
        silentFake.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Fatal_transcriber_error_stops_the_pipeline_with_a_readable_status()
    {
        var fake = new FakeTranscriber { FailOnCall = _ => new TranscriptionException("API key rejected.") { IsFatal = true } };
        var pipeline = new CaptionPipeline();
        var stopped = new TaskCompletionSource();
        pipeline.StatusChanged += s =>
        {
            if (s.State == PipelineState.Error && !pipeline.IsRunning)
            {
                stopped.TrySetResult();
            }
        };
        var config = Config("speech_en.wav", partials: false) with
        {
            AudioSourceFactory = () => new WavFileAudioSource(Fixtures.Path("speech_en.wav"), realtime: true),
        };
        await pipeline.StartAsync(config, _ => Task.FromResult(new TranscriberSet(fake)), TestContext.Current.CancellationToken);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        pipeline.Status.Message.Should().Be("API key rejected.");
        fake.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Transcriber_can_be_swapped_while_running()
    {
        var first = new FakeTranscriber(text: (_, _) => "first");
        var second = new FakeTranscriber(text: (_, _) => "second");
        var pipeline = new CaptionPipeline();
        var config = Config("speech_en.wav", partials: false) with
        {
            AudioSourceFactory = () => new WavFileAudioSource(Fixtures.Path("speech_en.wav"), realtime: true),
        };
        var lines = new List<string>();
        pipeline.LineCommitted += l => lines.Add(l.Text);
        await pipeline.StartAsync(config, _ => Task.FromResult(new TranscriberSet(first)), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await pipeline.ReloadTranscriberAsync(_ => Task.FromResult(new TranscriberSet(second)), TestContext.Current.CancellationToken);
        first.Disposed.Should().BeTrue();
        await pipeline.WaitForSourceCompletionAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await pipeline.StopAsync(drain: true);
        lines.Should().StartWith("first").And.EndWith("second");
    }
}

/// <summary>End-to-end with a real Whisper model. Runs only when the tiny model is available locally.</summary>
public class LocalWhisperIntegrationTests
{
    internal static string? FindTinyModel()
    {
        string? env = Environment.GetEnvironmentVariable("CAPTIONOVERLAY_TEST_MODEL");
        if (env is not null && File.Exists(env))
        {
            return env;
        }
        var entry = ModelCatalog.LoadBundled().Find("tiny-q5_1")!;
        string path = new ModelStore().GetPath(entry);
        return File.Exists(path) ? path : null;
    }

    [Fact]
    [Trait("Category", "RequiresModel")]
    public async Task Tiny_model_transcribes_speech_fixture()
    {
        string? model = FindTinyModel();
        Assert.SkipWhen(model is null, "tiny-q5_1 model not downloaded (set CAPTIONOVERLAY_TEST_MODEL or download it in the app)");

        await using var transcriber = await LocalWhisperTranscriber.LoadAsync(
            new LocalWhisperOptions { ModelPath = model! }, GpuPreference.Auto, ct: TestContext.Current.CancellationToken);
        var samples = WavIO.ReadMono16k(Fixtures.Path("speech_en.wav"));
        var result = await transcriber.TranscribeAsync(samples, new TranscriptionOptions("en", null, false), TestContext.Current.CancellationToken);

        result.Text.ToLowerInvariant().Should().Contain("weather").And.Contain("umbrella");
        var bench = await ModelAdvisor.BenchmarkAsync(transcriber, Fixtures.Path("speech_en.wav"), TestContext.Current.CancellationToken);
        bench.RealTimeFactor.Should().BeGreaterThan(0);
        TestContext.Current.SendDiagnosticMessage($"Runtime {transcriber.RuntimeDescription}, RTF {bench.RealTimeFactor:F3}");
    }

    [Fact]
    public async Task Invalid_model_file_gives_clear_error()
    {
        string path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "this is not a ggml model", TestContext.Current.CancellationToken);
        try
        {
            var act = () => LocalWhisperTranscriber.ValidateModelAsync(path, TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<TranscriptionException>().WithMessage("*GGML*");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
