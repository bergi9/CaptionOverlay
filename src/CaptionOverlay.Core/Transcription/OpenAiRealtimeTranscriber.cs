using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaptionOverlay.Core.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaptionOverlay.Core.Transcription;

/// <summary>
/// Streaming transcription over OpenAI's Realtime API (<c>wss://…/v1/realtime?intent=transcription</c>) for models
/// such as <c>gpt-realtime-whisper</c> and <c>gpt-live-transcribe</c>, which do not work on <c>/audio/transcriptions</c>.
/// <para>
/// Our VAD/segmenter still decides where utterances start and end (server turn detection is off), so timestamps and
/// SRT output work as for every other engine. Audio is uploaded while someone speaks: each partial snapshot sends only
/// the samples not sent yet (resampled to 24 kHz PCM16) and returns the live text streamed back so far; the final
/// sends the rest, commits the turn and waits for the transcript. One WebSocket session stays open and is re-opened
/// when it drops or nears expiry. Protocol details were probed against the live API (see docs/decisions.md, ADR-019).
/// </para>
/// </summary>
public sealed class OpenAiRealtimeTranscriber : IApiTranscriber, IStreamingTranscriber
{
    private const int WireRate = 24000;
    private const int MaxAppendSamples = WireRate; // ≤ 1 s of audio per append event
    private static readonly TimeSpan FinalTimeout = TimeSpan.FromSeconds(20);

    private readonly ApiTranscriberOptions _options;
    private readonly ILogger _logger;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1); // one caller at a time drives the socket and the open turn
    private readonly Lock _state = new();              // shared with the receive loop

    // Connection (guarded by _gate)
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;
    private DateTimeOffset _expiresAt = DateTimeOffset.MaxValue;
    private TaskCompletionSource<JsonObject>? _sessionReady;

    // Open (uncommitted) turn (guarded by _gate)
    private Guid? _openUtterance;
    private int _sent16k;
    private Resampler? _resampler;

    // Streamed text and pending commits (guarded by _state)
    private readonly Dictionary<string, StringBuilder> _itemText = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closedItems = new(StringComparer.Ordinal); // committed or cleared
    private readonly Queue<PendingTurn> _awaitingCommit = new();
    private readonly Dictionary<string, PendingTurn> _awaitingTranscript = new(StringComparer.Ordinal);
    private string? _openItem;

    private bool _disposed;
    private long _lastCommit;

    private OpenAiRealtimeTranscriber(ApiTranscriberOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _endpoint = RealtimeEndpoint(options.BaseUrl);
    }

    public string DisplayName => $"{_options.ProviderName} – {_options.Model}";

    public string RuntimeDescription => $"API ({_options.ProviderName}, streaming)";

    /// <summary>Always true: the partial passes are what streams the audio while someone speaks.</summary>
    public bool SupportsPartials => true;

    /// <summary>Models that only work over the Realtime API (probed 2026-09: 404 on <c>/audio/transcriptions</c>).</summary>
    public static bool IsStreamingModel(string? id)
    {
        string m = (id ?? "").ToLowerInvariant();
        return m.Contains("realtime-whisper", StringComparison.Ordinal) || m.Contains("live-transcribe", StringComparison.Ordinal);
    }

    /// <summary><c>https://api.openai.com/v1</c> → <c>wss://api.openai.com/v1/realtime?intent=transcription</c>.</summary>
    public static Uri RealtimeEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new TranscriptionException(Loc.Format("Api_InvalidBaseUrl", baseUrl)) { IsFatal = true };
        }
        var builder = new UriBuilder(new Uri(baseUri, "realtime"))
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = "intent=transcription",
        };
        return builder.Uri;
    }

    /// <summary>Connects and configures the session, so a wrong key or model fails here rather than on the first caption.</summary>
    public static async Task<OpenAiRealtimeTranscriber> ConnectAsync(ApiTranscriberOptions options, ILogger? logger = null, CancellationToken ct = default)
    {
        var t = new OpenAiRealtimeTranscriber(options, logger);
        try
        {
            await t._gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await t.EnsureConnectedAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                t._gate.Release();
            }
            return t;
        }
        catch
        {
            await t.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<TimeSpan> TestConnectionAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CloseSocketAsync().ConfigureAwait(false);
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        return sw.Elapsed;
    }

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, TranscriptionOptions opts, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!opts.IsPartial)
        {
            _logger.LogDebug("Final pass for {Id}: {Seconds:F2} s, {Sent:F2} s already streamed", (opts.UtteranceId ?? Guid.Empty).ToString()[..8], samples16kMono.Length / 16000.0, _sent16k / 16000.0);
        }
        Guid utterance = opts.UtteranceId ?? Guid.NewGuid(); // no id (tests, benchmark): a one-off turn
        Task<string> transcript;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            // A final is usually shorter than what was streamed: it ends shortly after the last speech, while the partials
            // already sent the silence that ended it. That silence stays in the turn. Only after a forced cut does the
            // streamed tail belong to the next utterance, so then the turn starts over with exactly the final's audio.
            bool resend = _sent16k > samples16kMono.Length && (opts.ForcedCut || opts.IsPartial);
            if (_openUtterance != utterance || resend)
            {
                await OpenTurnAsync(utterance, ct).ConfigureAwait(false);
            }
            if (_sent16k < samples16kMono.Length)
            {
                await AppendAsync(samples16kMono.AsMemory(_sent16k), ct).ConfigureAwait(false);
                _sent16k = samples16kMono.Length;
            }

            if (opts.IsPartial)
            {
                return new TranscriptionResult(LiveText(), null, [], sw.Elapsed);
            }

            // Push out what the resampler still holds, then commit the turn.
            await AppendAsync(new float[Resampler.TargetSampleRate / 20], ct).ConfigureAwait(false);
            var turn = new PendingTurn(utterance);
            lock (_state)
            {
                _awaitingCommit.Enqueue(turn);
                if (_openItem is not null)
                {
                    _closedItems.Add(_openItem);
                    _openItem = null;
                }
            }
            Volatile.Write(ref _lastCommit, Stopwatch.GetTimestamp());
            await SendAsync(new JsonObject { ["type"] = "input_audio_buffer.commit" }, ct).ConfigureAwait(false);
            _logger.LogDebug("Committed {Id} after {Ms} ms ({Seconds:F2} s of audio)", utterance.ToString()[..8], (int)sw.ElapsedMilliseconds, samples16kMono.Length / 16000.0);
            _openUtterance = null;
            _resampler = null;
            transcript = turn.Result.Task;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            await CloseSocketAsync().ConfigureAwait(false);
            throw new TranscriptionException(Loc.Format("Common_NetworkError", ex.Message), ex) { IsTransient = true };
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            string text = await transcript.WaitAsync(FinalTimeout, ct).ConfigureAwait(false);
            return new TranscriptionResult(text.Trim(), null, [], sw.Elapsed);
        }
        catch (TimeoutException)
        {
            throw new TranscriptionException(Loc.Get("Api_Timeout")) { IsTransient = true };
        }
    }

    /// <summary>The utterance was discarded (too short, paused): drop its streamed audio.</summary>
    public void CancelUtterance(Guid utteranceId) => _ = CancelAsync(utteranceId);

    private async Task CancelAsync(Guid utteranceId)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (!_disposed && _openUtterance == utteranceId && _socket?.State == WebSocketState.Open)
            {
                await ClearAsync(CancellationToken.None).ConfigureAwait(false);
                _openUtterance = null;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Clearing a cancelled utterance failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await CloseSocketAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ───────────────────────────── Turn handling (caller holds _gate) ─────────────────────────────

    private async Task OpenTurnAsync(Guid utterance, CancellationToken ct)
    {
        if (_openUtterance is not null)
        {
            await ClearAsync(ct).ConfigureAwait(false);
        }
        if (DateTimeOffset.UtcNow > _expiresAt - TimeSpan.FromSeconds(30))
        {
            // Sessions expire; switch between utterances rather than in the middle of one.
            await CloseSocketAsync().ConfigureAwait(false);
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
        }
        _openUtterance = utterance;
        _sent16k = 0;
        _resampler = new Resampler(WireRate);
    }

    private async Task ClearAsync(CancellationToken ct)
    {
        lock (_state)
        {
            if (_openItem is not null)
            {
                _closedItems.Add(_openItem); // late deltas of the cleared audio must not reach the next turn
                _openItem = null;
            }
        }
        await SendAsync(new JsonObject { ["type"] = "input_audio_buffer.clear" }, ct).ConfigureAwait(false);
        _openUtterance = null;
    }

    private async Task AppendAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct)
    {
        if (samples16k.IsEmpty)
        {
            return;
        }
        float[] wire = _resampler!.Process(samples16k.Span, Resampler.TargetSampleRate, 1);
        for (int offset = 0; offset < wire.Length; offset += MaxAppendSamples)
        {
            int n = Math.Min(MaxAppendSamples, wire.Length - offset);
            var pcm = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                short v = (short)Math.Clamp(MathF.Round(wire[offset + i] * short.MaxValue), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), v);
            }
            await SendAsync(new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = Convert.ToBase64String(pcm) }, ct).ConfigureAwait(false);
        }
    }

    private string LiveText()
    {
        lock (_state)
        {
            return _openItem is not null && _itemText.TryGetValue(_openItem, out var text) ? text.ToString().Trim() : "";
        }
    }

    // ───────────────────────────── Connection (caller holds _gate) ─────────────────────────────

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_socket?.State == WebSocketState.Open && _receiveLoop is { IsCompleted: false })
        {
            return;
        }
        await CloseSocketAsync().ConfigureAwait(false);

        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + _options.ApiKey);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            await socket.ConnectAsync(_endpoint, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new TranscriptionException(Loc.Get("Api_KeyRejected")) { IsFatal = true };
            }
            if (ex is OperationCanceledException)
            {
                throw new TranscriptionException(Loc.Get("Api_Timeout")) { IsTransient = true };
            }
            throw new TranscriptionException((int)status == 0 ? Loc.Format("Common_NetworkError", ex.Message) : Loc.Format("Api_Error", (int)status, ex.Message), ex)
            {
                IsTransient = (int)status == 0 || (int)status >= 500,
                IsFatal = status is HttpStatusCode.NotFound,
            };
        }

        _socket = socket;
        _receiveCts = new CancellationTokenSource();
        _sessionReady = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_state)
        {
            _itemText.Clear();
            _closedItems.Clear();
            _openItem = null;
        }
        _openUtterance = null;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _receiveCts.Token), CancellationToken.None);

        var transcription = new JsonObject { ["model"] = _options.Model };
        if (_options.Language is { Length: > 0 } language)
        {
            transcription["language"] = language;
        }
        // No prompt: gpt-realtime-whisper rejects it, and a rejected session.update leaves the session on its
        // defaults (server VAD, no transcription model).
        await SendAsync(new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "transcription",
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = WireRate },
                        ["transcription"] = transcription,
                        ["turn_detection"] = null, // our segmenter decides where turns end
                    },
                },
            },
        }, ct).ConfigureAwait(false);

        try
        {
            var session = await _sessionReady.Task.WaitAsync(_options.Timeout, ct).ConfigureAwait(false);
            // Sessions report expires_at (observed: connect time + 60 min); renew between utterances shortly before it.
            // Without a usable value, assume the documented 60-minute limit. A dropped session reconnects anyway.
            var now = DateTimeOffset.UtcNow;
            _expiresAt = session["expires_at"]?.GetValue<long>() is { } expires && DateTimeOffset.FromUnixTimeSeconds(expires) > now.AddMinutes(1)
                ? DateTimeOffset.FromUnixTimeSeconds(expires)
                : now.AddMinutes(55);
            _logger.LogInformation("Realtime transcription session ready ({Model}), expires {Expires:u}", _options.Model, _expiresAt);
        }
        catch (TimeoutException)
        {
            await CloseSocketAsync().ConfigureAwait(false);
            throw new TranscriptionException(Loc.Get("Api_Timeout")) { IsTransient = true };
        }
        catch
        {
            await CloseSocketAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CloseSocketAsync()
    {
        var socket = _socket;
        _socket = null;
        _openUtterance = null;
        _resampler = null;
        _expiresAt = DateTimeOffset.MaxValue;
        if (socket is null)
        {
            return;
        }
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }
        _receiveCts?.Cancel();
        if (_receiveLoop is { } loop)
        {
            await Task.WhenAny(loop, Task.Delay(2000)).ConfigureAwait(false);
        }
        socket.Dispose();
        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveLoop = null;
        FailPending(new TranscriptionException(Loc.Format("Common_NetworkError", "connection closed")) { IsTransient = true });
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _socket!.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    // ───────────────────────────── Receiving ─────────────────────────────

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        TranscriptionException? closedBecause = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Realtime session closed by the server: {Status} {Description}", socket.CloseStatus, socket.CloseStatusDescription);
                    break;
                }
                if (JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) is JsonObject evt)
                {
                    Handle(evt);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or JsonException or IOException)
        {
            _logger.LogWarning(ex, "Realtime session receive failed");
            closedBecause = new TranscriptionException(Loc.Format("Common_NetworkError", ex.Message), ex) { IsTransient = true };
        }
        closedBecause ??= new TranscriptionException(Loc.Format("Common_NetworkError", "connection closed")) { IsTransient = true };
        _sessionReady?.TrySetException(closedBecause);
        FailPending(closedBecause);
    }

    private void Handle(JsonObject evt)
    {
        string? type = evt["type"]?.GetValue<string>();
        string? item = evt["item_id"]?.GetValue<string>();
        switch (type)
        {
            case "session.updated":
                _sessionReady?.TrySetResult(evt["session"] as JsonObject ?? []);
                break;

            case "conversation.item.input_audio_transcription.delta" when item is not null:
                lock (_state)
                {
                    if (_closedItems.Contains(item) && !_awaitingTranscript.ContainsKey(item))
                    {
                        break; // late delta of cleared audio
                    }
                    if (!_closedItems.Contains(item))
                    {
                        _openItem = item; // the newest uncommitted item is the open turn
                    }
                    if (!_itemText.TryGetValue(item, out var text))
                    {
                        _itemText[item] = text = new StringBuilder();
                    }
                    text.Append(evt["delta"]?.GetValue<string>());
                }
                break;

            case "input_audio_buffer.committed" when item is not null:
                _logger.LogDebug("Server committed {Item} {Ms} ms after the commit", item, (int)Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCommit)).TotalMilliseconds);
                lock (_state)
                {
                    _closedItems.Add(item);
                    if (_awaitingCommit.TryDequeue(out var turn))
                    {
                        _awaitingTranscript[item] = turn;
                    }
                }
                break;

            case "conversation.item.input_audio_transcription.completed" when item is not null:
                _logger.LogDebug("Transcript for {Item} {Ms} ms after the commit", item, (int)Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCommit)).TotalMilliseconds);
                Complete(item, turn => turn.Result.TrySetResult(evt["transcript"]?.GetValue<string>() ?? ""));
                break;

            case "conversation.item.input_audio_transcription.failed" when item is not null:
                string failure = evt["error"]?["message"]?.GetValue<string>() ?? "transcription failed";
                Complete(item, turn => turn.Result.TrySetException(
                    new TranscriptionException(Loc.Format("Transcription_Failed", failure)) { IsTransient = true }));
                break;

            case "error":
                HandleError(evt["error"] as JsonObject);
                break;
        }
    }

    private void Complete(string item, Action<PendingTurn> complete)
    {
        PendingTurn? turn;
        lock (_state)
        {
            _awaitingTranscript.Remove(item, out turn);
            _itemText.Remove(item);
        }
        if (turn is not null)
        {
            complete(turn);
        }
    }

    private void HandleError(JsonObject? error)
    {
        string code = error?["code"]?.GetValue<string>() ?? "";
        string message = error?["message"]?.GetValue<string>() ?? "unknown error";
        // A wrong key does not fail the WebSocket handshake: the server accepts, then sends invalid_api_key (whose
        // message echoes part of the key, so it is neither logged nor shown).
        bool keyRejected = code is "invalid_api_key" or "unauthorized" or "insufficient_permissions";
        _logger.LogWarning("Realtime API error {Code}: {Message}", code, keyRejected ? "(key rejected)" : message);
        var fatal = keyRejected
            ? new TranscriptionException(Loc.Get("Api_KeyRejected")) { IsFatal = true }
            : new TranscriptionException(Loc.Format("Api_Error", code, message)) { IsFatal = true };
        if (_sessionReady is { Task.IsCompleted: false } ready)
        {
            // The session configuration was rejected (unknown model, unsupported field): nothing will be transcribed.
            ready.TrySetException(fatal);
            return;
        }
        if (keyRejected)
        {
            FailPending(fatal);
            return;
        }
        if (code == "input_audio_buffer_commit_empty")
        {
            // Less than 100 ms reached the server: an empty result, the caption is simply dropped.
            PendingTurn? turn;
            lock (_state)
            {
                _awaitingCommit.TryDequeue(out turn);
            }
            turn?.Result.TrySetResult("");
        }
    }

    private void FailPending(TranscriptionException error)
    {
        List<PendingTurn> turns;
        lock (_state)
        {
            turns = [.. _awaitingCommit, .. _awaitingTranscript.Values];
            _awaitingCommit.Clear();
            _awaitingTranscript.Clear();
        }
        foreach (var turn in turns)
        {
            turn.Result.TrySetException(error);
        }
    }

    private sealed record PendingTurn(Guid Utterance)
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
