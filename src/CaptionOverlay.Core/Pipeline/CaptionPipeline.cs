using System.Diagnostics;
using System.Threading.Channels;
using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Segmentation;
using CaptionOverlay.Core.Transcription;
using CaptionOverlay.Core.Vad;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaptionOverlay.Core.Pipeline;

/// <summary>
/// Orchestrates capture → resample → VAD → segmenter → scheduler → filter → caption buffer.
/// Threads: capture callback (copy only) → processing task (cheap DSP) → transcription worker.
/// </summary>
public sealed class CaptionPipeline : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly HallucinationFilter _filter;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Stopwatch _session = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TimeSpan> _finalQueuedAt = new();

    private PipelineConfig? _config;
    private CancellationTokenSource? _cts;
    private IAudioSource? _source;
    private IVoiceActivityDetector? _vad;
    private UtteranceSegmenter? _segmenter;
    private AudioFrontEnd? _frontEnd;
    private TranscriptionScheduler? _scheduler;
    private TranscriberSet? _transcribers;
    private Channel<AudioChunk>? _channel;
    private Task? _processing;
    private TaskCompletionSource? _sourceCompleted;
    private bool _vadDisabled;
    private volatile bool _paused;
    private long _lastDataTicks;
    private PipelineMetrics? _lastMetrics;

    public CaptionPipeline(ILoggerFactory? loggerFactory = null, HallucinationFilter? filter = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<CaptionPipeline>();
        _filter = filter ?? new HallucinationFilter();
    }

    public CaptionBuffer Captions { get; } = new();

    public PipelineStatus Status { get; private set; } = PipelineStatus.Idle;

    public bool IsRunning => _cts is not null;

    public bool IsPaused => _paused;

    public TimeSpan SessionTime => _session.Elapsed;

    public string? RuntimeDescription => _transcribers?.Final.RuntimeDescription;

    public string? TranscriberName => _transcribers?.Final.DisplayName;

    public event Action<PipelineStatus>? StatusChanged;

    /// <summary>Raised for every committed caption line (after filtering), on the transcription thread.</summary>
    public event Action<CaptionLine>? LineCommitted;

    public PipelineMetrics Metrics => _scheduler is null
        ? (_lastMetrics ?? PipelineMetrics.Empty) with { SessionTime = _session.Elapsed }
        : new PipelineMetrics(_scheduler.LastRealTimeFactor, _scheduler.LagSeconds, RuntimeDescription, _scheduler.DroppedPartials, _session.Elapsed);

    public async Task StartAsync(PipelineConfig config, TranscriberFactory transcriberFactory, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cts is not null)
            {
                throw new InvalidOperationException("Pipeline already running.");
            }

            _config = config;
            SetStatus(new PipelineStatus(PipelineState.LoadingModel, Loc.Get("Pipeline_LoadingModel")));
            try
            {
                _transcribers = await transcriberFactory(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to create transcriber");
                SetStatus(new PipelineStatus(PipelineState.Error, ex.Message));
                throw;
            }

            _cts = new CancellationTokenSource();
            _vadDisabled = config.VadFactory is null;
            _vad = config.VadFactory?.Invoke() ?? new AlwaysSpeechVad();
            var segmenterOptions = (_vadDisabled ? SegmenterOptions.FixedWindows(config.Segmenter) : config.Segmenter)
                with { EmitPartials = config.EnablePartials };
            _segmenter = new UtteranceSegmenter(segmenterOptions);
            _frontEnd = new AudioFrontEnd(_vad.FrameSize);
            _scheduler = new TranscriptionScheduler(config.Scheduler, _loggerFactory.CreateLogger<TranscriptionScheduler>())
            {
                Language = config.Language,
                PartialsEnabled = config.EnablePartials,
                PromptProvider = () => Captions.GetRecentText(config.PromptChars),
            };
            _scheduler.FinalCompleted += OnFinalCompleted;
            _scheduler.PartialCompleted += OnPartialCompleted;
            _scheduler.JobFailed += OnJobFailed;
            _scheduler.BusyChanged += OnBusyChanged;
            _scheduler.Start(_transcribers);

            _channel = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            _sourceCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _source = config.AudioSourceFactory();
            _source.SamplesAvailable += OnSamples;
            _source.StatusChanged += OnSourceStatus;
            _source.Completed += OnSourceCompleted;

            Captions.Reset();
            _paused = false;
            _session.Restart();
            var token = _cts.Token;
            _processing = Task.Run(() => ProcessAsync(_channel.Reader, _source.IsLive, token), CancellationToken.None);
            await _source.StartAsync(token).ConfigureAwait(false);

            _logger.LogInformation("Pipeline started: transcriber {Name} on {Runtime}, language {Lang}, VAD {Vad}, partials {Partials}",
                _transcribers.Final.DisplayName, _transcribers.Final.RuntimeDescription, config.Language ?? "auto", !_vadDisabled, config.EnablePartials);
            SetStatus(new PipelineStatus(PipelineState.Listening, _transcribers.Final.RuntimeDescription));
        }
        catch
        {
            await TearDownAsync(drain: false).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops capture. With <paramref name="drain"/>, the in-progress utterance and all queued finals are transcribed first.</summary>
    public async Task StopAsync(bool drain = false, TimeSpan? drainTimeout = null)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownAsync(drain, drainTimeout).ConfigureAwait(false);
            SetStatus(PipelineStatus.Idle);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>For finite sources: completes when the source has delivered everything.</summary>
    public Task WaitForSourceCompletionAsync(CancellationToken ct = default) =>
        _sourceCompleted?.Task.WaitAsync(ct) ?? Task.CompletedTask;

    /// <summary>Switches model or mode without restarting capture.</summary>
    public async Task ReloadTranscriberAsync(TranscriberFactory transcriberFactory, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_scheduler is null)
            {
                return;
            }
            var previous = Status;
            SetStatus(new PipelineStatus(PipelineState.LoadingModel, Loc.Get("Pipeline_LoadingModel")));
            TranscriberSet next;
            try
            {
                next = await transcriberFactory(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to load new transcriber; keeping the current one");
                SetStatus(new PipelineStatus(PipelineState.Error, ex.Message));
                throw;
            }

            var old = await _scheduler.SwapTranscribersAsync(next).ConfigureAwait(false);
            _transcribers = next;
            if (old is not null)
            {
                await DisposeTranscribersAsync(old, next).ConfigureAwait(false);
            }
            _logger.LogInformation("Transcriber switched to {Name} on {Runtime}", next.Final.DisplayName, next.Final.RuntimeDescription);
            SetStatus(previous.State == PipelineState.Paused
                ? previous
                : new PipelineStatus(PipelineState.Listening, next.Final.RuntimeDescription));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Pauses captions: audio keeps flowing (timeline continues) but nothing is transcribed.</summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused || _cts is null)
        {
            return;
        }
        _paused = paused;
        SetStatus(paused
            ? new PipelineStatus(PipelineState.Paused, Loc.Get("Pipeline_Paused"))
            : new PipelineStatus(PipelineState.Listening, RuntimeDescription));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private async Task TearDownAsync(bool drain, TimeSpan? drainTimeout = null)
    {
        if (_cts is null && _transcribers is null)
        {
            return;
        }

        if (_source is not null)
        {
            await _source.StopAsync().ConfigureAwait(false);
            _source.SamplesAvailable -= OnSamples;
            _source.StatusChanged -= OnSourceStatus;
            _source.Completed -= OnSourceCompleted;
        }
        _channel?.Writer.TryComplete();

        if (_processing is not null)
        {
            if (!drain && _cts is not null)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            try
            {
                await _processing.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (drain && _scheduler is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(drainTimeout ?? TimeSpan.FromSeconds(30));
                await _scheduler.WaitForFinalsAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timed out waiting for queued finals");
            }
        }

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        if (_scheduler is not null)
        {
            _lastMetrics = Metrics; // keep the final numbers readable after stop
        }
        if (_scheduler is not null)
        {
            _scheduler.FinalCompleted -= OnFinalCompleted;
            _scheduler.PartialCompleted -= OnPartialCompleted;
            _scheduler.JobFailed -= OnJobFailed;
            _scheduler.BusyChanged -= OnBusyChanged;
            await _scheduler.DisposeAsync().ConfigureAwait(false);
        }
        if (_source is not null)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }
        if (_transcribers is not null)
        {
            await DisposeTranscribersAsync(_transcribers, null).ConfigureAwait(false);
        }
        _vad?.Dispose();
        _cts?.Dispose();
        _session.Stop();

        _cts = null;
        _source = null;
        _scheduler = null;
        _transcribers = null;
        _vad = null;
        _segmenter = null;
        _frontEnd = null;
        _processing = null;
        _channel = null;
        _sourceCompleted?.TrySetResult();
    }

    private static async Task DisposeTranscribersAsync(TranscriberSet set, TranscriberSet? keep)
    {
        var keepList = keep is null ? [] : new[] { keep.Final, keep.Partial };
        if (!keepList.Contains(set.Final))
        {
            await set.Final.DisposeAsync().ConfigureAwait(false);
        }
        if (set.Partial is not null && set.Partial != set.Final && !keepList.Contains(set.Partial))
        {
            await set.Partial.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnSamples(AudioChunk chunk)
    {
        // Capture thread: never block.
        Interlocked.Exchange(ref _lastDataTicks, Stopwatch.GetTimestamp());
        _channel?.Writer.TryWrite(chunk);
    }

    private void OnSourceStatus(string message) => _logger.LogInformation("Audio source: {Message}", message);

    private void OnSourceCompleted() => _channel?.Writer.TryComplete();

    private async Task ProcessAsync(ChannelReader<AudioChunk> reader, bool isLive, CancellationToken ct)
    {
        var frontEnd = _frontEnd!;
        // Live sources: the session timeline starts now. Loopback delivers nothing while the PC is silent,
        // so waiting for the first packet would shift every timestamp (SRT export) by the initial silence.
        // Capture start-up latency is well within the silence-injection tolerance.
        if (isLive)
        {
            frontEnd.StartClock(_session.Elapsed);
        }
        Task<bool>? waitTask = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (reader.TryRead(out var chunk))
                {
                    frontEnd.Push(chunk, OnFrame);
                }

                if (isLive
                    && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastDataTicks)) > TimeSpan.FromMilliseconds(100))
                {
                    frontEnd.InjectSilenceIfIdle(_session.Elapsed, OnFrame);
                }

                waitTask ??= reader.WaitToReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(waitTask, Task.Delay(50, ct)).ConfigureAwait(false);
                if (completed == waitTask)
                {
                    bool more = await waitTask.ConfigureAwait(false);
                    waitTask = null;
                    if (!more)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio processing failed");
            SetStatus(new PipelineStatus(PipelineState.Error, Loc.Format("Pipeline_AudioFailed", ex.Message)));
            return;
        }

        // Source finished (or stopped with drain): flush the in-progress utterance.
        frontEnd.Flush(OnFrame);
        foreach (var e in _segmenter!.Flush())
        {
            Dispatch(e);
        }
        _sourceCompleted?.TrySetResult();
    }

    private void OnFrame(float[] frame)
    {
        // While paused the segmenter still sees (silent) frames so its timeline stays in sync with
        // the session clock, but whatever it emits is thrown away.
        bool paused = _paused;
        float probability = paused ? 0f : _vad!.Process(frame);
        foreach (var e in _segmenter!.Process(frame, probability))
        {
            if (paused)
            {
                _scheduler!.CancelUtterance(e.UtteranceId);
                Captions.Discard(e.UtteranceId);
            }
            else
            {
                Dispatch(e);
            }
        }
    }

    private void Dispatch(SegmenterEvent e)
    {
        var scheduler = _scheduler!;
        switch (e)
        {
            case PartialSnapshot partial:
                Captions.SetActiveUtterance(partial.UtteranceId);
                scheduler.OfferPartial(partial);
                break;
            case FinalUtterance final:
                if (_vadDisabled && HallucinationFilter.Rms(final.Samples) < HallucinationFilter.SilentRms)
                {
                    // Fixed-window mode without VAD: skip (near-)digital silence entirely.
                    scheduler.CancelUtterance(final.UtteranceId);
                    Captions.Discard(final.UtteranceId);
                    break;
                }
                _finalQueuedAt[final.UtteranceId] = _session.Elapsed;
                scheduler.EnqueueFinal(final);
                break;
            case UtteranceDiscarded discarded:
                scheduler.CancelUtterance(discarded.UtteranceId);
                Captions.Discard(discarded.UtteranceId);
                break;
        }
    }

    /// <summary>
    /// How long after the end of the utterance its line is committed (italic → normal in the overlay), split into the
    /// segmenter's wait for end-of-speech silence, waiting in the queue and the transcription itself. Meaningful for
    /// live and real-time sources, where the session clock runs with the audio.
    /// </summary>
    private void LogCommitLatency(FinalUtterance final, TranscriptionResult result)
    {
        var now = _session.Elapsed;
        if (!_finalQueuedAt.TryRemove(final.UtteranceId, out var queued))
        {
            return;
        }
        _logger.LogInformation(
            "Commit latency {Id}: {Total} ms after the utterance end (end-of-speech wait {Segmenter} ms, queue {Queue} ms, transcription {Transcription} ms)",
            final.UtteranceId.ToString()[..8], (int)(now - final.End).TotalMilliseconds, (int)(queued - final.End).TotalMilliseconds,
            (int)(now - queued - result.InferenceTime).TotalMilliseconds, (int)result.InferenceTime.TotalMilliseconds);
    }

    private void OnFinalCompleted(FinalUtterance final, TranscriptionResult result)
    {
        LogCommitLatency(final, result);
        var verdict = _filter.Apply(new FilterInput(
            result.Text,
            result.DetectedLanguage,
            Captions.LastCommitted?.Text,
            HallucinationFilter.Rms(final.Samples),
            result.AverageProbability));
        if (!verdict.Keep)
        {
            _logger.LogDebug("Dropped final {Id}: {Reason}", final.UtteranceId.ToString()[..8], verdict.Reason);
        }
        var line = Captions.CommitFinal(final.UtteranceId, verdict.Keep ? verdict.Text : null, final.StartOffset, final.End);
        if (line is not null)
        {
            LineCommitted?.Invoke(line);
        }
        if (Status.State == PipelineState.Error)
        {
            // A successful job clears a transient error (e.g. network recovered).
            SetStatus(new PipelineStatus(_paused ? PipelineState.Paused : PipelineState.Listening, RuntimeDescription));
        }
    }

    private void OnPartialCompleted(PartialSnapshot partial, TranscriptionResult result)
    {
        Captions.ApplyPartial(partial.UtteranceId, partial.Sequence, _filter.CleanPartial(result.Text));
    }

    private void OnJobFailed(SegmenterEvent job, TranscriptionException error, bool willRetry)
    {
        if (job is FinalUtterance && !willRetry)
        {
            Captions.Discard(job.UtteranceId);
        }
        if (job is PartialSnapshot && !error.IsFatal)
        {
            return;
        }
        SetStatus(new PipelineStatus(PipelineState.Error, error.Message));
        if (error.IsFatal)
        {
            // e.g. API key rejected: stop cleanly instead of hammering the API.
            _ = Task.Run(async () =>
            {
                await StopAsync().ConfigureAwait(false);
                SetStatus(new PipelineStatus(PipelineState.Error, error.Message));
            });
        }
    }

    private void OnBusyChanged(bool busy)
    {
        if (Status.State is PipelineState.Listening or PipelineState.Transcribing)
        {
            SetStatus(new PipelineStatus(busy ? PipelineState.Transcribing : PipelineState.Listening, RuntimeDescription));
        }
    }

    private void SetStatus(PipelineStatus status)
    {
        if (Status == status)
        {
            return;
        }
        Status = status;
        StatusChanged?.Invoke(status);
    }
}
