using System.Diagnostics;
using System.Text;
using CaptionOverlay.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace CaptionOverlay.Core.Transcription;

public enum GpuPreference
{
    Auto,
    CpuOnly,
}

public sealed record LocalWhisperOptions
{
    public required string ModelPath { get; init; }

    public string DisplayName { get; init; } = "Whisper";

    /// <summary>Language forced by the model (e.g. German fine-tunes). Overrides the per-call language.</summary>
    public string? ForcedLanguage { get; init; }

    public int? Threads { get; init; }

    public bool EnablePartials { get; init; } = true;
}

/// <summary>Local transcription through whisper.cpp (Whisper.net). One model instance, one inference at a time.</summary>
public sealed class LocalWhisperTranscriber : ITranscriber
{
    private const int MinSamples = 16000; // Whisper behaves badly on clips shorter than 1 s.
    private static readonly Lock RuntimeGate = new();
    private static bool _runtimeConfigured;

    private readonly LocalWhisperOptions _options;
    private readonly ILogger _logger;
    private readonly WhisperFactory _factory;
    private readonly SemaphoreSlim _inference = new(1, 1);
    private readonly int _threads;

    private LocalWhisperTranscriber(LocalWhisperOptions options, WhisperFactory factory, ILogger logger, string runtime)
    {
        _options = options;
        _factory = factory;
        _logger = logger;
        _threads = options.Threads ?? Math.Clamp(HardwareInfo.PhysicalCoreCount, 1, 8);
        RuntimeDescription = runtime;
    }

    public string DisplayName => _options.DisplayName;

    public string RuntimeDescription { get; }

    public bool SupportsPartials => _options.EnablePartials;

    /// <summary>
    /// Selects the native runtime order. Must run before the first model is loaded; the native
    /// library cannot be swapped afterwards, so later calls only take effect after an app restart.
    /// </summary>
    public static void ConfigureRuntime(GpuPreference preference)
    {
        lock (RuntimeGate)
        {
            if (_runtimeConfigured)
            {
                return;
            }
            RuntimeOptions.RuntimeLibraryOrder = preference == GpuPreference.CpuOnly
                ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
                : [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
            _runtimeConfigured = true;
        }
    }

    /// <summary>Loads the model (slow: seconds for large models). Throws <see cref="TranscriptionException"/> on invalid files.</summary>
    public static Task<LocalWhisperTranscriber> LoadAsync(
        LocalWhisperOptions options, GpuPreference gpu, ILogger? logger = null, CancellationToken ct = default) =>
        LoadCoreAsync(options, gpu, useGpu: gpu != GpuPreference.CpuOnly, logger, ct);

    private static Task<LocalWhisperTranscriber> LoadCoreAsync(
        LocalWhisperOptions options, GpuPreference runtimePreference, bool useGpu, ILogger? logger, CancellationToken ct)
    {
        logger ??= NullLogger.Instance;
        ConfigureRuntime(runtimePreference);
        return Task.Run(() =>
        {
            if (!File.Exists(options.ModelPath))
            {
                throw new TranscriptionException($"Model file not found: {options.ModelPath}") { IsFatal = true };
            }

            if (!HasGgmlMagic(options.ModelPath))
            {
                throw new TranscriptionException(InvalidModelMessage) { IsFatal = true };
            }

            var sw = Stopwatch.StartNew();
            WhisperFactory factory;
            try
            {
                factory = WhisperFactory.FromPath(options.ModelPath, new WhisperFactoryOptions { UseGpu = useGpu });
                // Loading can be lazy: building a processor forces the model to actually load.
                using var probe = factory.CreateBuilder().Build();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException or BadImageFormatException)
            {
                // The model file exists (checked above), so this is the native whisper.cpp library itself.
                throw new TranscriptionException(
                    "The Whisper runtime (native libraries in the 'runtimes' folder next to the app) could not be loaded. " +
                    "Re-extract the full CaptionOverlay zip.", ex) { IsFatal = true };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new TranscriptionException(InvalidModelMessage, ex) { IsFatal = true };
            }

            string runtime = DescribeRuntime(RuntimeOptions.LoadedLibrary);
            logger.LogInformation("Loaded model {Model} in {Ms} ms using runtime {Runtime}; whisper system info: {Info}",
                Path.GetFileName(options.ModelPath), sw.ElapsedMilliseconds, runtime, SafeRuntimeInfo());
            return new LocalWhisperTranscriber(options, factory, logger, runtime);
        }, ct);
    }

    /// <summary>
    /// Validates that a file loads as a Whisper model (for custom model import). The check runs on the CPU, but
    /// <paramref name="runtimePreference"/> still picks the process-wide native runtime if nothing was loaded yet:
    /// validating with a CPU-only runtime order would keep all later models off the GPU until a restart.
    /// </summary>
    public static async Task ValidateModelAsync(string path, GpuPreference runtimePreference = GpuPreference.Auto, CancellationToken ct = default)
    {
        var t = await LoadCoreAsync(new LocalWhisperOptions { ModelPath = path }, runtimePreference, useGpu: false, null, ct).ConfigureAwait(false);
        await t.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, TranscriptionOptions opts, CancellationToken ct)
    {
        float[] samples = samples16kMono.Length >= MinSamples ? samples16kMono : Pad(samples16kMono, MinSamples);
        string? language = _options.ForcedLanguage ?? opts.Language;

        await _inference.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var builder = _factory.CreateBuilder()
                .WithThreads(_threads)
                .WithNoContext()
                .WithProbabilities();
            builder = string.IsNullOrEmpty(language) || language == "auto"
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(language);
            if (!string.IsNullOrWhiteSpace(opts.Prompt))
            {
                builder = builder.WithPrompt(opts.Prompt);
            }
            if (opts.IsPartial)
            {
                builder = builder.WithSingleSegment();
            }

            await using var processor = builder.Build();
            var segments = new List<TranscribedSegment>();
            var text = new StringBuilder();
            string? detected = null;
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                segments.Add(new TranscribedSegment(segment.Text, segment.Start, segment.End, segment.Probability));
                text.Append(segment.Text);
                detected ??= segment.Language;
            }
            sw.Stop();
            return new TranscriptionResult(text.ToString().Trim(), detected, segments, sw.Elapsed);
        }
        finally
        {
            _inference.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Wait for a running inference before freeing the native model.
        await _inference.WaitAsync().ConfigureAwait(false);
        try
        {
            _factory.Dispose();
        }
        finally
        {
            _inference.Release();
            _inference.Dispose();
        }
    }

    private const string InvalidModelMessage =
        "This file could not be loaded as a Whisper GGML model. Pick a whisper.cpp 'ggml-*.bin' file " +
        "(Transformers .safetensors/.pt files are not supported).";

    /// <summary>whisper.cpp model files start with the uint32 magic 0x67676d6c ("ggml").</summary>
    private static bool HasGgmlMagic(string path)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == 4 && BitConverter.ToUInt32(header) == 0x67676d6c;
    }

    private static float[] Pad(float[] samples, int length)
    {
        var padded = new float[length];
        samples.CopyTo(padded, 0);
        return padded;
    }

    private static string DescribeRuntime(RuntimeLibrary? library) => library switch
    {
        RuntimeLibrary.Cuda => "GPU (CUDA)",
        RuntimeLibrary.Vulkan => "GPU (Vulkan)",
        RuntimeLibrary.Cpu => "CPU",
        RuntimeLibrary.CpuNoAvx => "CPU (no AVX)",
        null => "unknown",
        var other => other.ToString() ?? "unknown",
    };

    private static string SafeRuntimeInfo()
    {
        try
        {
            return WhisperFactory.GetRuntimeInfo() ?? "n/a";
        }
        catch (Exception)
        {
            return "n/a";
        }
    }
}
