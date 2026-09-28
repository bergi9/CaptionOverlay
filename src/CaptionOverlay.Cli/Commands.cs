using System.Diagnostics;
using CaptionOverlay.Cli.Fixtures;
using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Diagnostics;
using CaptionOverlay.Core.Models;
using CaptionOverlay.Core.Pipeline;
using CaptionOverlay.Core.Segmentation;
using CaptionOverlay.Core.Transcription;
using CaptionOverlay.Core.Vad;
using Microsoft.Extensions.Logging;

namespace CaptionOverlay.Cli;

public static class Commands
{
    public static void PrintHelp() => Console.WriteLine("""
        captionoverlay-cli — developer harness for the CaptionOverlay pipeline

        Commands:
          devices                               List output (render) devices
          record <out.wav> [--seconds 10] [--device <id>]
                                                Record system audio (loopback) to a 16 kHz mono WAV
          segment <in.wav> [--no-vad]           Print utterance boundaries found by VAD + segmenter
          live [--model <id|path>] [--lang de] [--wav <file> [--realtime]] [--partial-model <id|path>]
               [--api groq|openai|speaches|custom --api-key <key> --api-model <m> --api-url <url>]
               [--no-vad] [--no-partials] [--cpu] [--srt <file>] [--normal-priority]
                                                Live captions from loopback (or a WAV file) to the console
          bench --model <id|path> [--cpu]       Benchmark a model on the bundled fixture (real-time factor)
          models                                List catalog models and what is installed
          download <id>                         Download a catalog model (resumable, SHA-256 verified)
          hardware                              Show detected hardware and the recommended model
          api-models --api openai|groq|speaches|custom [--api-key <key>] [--api-url <url>] [--all]
                                                List the provider's transcription models (--all: every model)
          fixtures fetch-de [--out tests/fixtures/de] [--revision <hash>] [--force] [--write-composite <path.wav>]
                                                Download the German FLEURS test clips (pinned revision)

        Options: --verbose (debug logging to stderr). API key may also come from CAPTIONOVERLAY_API_KEY or the key saved in the app.
        """);

    public static Task<int> RunAsync(CliArgs args, ILoggerFactory loggers, CancellationToken ct) => args.Command switch
    {
        "devices" => Task.FromResult(Devices()),
        "record" => RecordAsync(args, loggers, ct),
        "segment" => Task.FromResult(Segment(args)),
        "live" => LiveAsync(args, loggers, ct),
        "bench" => BenchAsync(args, loggers, ct),
        "models" => Task.FromResult(Models()),
        "download" => DownloadAsync(args, loggers, ct),
        "hardware" => Task.FromResult(Hardware()),
        "api-models" => ApiModelsAsync(args, ct),
        "fixtures" => FixturesAsync(args, ct),
        _ =>throw new CliException($"unknown command '{args.Command}' (try --help)"),
    };

    private static int Devices()
    {
        foreach (var d in WasapiLoopbackSource.GetOutputDevices())
        {
            Console.WriteLine($"{(d.IsDefault ? "*" : " ")} {d.Name}\n    {d.Id}");
        }
        return 0;
    }

    private static async Task<int> RecordAsync(CliArgs args, ILoggerFactory loggers, CancellationToken ct)
    {
        string output = args.RequirePositional(0, "output WAV path");
        double seconds = args.GetDouble("seconds", 10);
        var frontEnd = new AudioFrontEnd(512);
        var samples = new List<float>();
        var gate = new Lock();
        var clock = Stopwatch.StartNew();
        bool clockStarted = false;
        long lastData = 0;

        await using var source = new WasapiLoopbackSource(args.Get("device"), loggers.CreateLogger<WasapiLoopbackSource>());
        source.StatusChanged += m => Console.Error.WriteLine(m);
        source.SamplesAvailable += chunk =>
        {
            lock (gate)
            {
                if (!clockStarted)
                {
                    frontEnd.StartClock(clock.Elapsed - chunk.Duration);
                    clockStarted = true;
                }
                frontEnd.Push(chunk, samples.AddRange);
                lastData = clock.ElapsedTicks;
            }
        };
        await source.StartAsync(ct);
        Console.Error.WriteLine($"Recording {seconds:F0} s of system audio… (Ctrl+C to stop early)");
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(50, ct);
                lock (gate)
                {
                    // Nothing playing → loopback is silent; keep the timeline moving.
                    if (!clockStarted)
                    {
                        frontEnd.StartClock(TimeSpan.Zero);
                        clockStarted = true;
                    }
                    if (TimeSpan.FromTicks(clock.ElapsedTicks - lastData) > TimeSpan.FromMilliseconds(100))
                    {
                        frontEnd.InjectSilenceIfIdle(clock.Elapsed, samples.AddRange);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        await source.StopAsync();

        float[] result;
        lock (gate)
        {
            frontEnd.Flush(samples.AddRange);
            int wanted = (int)(Math.Min(seconds, clock.Elapsed.TotalSeconds) * Resampler.TargetSampleRate);
            result = samples.Count >= wanted ? samples.GetRange(0, wanted).ToArray() : [.. samples, .. new float[wanted - samples.Count]];
        }
        WavIO.WritePcm16(output, result);
        Console.Error.WriteLine($"Wrote {result.Length / 16000.0:F2} s (16 kHz mono PCM16) to {output}; peak {result.DefaultIfEmpty().Max(Math.Abs):F3}");
        return 0;
    }

    private static int Segment(CliArgs args)
    {
        string input = args.RequirePositional(0, "input WAV path");
        float[] samples = WavIO.ReadMono16k(input);
        bool vadEnabled = !args.Has("no-vad");
        using IVoiceActivityDetector vad = vadEnabled ? new SileroVad(SileroVad.DefaultModelPath) : new AlwaysSpeechVad();
        var options = new SegmenterOptions { EmitPartials = false };
        var segmenter = new UtteranceSegmenter(vadEnabled ? options : SegmenterOptions.FixedWindows(options));
        var frontEnd = new AudioFrontEnd(vad.FrameSize);
        int n = 0;
        void Print(SegmenterEvent e)
        {
            switch (e)
            {
                case FinalUtterance f:
                    Console.WriteLine($"#{++n,-3} {f.StartOffset:hh\\:mm\\:ss\\.fff} → {f.End:hh\\:mm\\:ss\\.fff}  ({f.Duration.TotalSeconds:F2} s, rms {HallucinationFilter.Rms(f.Samples):F3})");
                    break;
                case UtteranceDiscarded:
                    Console.WriteLine("     (discarded: too short)");
                    break;
            }
        }
        void OnFrame(float[] frame)
        {
            foreach (var e in segmenter.Process(frame, vad.Process(frame)))
            {
                Print(e);
            }
        }
        frontEnd.Push(new AudioChunk(samples, 16000, 1), OnFrame);
        frontEnd.Flush(OnFrame);
        foreach (var e in segmenter.Flush())
        {
            Print(e);
        }
        Console.WriteLine($"{n} utterance(s) in {samples.Length / 16000.0:F2} s of audio");
        return 0;
    }

    /// <summary>--api-key, CAPTIONOVERLAY_API_KEY, or the key saved in the app (Settings → API, DPAPI-encrypted for this user).</summary>
    private static string? ApiKey(CliArgs args, ApiProviderPreset preset) =>
        args.Get("api-key") ?? Environment.GetEnvironmentVariable("CAPTIONOVERLAY_API_KEY")
            ?? new Core.Settings.SecretStore().Get(Core.Settings.SecretStore.ApiKeyName(preset.Id));

    private static Task<IApiTranscriber> CreateApiTranscriberAsync(CliArgs args, bool partials, string? language, ILogger? logger, CancellationToken ct)
    {
        var preset = ApiProviderPreset.Find(args.Get("api"));
        return ApiTranscribers.CreateAsync(new ApiTranscriberOptions
        {
            BaseUrl = args.Get("api-url") ?? preset.BaseUrl,
            Model = args.Get("api-model") ?? preset.DefaultModel,
            ApiKey = ApiKey(args, preset),
            ProviderName = preset.Name,
            EnablePartials = partials,
            Language = language,
        }, logger, ct);
    }

    private static async Task<int> ApiModelsAsync(CliArgs args, CancellationToken ct)
    {
        await using var t = new OpenAiCompatibleTranscriber(new ApiTranscriberOptions
        {
            BaseUrl = args.Get("api-url") ?? ApiProviderPreset.Find(args.Get("api")).BaseUrl,
            Model = "list",
            ApiKey = ApiKey(args, ApiProviderPreset.Find(args.Get("api"))),
        });
        var models = await t.ListModelsAsync(ct);
        foreach (var m in models.Where(m => args.Has("all") || ApiTranscribers.IsUsableModel(m)))
        {
            Console.WriteLine(OpenAiRealtimeTranscriber.IsStreamingModel(m.Id) ? $"{m.Id}  (streaming)"
                : args.Has("all") && ApiTranscribers.IsUsableModel(m) ? $"{m.Id}  (transcription)" : m.Id);
        }
        return 0;
    }

    private static async Task<int> LiveAsync(CliArgs args, ILoggerFactory loggers, CancellationToken ct)
    {
        string? language = args.Get("lang");
        string? wav = args.Get("wav");
        var gpu = args.Has("cpu") ? GpuPreference.CpuOnly : GpuPreference.Auto;
        bool api = args.Has("api");
        bool partials = !args.Has("no-partials");
        bool streaming = api && OpenAiRealtimeTranscriber.IsStreamingModel(args.Get("api-model"));
        var catalog = ModelCatalog.LoadBundled();
        var store = new ModelStore();

        TranscriberFactory factory = async token =>
        {
            if (api)
            {
                return new TranscriberSet(await CreateApiTranscriberAsync(args, partials, language, loggers.CreateLogger("ApiTranscriber"), token));
            }

            // Like the app: local inference yields to other programs unless asked otherwise (for comparisons).
            ProcessPriority.SetBackground(!args.Has("normal-priority"), loggers.CreateLogger("Priority"));
            var main = ResolveModel(args.Get("model") ?? "tiny-q5_1", catalog, store);
            var final = await LocalWhisperTranscriber.LoadAsync(
                new LocalWhisperOptions { ModelPath = main.Path, DisplayName = main.DisplayName, ForcedLanguage = main.ForceLanguage, EnablePartials = partials },
                gpu, loggers.CreateLogger<LocalWhisperTranscriber>(), token);
            ITranscriber? partial = null;
            if (args.Get("partial-model") is { } partialId)
            {
                var pm = ResolveModel(partialId, catalog, store);
                partial = await LocalWhisperTranscriber.LoadAsync(
                    new LocalWhisperOptions { ModelPath = pm.Path, DisplayName = pm.DisplayName, ForcedLanguage = main.ForceLanguage ?? pm.ForceLanguage },
                    gpu, loggers.CreateLogger<LocalWhisperTranscriber>(), token);
            }
            return new TranscriberSet(final, partial);
        };

        var config = new PipelineConfig
        {
            AudioSourceFactory = wav is null
                ? () => new WasapiLoopbackSource(args.Get("device"), loggers.CreateLogger<WasapiLoopbackSource>())
                : () => new WavFileAudioSource(wav, realtime: args.Has("realtime")),
            VadFactory = args.Has("no-vad") ? null : () => new SileroVad(SileroVad.DefaultModelPath),
            Language = language,
            // Streaming models need the partial passes: they carry the audio upload.
            EnablePartials = partials || streaming,
            Segmenter = streaming ? new SegmenterOptions { PartialIntervalMs = 250 } : new SegmenterOptions(),
            Scheduler = new SchedulerOptions { MinPartialInterval = api && !streaming ? TimeSpan.FromSeconds(1.5) : TimeSpan.Zero },
        };

        await using var pipeline = new CaptionPipeline(loggers);
        var console = new ConsoleCaptionView();
        Core.Export.SrtWriter? srt = args.Get("srt") is { } srtPath ? new Core.Export.SrtWriter(srtPath) : null;
        pipeline.Captions.Changed += (_, e) => console.Render(pipeline.Captions, e);
        pipeline.LineCommitted += line => srt?.Append(line);
        // Listening <-> Transcribing flips on every job; only surface the interesting states.
        pipeline.StatusChanged += s =>
        {
            if (s.State is not (PipelineState.Listening or PipelineState.Transcribing))
            {
                console.Status(s.ToString());
            }
        };

        await pipeline.StartAsync(config, factory, ct);
        Console.Error.WriteLine(wav is null ? "Listening to system audio… (Ctrl+C to stop)" : $"Transcribing {wav}…");
        using var metricsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var metricsTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var metricsTask = Task.Run(async () =>
        {
            while (await metricsTimer.WaitForNextTickAsync(metricsCts.Token))
            {
                var m = pipeline.Metrics;
                console.Status($"metrics: runtime {m.Runtime}, RTF {m.RealTimeFactor:F2}, lag {m.LagSeconds:F1} s, dropped partials {m.DroppedPartials}" +
                    (m.IsLagging ? " — lagging: consider a smaller model or API mode" : ""));
            }
        }, CancellationToken.None);

        try
        {
            if (wav is not null)
            {
                await pipeline.WaitForSourceCompletionAsync(ct);
            }
            else
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        await pipeline.StopAsync(drain: true, TimeSpan.FromSeconds(60));
        var metrics = pipeline.Metrics;
        await metricsCts.CancelAsync();
        srt?.Dispose();
        Console.Error.WriteLine($"Done. {pipeline.Captions.Committed.Count} line(s); last RTF {metrics.RealTimeFactor:F2}; runtime {metrics.Runtime ?? "n/a"}");
        try
        {
            await metricsTask;
        }
        catch (OperationCanceledException)
        {
        }
        return pipeline.Status.State == PipelineState.Error ? 1 : 0;
    }

    private static async Task<int> BenchAsync(CliArgs args, ILoggerFactory loggers, CancellationToken ct)
    {
        var model = ResolveModel(args.Get("model") ?? throw new CliException("--model is required"), ModelCatalog.LoadBundled(), new ModelStore());
        var sw = Stopwatch.StartNew();
        await using var t = await LocalWhisperTranscriber.LoadAsync(
            new LocalWhisperOptions { ModelPath = model.Path, DisplayName = model.DisplayName, ForcedLanguage = model.ForceLanguage },
            args.Has("cpu") ? GpuPreference.CpuOnly : GpuPreference.Auto, loggers.CreateLogger<LocalWhisperTranscriber>(), ct);
        Console.WriteLine($"Loaded {model.DisplayName} in {sw.ElapsedMilliseconds} ms on {t.RuntimeDescription}");
        var result = await ModelAdvisor.BenchmarkAsync(t, ct: ct);
        Console.WriteLine($"Audio {result.AudioDuration.TotalSeconds:F1} s, inference {result.InferenceTime.TotalMilliseconds:F0} ms, RTF {result.RealTimeFactor:F3} → {result.RatingText}");
        Console.WriteLine($"Text: {result.Text}");
        return 0;
    }

    private static int Models()
    {
        var catalog = ModelCatalog.LoadBundled();
        var store = new ModelStore();
        foreach (var m in catalog.Models)
        {
            string state = store.IsInstalled(m) ? "installed" : store.PartialBytes(m) > 0 ? $"partial {store.PartialBytes(m) * 100 / m.SizeBytes}%" : "";
            Console.WriteLine($"{m.Id,-28} {m.SizeBytes / 1_000_000,6} MB  {string.Join(',', m.Languages),-6} {state}");
        }
        foreach (var c in store.GetCustomModels())
        {
            Console.WriteLine($"{c.Id,-28} custom: {c.Path}");
        }
        Console.WriteLine($"Models folder: {store.Root}");
        return 0;
    }

    private static async Task<int> DownloadAsync(CliArgs args, ILoggerFactory loggers, CancellationToken ct)
    {
        string id = args.RequirePositional(0, "model id");
        using var http = new HttpClient();
        var catalog = await ModelCatalog.LoadAsync(http, logger: loggers.CreateLogger("catalog"), ct: ct);
        var entry = catalog.Find(id) ?? throw new CliException($"unknown model '{id}' (see 'models')");
        var store = new ModelStore();
        if (store.IsInstalled(entry))
        {
            Console.WriteLine("Already installed.");
            return 0;
        }
        using var downloader = new ModelDownloader(http, store, loggers.CreateLogger<ModelDownloader>());
        var progress = new Progress<DownloadProgress>(p =>
            Console.Error.Write($"\r{p.State,-11} {p.Fraction,6:P1}  {p.BytesDownloaded / 1_000_000,5}/{p.TotalBytes / 1_000_000} MB  {p.BytesPerSecond / 1_000_000,6:F1} MB/s  ETA {p.Eta:mm\\:ss}   "));
        await downloader.DownloadAsync(entry, progress, ct);
        Console.Error.WriteLine();
        Console.WriteLine($"Installed to {store.GetPath(entry)}");
        return 0;
    }

    private static int Hardware()
    {
        var hw = HardwareInfo.Query();
        Console.WriteLine(hw);
        foreach (var lang in new[] { "en", "de" })
        {
            var rec = ModelAdvisor.Recommend(hw, lang);
            Console.WriteLine($"[{lang}] recommended: {rec.ModelId}{(rec.SuggestApi ? " (or API mode)" : "")} — {rec.Reason}");
        }
        return 0;
    }

    private static async Task<int> FixturesAsync(CliArgs args, CancellationToken ct)
    {
        string sub = args.RequirePositional(0, "fixtures subcommand (fetch-de)");
        if (sub != "fetch-de")
        {
            throw new CliException($"unknown fixtures subcommand '{sub}' (expected fetch-de)");
        }
        string output = args.Get("out") ?? Path.Combine(FindRepoRoot(), "tests", "fixtures", "de");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var fetcher = new GermanFixtureFetcher(http, Console.Error);
        return await fetcher.RunAsync(output, args.Get("revision") ?? GermanFixtureFetcher.DefaultRevision, args.Has("force"), args.Get("write-composite"), ct);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CaptionOverlay.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new CliException("run from inside the repository or pass --out <folder>");
    }

    private static ResolvedModel ResolveModel(string idOrPath, ModelCatalog catalog, ModelStore store)
    {
        if (File.Exists(idOrPath))
        {
            return new ResolvedModel("file", Path.GetFileNameWithoutExtension(idOrPath), Path.GetFullPath(idOrPath), null, true);
        }
        return store.Resolve(idOrPath, catalog)
            ?? throw new CliException($"model '{idOrPath}' is not installed (run: download {idOrPath}) and is not a file path");
    }
}

/// <summary>Prints committed captions as lines and the tentative one as an overwritable status line.</summary>
internal sealed class ConsoleCaptionView
{
    private readonly Lock _gate = new();
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private int _tentativeLength;

    public void Render(CaptionBuffer buffer, CaptionChangedEventArgs e)
    {
        lock (_gate)
        {
            ClearTentative();
            if (e.CommittedLine is { } line)
            {
                Console.WriteLine($"[{line.Start:hh\\:mm\\:ss\\.f} → {line.End:hh\\:mm\\:ss\\.f}] {line.Text}");
            }
            if (_interactive && buffer.Tentative is { } t)
            {
                string text = "  … " + t.Text;
                int width = Math.Max(20, Console.WindowWidth - 1);
                if (text.Length > width)
                {
                    text = "  …" + text[^(width - 3)..];
                }
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(text);
                Console.ResetColor();
                _tentativeLength = text.Length;
            }
        }
    }

    public void Status(string message)
    {
        lock (_gate)
        {
            ClearTentative();
            Console.Error.WriteLine($"[{message}]");
        }
    }

    private void ClearTentative()
    {
        if (_tentativeLength > 0)
        {
            Console.Write("\r" + new string(' ', _tentativeLength) + "\r");
            _tentativeLength = 0;
        }
    }
}
