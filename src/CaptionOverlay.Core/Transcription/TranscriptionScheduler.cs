using CaptionOverlay.Core.Segmentation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaptionOverlay.Core.Transcription;

public sealed record SchedulerOptions
{
    /// <summary>Minimum time between the starts of two partial passes (API mode: 1.5 s to limit cost).</summary>
    public TimeSpan MinPartialInterval { get; init; } = TimeSpan.Zero;

    /// <summary>How often a final is retried after transient errors before it is given up.</summary>
    public int MaxFinalAttempts { get; init; } = 4;
}

public sealed record TranscriberSet(ITranscriber Final, ITranscriber? Partial = null)
{
    public ITranscriber PartialOrFinal => Partial ?? Final;
}

/// <summary>
/// Single worker that serializes inference. Finals are queued and never dropped (except after
/// repeated transient failures); partials are latest-wins and dropped when inference lags.
/// </summary>
public sealed class TranscriptionScheduler : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly SchedulerOptions _options;
    private readonly Lock _gate = new();
    private readonly LinkedList<(FinalUtterance Utterance, int Attempts)> _finals = new();
    private readonly HashSet<Guid> _finalizedIds = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _transcriberGate = new(1, 1);
    private PartialSnapshot? _pendingPartial;
    private FinalUtterance? _runningFinal;
    private int _runningAttempts;
    private bool _busy;
    private TranscriberSet? _transcribers;
    private DateTime _lastPartialStart = DateTime.MinValue;
    private DateTime _backoffUntil = DateTime.MinValue;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private TaskCompletionSource _idle = NewIdleTcs(completed: true);

    public TranscriptionScheduler(SchedulerOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new SchedulerOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    public event Action<FinalUtterance, TranscriptionResult>? FinalCompleted;

    public event Action<PartialSnapshot, TranscriptionResult>? PartialCompleted;

    /// <summary>A job failed. For finals, <c>willRetry</c> tells whether it stays queued.</summary>
    public event Action<SegmenterEvent, TranscriptionException, bool>? JobFailed;

    public event Action<bool>? BusyChanged;

    /// <summary>Language passed to every job (null/"auto" = detect).</summary>
    public string? Language { get; set; }

    /// <summary>Provides context text (recent committed captions) for each job.</summary>
    public Func<string?>? PromptProvider { get; set; }

    public bool PartialsEnabled { get; set; } = true;

    public int DroppedPartials { get; private set; }

    public double? LastRealTimeFactor { get; private set; }

    public string? RuntimeDescription => _transcribers?.Final.RuntimeDescription;

    /// <summary>Seconds of final audio waiting (including the one being transcribed).</summary>
    public double LagSeconds
    {
        get
        {
            lock (_gate)
            {
                double queued = _finals.Sum(f => f.Utterance.Duration.TotalSeconds);
                return queued + (_runningFinal?.Duration.TotalSeconds ?? 0);
            }
        }
    }

    public int QueuedFinals
    {
        get
        {
            lock (_gate)
            {
                return _finals.Count;
            }
        }
    }

    public void Start(TranscriberSet transcribers)
    {
        if (_worker is not null)
        {
            throw new InvalidOperationException("Already started.");
        }
        _transcribers = transcribers;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _worker = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    /// <summary>Replaces the transcribers after any running inference completes. Returns the old set (caller disposes).</summary>
    public async Task<TranscriberSet?> SwapTranscribersAsync(TranscriberSet transcribers)
    {
        await _transcriberGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var old = _transcribers;
            _transcribers = transcribers;
            _signal.Release();
            return old;
        }
        finally
        {
            _transcriberGate.Release();
        }
    }

    public void EnqueueFinal(FinalUtterance utterance)
    {
        lock (_gate)
        {
            _finals.AddLast((utterance, 0));
            if (_finalizedIds.Count > 1000)
            {
                _finalizedIds.Clear(); // only recent ids matter for late partials
            }
            _finalizedIds.Add(utterance.UtteranceId);
            if (_pendingPartial?.UtteranceId == utterance.UtteranceId)
            {
                _pendingPartial = null;
                DroppedPartials++;
            }
            MarkBusyLocked();
        }
        _signal.Release();
    }

    public void OfferPartial(PartialSnapshot partial)
    {
        if (!PartialsEnabled)
        {
            return;
        }
        lock (_gate)
        {
            if (_finalizedIds.Contains(partial.UtteranceId))
            {
                return;
            }
            if (_pendingPartial is not null)
            {
                DroppedPartials++;
            }
            _pendingPartial = partial;
            MarkBusyLocked();
        }
        _signal.Release();
    }

    /// <summary>Forgets a pending partial for a discarded utterance (and tells a streaming transcriber).</summary>
    public void CancelUtterance(Guid utteranceId)
    {
        lock (_gate)
        {
            _finalizedIds.Add(utteranceId);
            if (_pendingPartial?.UtteranceId == utteranceId)
            {
                _pendingPartial = null;
            }
        }
        // A streaming transcriber may already hold audio of this utterance.
        (_transcribers?.PartialOrFinal as IStreamingTranscriber)?.CancelUtterance(utteranceId);
    }

    /// <summary>Completes when no final is queued or running (partials are ignored).</summary>
    public Task WaitForFinalsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return _idle.Task.WaitAsync(ct);
        }
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cts.Dispose();
        _cts = null;
        _worker = null;
        lock (_gate)
        {
            _finals.Clear();
            _pendingPartial = null;
            _runningFinal = null;
            _finalizedIds.Clear();
            _idle.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _signal.Dispose();
        _transcriberGate.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (job, waitHint) = TakeNext();
            if (job is null)
            {
                SetBusy(false);
                if (waitHint is { } hint)
                {
                    await _signal.WaitAsync(hint, ct).ConfigureAwait(false);
                }
                else
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                }
                continue;
            }

            SetBusy(true);
            await _transcriberGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (job is FinalUtterance final)
                {
                    await RunFinalAsync(final, ct).ConfigureAwait(false);
                }
                else if (job is PartialSnapshot partial)
                {
                    await RunPartialAsync(partial, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _transcriberGate.Release();
            }
        }
    }

    private (SegmenterEvent? Job, TimeSpan? WaitHint) TakeNext()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_finals.Count > 0)
            {
                if (now < _backoffUntil)
                {
                    return (null, _backoffUntil - now);
                }
                var first = _finals.First!.Value;
                _finals.RemoveFirst();
                _runningFinal = first.Utterance;
                _runningAttempts = first.Attempts;
                return (first.Utterance, null);
            }

            // No final queued or running: anyone waiting for finals can continue.
            _idle.TrySetResult();

            if (_pendingPartial is { } partial && PartialsEnabled)
            {
                var due = _lastPartialStart + _options.MinPartialInterval;
                if (now < due)
                {
                    return (null, due - now);
                }
                _pendingPartial = null;
                _lastPartialStart = now;
                return (partial, null);
            }

            return (null, null);
        }
    }

    private async Task RunFinalAsync(FinalUtterance final, CancellationToken ct)
    {
        var transcriber = _transcribers!.Final;
        try
        {
            var result = await transcriber.TranscribeAsync(final.Samples, Options(final), ct).ConfigureAwait(false);
            double audioSeconds = Math.Max(final.Duration.TotalSeconds, 0.001);
            LastRealTimeFactor = result.InferenceTime.TotalSeconds / audioSeconds;
            _logger.LogInformation("Final {Id}: {Audio:F2} s audio in {Ms} ms (RTF {Rtf:F2})",
                final.UtteranceId.ToString()[..8], audioSeconds, (int)result.InferenceTime.TotalMilliseconds, LastRealTimeFactor);
            lock (_gate)
            {
                _runningFinal = null;
            }
            FinalCompleted?.Invoke(final, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var tex = ex as TranscriptionException ?? new TranscriptionException(Loc.Format("Transcription_Failed", ex.Message), ex) { IsTransient = false };
            int attempts = _runningAttempts + 1;
            bool retry = tex.IsTransient && !tex.IsFatal && attempts < _options.MaxFinalAttempts;
            lock (_gate)
            {
                _runningFinal = null;
                if (retry)
                {
                    // Keep ordering: the failed final goes back to the front.
                    _finals.AddFirst((final, attempts));
                    var backoff = tex.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempts)));
                    _backoffUntil = DateTime.UtcNow + backoff;
                }
            }
            _logger.LogWarning(ex, "Final transcription failed (attempt {Attempt}, retry: {Retry})", attempts, retry);
            JobFailed?.Invoke(final, tex, retry);
        }
    }

    private async Task RunPartialAsync(PartialSnapshot partial, CancellationToken ct)
    {
        var transcriber = _transcribers!.PartialOrFinal;
        if (!transcriber.SupportsPartials)
        {
            return;
        }
        try
        {
            var result = await transcriber.TranscribeAsync(partial.Samples, Options(partial), ct).ConfigureAwait(false);
            PartialCompleted?.Invoke(partial, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var tex = ex as TranscriptionException ?? new TranscriptionException(Loc.Format("Transcription_Failed", ex.Message), ex);
            if (tex.RetryAfter is { } retryAfter)
            {
                lock (_gate)
                {
                    _backoffUntil = DateTime.UtcNow + retryAfter;
                }
            }
            _logger.LogDebug(ex, "Partial transcription failed");
            JobFailed?.Invoke(partial, tex, false);
        }
    }

    private TranscriptionOptions Options(SegmenterEvent job) =>
        new(Language, PromptProvider?.Invoke(), job is PartialSnapshot, job.UtteranceId, job is FinalUtterance { IsForcedCut: true },
            (job as PartialSnapshot)?.StableSamples);

    private void MarkBusyLocked()
    {
        if (_idle.Task.IsCompleted && _finals.Count > 0)
        {
            _idle = NewIdleTcs(completed: false);
        }
    }

    private void SetBusy(bool busy)
    {
        if (_busy == busy)
        {
            return;
        }
        _busy = busy;
        BusyChanged?.Invoke(busy);
    }

    private static TaskCompletionSource NewIdleTcs(bool completed)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            tcs.SetResult();
        }
        return tcs;
    }
}
