using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaptionOverlay.Core.Transcription;

/// <summary>
/// Streaming transcription through a self-hosted WhisperLiveKit server and its own WebSocket (<c>/asr</c>).
/// <para>
/// WhisperLiveKit splits a continuous stream into lines itself. We keep our VAD/segmenter instead (same timestamps, SRT,
/// filter and overlay as every other engine), so each utterance gets its own WebSocket session: partial passes stream
/// the samples not sent yet and return the live text (confirmed lines plus the provisional buffer); the final sends the
/// rest and an empty frame, the server flushes and answers <c>ready_to_stop</c>, and its lines are the transcript.
/// A spare session is opened while an utterance runs, so the next one does not wait for the handshake.
/// Protocol read from the WhisperLiveKit 0.2.26 source (see docs/decisions.md, ADR-028).
/// </para>
/// </summary>
public sealed class WhisperLiveKitTranscriber : IApiTranscriber, IStreamingTranscriber
{
    private const int SampleRate = 16000;
    private const int MaxChunkSamples = SampleRate; // ≤ 1 s of audio per frame
    private static readonly TimeSpan FinalTimeout = TimeSpan.FromSeconds(30);

    private readonly ApiTranscriberOptions _options;
    private readonly ILogger _logger;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1); // one caller at a time drives the open session

    private Session? _open;        // the utterance being streamed (guarded by _gate)
    private Task<Session>? _spare; // pre-opened for the next utterance (guarded by _gate)
    private bool _disposed;

    private WhisperLiveKitTranscriber(ApiTranscriberOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _endpoint = StreamEndpoint(options.BaseUrl, options.Language);
    }

    public string DisplayName => _options.DisplayName;

    public string RuntimeDescription => $"API ({_options.ProviderName}, streaming)";

    /// <summary>Always true: the partial passes are what streams the audio while someone speaks.</summary>
    public bool SupportsPartials => true;

    /// <summary>
    /// <c>https://host/v1</c> → <c>wss://host/asr?language=de</c>. The REST API lives under <c>/v1</c>, the WebSocket at the
    /// root; a path prefix of a reverse proxy (<c>https://host/wlk/v1</c>) is kept.
    /// </summary>
    public static Uri StreamEndpoint(string baseUrl, string? language)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new TranscriptionException(Loc.Format("Api_InvalidBaseUrl", baseUrl)) { IsFatal = true };
        }
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = path + "/asr",
            Query = language is { Length: > 0 } and not "auto" ? "language=" + Uri.EscapeDataString(language) : "",
        };
        return builder.Uri;
    }

    /// <summary>Connects once, so a wrong key or address is reported before listening starts; that session is kept as the spare.</summary>
    public static async Task<WhisperLiveKitTranscriber> ConnectAsync(ApiTranscriberOptions options, ILogger? logger = null, CancellationToken ct = default)
    {
        var t = new WhisperLiveKitTranscriber(options, logger);
        try
        {
            t._spare = Task.FromResult(await t.OpenSessionAsync(ct).ConfigureAwait(false));
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
        var session = await OpenSessionAsync(ct).ConfigureAwait(false);
        var elapsed = sw.Elapsed;
        await session.DisposeAsync().ConfigureAwait(false);
        return elapsed;
    }

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, TranscriptionOptions opts, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Guid utterance = opts.UtteranceId ?? Guid.NewGuid(); // no id (tests, benchmark): a one-off session
        Session session;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // After a forced cut the streamed tail belongs to the next utterance: start over with exactly the final's
            // audio. A session the server closed mid-utterance is replaced and gets everything again.
            bool resend = _open is { } current && current.Utterance == utterance && current.Sent > samples16kMono.Length
                && (opts.ForcedCut || opts.IsPartial);
            if (_open is null || _open.Utterance != utterance || resend || _open.IsClosed)
            {
                Abandon(ref _open);
                _open = await TakeSessionAsync(ct).ConfigureAwait(false);
                _open.Utterance = utterance;
            }
            session = _open;

            // Partial passes send only the stable part: the last seconds of a long utterance may still move to the next
            // one at a forced cut (see OpenAiRealtimeTranscriber).
            int upTo = opts.IsPartial ? Math.Min(opts.StableSamples ?? samples16kMono.Length, samples16kMono.Length) : samples16kMono.Length;
            if (session.Sent < upTo)
            {
                await session.SendAudioAsync(samples16kMono.AsMemory(session.Sent, upTo - session.Sent), ct).ConfigureAwait(false);
                session.Sent = upTo;
            }
            if (opts.IsPartial)
            {
                return new TranscriptionResult(session.Text, null, [], sw.Elapsed);
            }

            await session.SendEndAsync(ct).ConfigureAwait(false);
            _open = null;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            Abandon(ref _open);
            throw new TranscriptionException(Loc.Format("Common_NetworkError", ex.Message), ex) { IsTransient = true };
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            string text = await session.Finished.WaitAsync(FinalTimeout, ct).ConfigureAwait(false);
            _logger.LogDebug("Final for {Id} {Ms} ms after the end of the stream ({Seconds:F2} s of audio)",
                utterance.ToString()[..8], (int)sw.ElapsedMilliseconds, samples16kMono.Length / (double)SampleRate);
            return new TranscriptionResult(text, null, [], sw.Elapsed);
        }
        catch (TimeoutException)
        {
            throw new TranscriptionException(Loc.Get("Api_Timeout")) { IsTransient = true };
        }
        finally
        {
            _ = session.DisposeAsync().AsTask();
        }
    }

    /// <summary>The utterance was discarded (too short, paused): close its session, its text is never used.</summary>
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
            if (_open?.Utterance == utteranceId)
            {
                Abandon(ref _open);
            }
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
            Abandon(ref _open);
            if (_spare is { } spare)
            {
                _spare = null;
                try
                {
                    await (await spare.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
                }
                catch (TranscriptionException)
                {
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ───────────────────────────── Sessions (caller holds _gate) ─────────────────────────────

    private void Abandon(ref Session? session)
    {
        if (session is not null)
        {
            _ = session.DisposeAsync().AsTask();
            session = null;
        }
    }

    /// <summary>The spare if it is (or is about to be) usable, else a new session; then starts the next spare.</summary>
    private async Task<Session> TakeSessionAsync(CancellationToken ct)
    {
        Session? session = null;
        if (_spare is { } spare)
        {
            _spare = null;
            try
            {
                var candidate = await spare.WaitAsync(ct).ConfigureAwait(false);
                if (candidate.IsClosed)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false); // idle too long: the server or a proxy closed it
                }
                else
                {
                    session = candidate;
                }
            }
            catch (TranscriptionException ex)
            {
                _logger.LogDebug("Spare session failed ({Reason}); connecting again", ex.Message);
            }
        }
        session ??= await OpenSessionAsync(ct).ConfigureAwait(false);

        var next = OpenSessionAsync(CancellationToken.None);
        _ = next.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        _spare = next;
        return session;
    }

    private async Task<Session> OpenSessionAsync(CancellationToken ct)
    {
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
            // A wrong token is refused before the upgrade (HTTP 403 from the server, 401 from some proxies).
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

        var session = new Session(socket, _logger);
        try
        {
            // The server first reports whether it takes raw PCM (--pcm-input) or decodes the stream with ffmpeg.
            await session.Configured.WaitAsync(_options.Timeout, ct).ConfigureAwait(false);
            return session;
        }
        catch (TimeoutException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new TranscriptionException(Loc.Get("Api_Timeout")) { IsTransient = true };
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static byte[] StreamingWavHeader()
    {
        var h = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), uint.MaxValue);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(h, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), uint.MaxValue);
        return h;
    }

    /// <summary>Lines (without silence markers) and the provisional buffer as one text.</summary>
    internal static string TextOf(JsonObject update)
    {
        var parts = new List<string>();
        if (update["lines"] is JsonArray lines)
        {
            foreach (var line in lines.OfType<JsonObject>())
            {
                if (line["speaker"]?.GetValue<int>() != -2 && line["text"]?.GetValue<string>() is { Length: > 0 } text)
                {
                    parts.Add(text);
                }
            }
        }
        if (update["buffer_transcription"]?.GetValue<string>() is { Length: > 0 } buffer)
        {
            parts.Add(buffer);
        }
        return string.Join(' ', string.Join(' ', parts).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>One WebSocket session = one utterance.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _receiveLoop;
        private readonly TaskCompletionSource _configured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _rawPcm;
        private bool _headerSent;
        private int _disposed;
        private volatile string _text = "";

        public Session(ClientWebSocket socket, ILogger logger)
        {
            _socket = socket;
            _logger = logger;
            _receiveLoop = Task.Run(ReceiveLoopAsync);
        }

        public Guid? Utterance { get; set; }

        /// <summary>16 kHz samples of the utterance already sent.</summary>
        public int Sent { get; set; }

        public Task Configured => _configured.Task;

        /// <summary>The transcript once the server has flushed the stream (<c>ready_to_stop</c>).</summary>
        public Task<string> Finished => _finished.Task;

        /// <summary>Confirmed lines plus the provisional buffer, as of the latest update.</summary>
        public string Text => _text;

        public bool IsClosed => _receiveLoop.IsCompleted || _socket.State != WebSocketState.Open;

        public async Task SendAudioAsync(ReadOnlyMemory<float> samples, CancellationToken ct)
        {
            if (!_rawPcm && !_headerSent)
            {
                // Without --pcm-input the server pipes the stream through ffmpeg: a WAV header with open-ended sizes
                // tells it the format, the PCM that follows is read until the stream ends.
                await _socket.SendAsync(StreamingWavHeader(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                _headerSent = true;
            }
            for (int offset = 0; offset < samples.Length; offset += MaxChunkSamples)
            {
                var chunk = samples.Span.Slice(offset, Math.Min(MaxChunkSamples, samples.Length - offset));
                var pcm = new byte[chunk.Length * 2];
                for (int i = 0; i < chunk.Length; i++)
                {
                    short v = (short)Math.Clamp(MathF.Round(chunk[i] * short.MaxValue), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), v);
                }
                await _socket.SendAsync(pcm, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }
        }

        /// <summary>An empty frame ends the stream; the server transcribes what is left and then sends ready_to_stop.</summary>
        public Task SendEndAsync(CancellationToken ct) =>
            _socket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, ct).AsTask();

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            TranscriptionException? failure = null;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (_socket.CloseStatus == (WebSocketCloseStatus)4400)
                        {
                            // Rejected session parameters, e.g. a language the server's backend does not know.
                            failure = new TranscriptionException(Loc.Format("Api_Error", 4400, _socket.CloseStatusDescription)) { IsFatal = true };
                        }
                        break;
                    }
                    if (JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) is JsonObject update && Handle(update) is { } error)
                    {
                        failure = error;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is WebSocketException or JsonException or IOException or InvalidOperationException)
            {
                failure = new TranscriptionException(Loc.Format("Common_NetworkError", ex.Message), ex) { IsTransient = true };
            }
            failure ??= new TranscriptionException(Loc.Format("Common_NetworkError", "connection closed")) { IsTransient = true };
            _configured.TrySetException(failure);
            _finished.TrySetException(failure);
        }

        /// <returns>An error that ends the session, or null.</returns>
        private TranscriptionException? Handle(JsonObject update)
        {
            string? type = update["type"]?.GetValue<string>();
            switch (type)
            {
                case "config":
                    _rawPcm = update["useAudioWorklet"]?.GetValue<bool>() == true;
                    _configured.TrySetResult();
                    return null;
                case "ready_to_stop":
                    _finished.TrySetResult(_text);
                    return null;
                case "error":
                    return Error(update["error"]?.ToString());
            }
            if (update["status"]?.GetValue<string>() == "error")
            {
                return Error(update["error"]?.ToString());
            }
            _text = TextOf(update);
            return null;
        }

        private TranscriptionException Error(string? message)
        {
            _logger.LogWarning("WhisperLiveKit error: {Message}", message);
            return new TranscriptionException(Loc.Format("Transcription_Failed", message ?? "unknown error")) { IsTransient = true };
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
            {
            }
            await _cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(_receiveLoop, Task.Delay(2000)).ConfigureAwait(false);
            _socket.Dispose();
            _cts.Dispose();
        }
    }
}
