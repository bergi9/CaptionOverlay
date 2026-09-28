using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CaptionOverlay.Core.Transcription;

namespace CaptionOverlay.Core.Tests.Transcription;

/// <summary>
/// <see cref="WhisperLiveKitTranscriber"/> against a local fake of WhisperLiveKit's <c>/asr</c> WebSocket, modelled on the
/// 0.2.26 source: token check before the upgrade (403), a <c>config</c> message, full-state updates (lines + provisional
/// buffer), and on an empty frame a flushed last update followed by <c>ready_to_stop</c>. The fake "transcribes" the
/// audio as its duration, so the tests can see what was streamed in which session.
/// </summary>
public sealed class WhisperLiveKitTranscriberTests : IAsyncDisposable
{
    private readonly FakeWhisperLiveKit _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static float[] Tone(double seconds) =>
        [.. Enumerable.Range(0, (int)(seconds * 16000)).Select(i => 0.3f * MathF.Sin(i * 0.2f))];

    private Task<WhisperLiveKitTranscriber> ConnectAsync(string key = "wlk-test") =>
        WhisperLiveKitTranscriber.ConnectAsync(new ApiTranscriberOptions
        {
            BaseUrl = _server.BaseUrl,
            Model = "whisper-small",
            ApiKey = key,
            ProviderName = "WhisperLiveKit",
            ProviderId = ApiProviderPreset.WhisperLiveKit.Id,
            Language = "de",
            Timeout = TimeSpan.FromSeconds(5),
        }, ct: Ct);

    private static TranscriptionOptions Partial(Guid id, int? stable = null) => new("de", "ignored prompt", true, id, StableSamples: stable);

    private static TranscriptionOptions Final(Guid id, bool forcedCut = false) => new("de", "ignored prompt", false, id, forcedCut);

    private static double Ms(TranscriptionResult r) => double.Parse(r.Text.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("https://stt.example.org/v1", "de", "wss://stt.example.org/asr?language=de")]
    [InlineData("https://stt.example.org/v1/", null, "wss://stt.example.org/asr")]
    [InlineData("http://localhost:8000", "auto", "ws://localhost:8000/asr")]
    [InlineData("https://example.org/wlk/v1", "en", "wss://example.org/wlk/asr?language=en")]
    public void Stream_endpoint_is_derived_from_the_base_url(string baseUrl, string? language, string expected) =>
        WhisperLiveKitTranscriber.StreamEndpoint(baseUrl, language).ToString().Should().Be(expected);

    [Fact]
    public async Task Partials_stream_new_audio_and_the_final_ends_the_session()
    {
        await using var t = await ConnectAsync();
        var id = Guid.NewGuid();
        float[] audio = Tone(3);

        var first = await t.TranscribeAsync(audio[..16000], Partial(id), Ct);
        await _server.WaitForAsync(() => _server.AudioFrames >= 1);
        var live = await t.TranscribeAsync(audio[..32000], Partial(id), Ct);
        live.Text.Should().NotBeEmpty("the latest update (lines + provisional buffer) is the partial text");
        var final = await t.TranscribeAsync(audio, Final(id), Ct);

        Ms(final).Should().BeApproximately(3000, 1, "each sample is sent once, in one session");
        final.Text.Should().NotContain("…", "the provisional buffer is flushed into the lines at the end");
        _server.Ends.Should().Be(1);
        _server.Authorization.Should().Be("Bearer wlk-test");
        _server.Query.Should().Be("?language=de");
        t.RuntimeDescription.Should().Contain("streaming");
    }

    [Fact]
    public async Task Each_utterance_gets_its_own_session_and_a_spare_is_ready_for_the_next()
    {
        await using var t = await ConnectAsync();
        var a = await t.TranscribeAsync(Tone(1), Final(Guid.NewGuid()), Ct);
        var b = await t.TranscribeAsync(Tone(2), Final(Guid.NewGuid()), Ct);

        Ms(a).Should().BeApproximately(1000, 1);
        Ms(b).Should().BeApproximately(2000, 1, "the second utterance's text must not include the first");
        await _server.WaitForAsync(() => _server.Connections == 3);
        _server.Ends.Should().Be(2);
    }

    [Fact]
    public async Task A_server_without_pcm_input_gets_a_wav_header_first()
    {
        _server.RawPcm = false;
        await using var t = await ConnectAsync();
        var final = await t.TranscribeAsync(Tone(1.5), Final(Guid.NewGuid()), Ct);

        Ms(final).Should().BeApproximately(1500, 1, "the header is not counted as audio");
        _server.SawWavHeader.Should().BeTrue();
    }

    [Fact]
    public async Task Partials_hold_back_the_unstable_tail_and_a_forced_cut_is_resent_in_a_new_session()
    {
        await using var t = await ConnectAsync();
        var id = Guid.NewGuid();
        float[] audio = Tone(4);
        await t.TranscribeAsync(audio, Partial(id, stable: 48000), Ct);
        await _server.WaitForAsync(() => _server.ReceivedMs >= 3000);
        _server.ReceivedMs.Should().BeApproximately(3000, 1, "only the stable part is streamed");

        // The final after a forced cut is shorter than what was streamed: its audio starts over in a fresh session.
        await t.TranscribeAsync(audio[..48000], Partial(id), Ct);
        var final = await t.TranscribeAsync(audio[..40000], Final(id, forcedCut: true), Ct);
        Ms(final).Should().BeApproximately(2500, 1);
    }

    [Fact]
    public async Task Cancelled_utterance_is_closed_and_its_text_does_not_leak_into_the_next()
    {
        await using var t = await ConnectAsync();
        var dropped = Guid.NewGuid();
        await t.TranscribeAsync(Tone(2), Partial(dropped), Ct);
        t.CancelUtterance(dropped);
        await _server.WaitForAsync(() => _server.Closed >= 1);

        var next = await t.TranscribeAsync(Tone(1), Final(Guid.NewGuid()), Ct);
        Ms(next).Should().BeApproximately(1000, 1);
    }

    [Fact]
    public async Task Rejected_key_is_fatal_and_not_echoed()
    {
        var act = () => ConnectAsync(key: "wlk-bad");
        var ex = (await act.Should().ThrowAsync<TranscriptionException>()).Which;
        ex.IsFatal.Should().BeTrue();
        ex.Message.Should().Contain("API key rejected").And.NotContain("wlk-bad");
    }

    [Fact]
    public async Task A_session_dropped_mid_utterance_is_replaced_and_gets_all_audio_again()
    {
        await using var t = await ConnectAsync();
        var id = Guid.NewGuid();
        float[] audio = Tone(2);
        await t.TranscribeAsync(audio[..16000], Partial(id), Ct);
        await _server.WaitForAsync(() => _server.AudioFrames >= 1);
        _server.DropAll();
        await Task.Delay(200, Ct);

        var final = await t.TranscribeAsync(audio, Final(id), Ct);
        Ms(final).Should().BeApproximately(2000, 1, "the new session receives the whole utterance");
    }

    [Fact]
    public void Text_joins_lines_and_the_buffer_and_skips_silence()
    {
        var update = JsonNode.Parse("""
            {"status":"active_transcription","lines":[
              {"speaker":1,"text":"Guten Abend,","start":"0:00:00","end":"0:00:01"},
              {"speaker":-2,"text":"","start":"0:00:01","end":"0:00:02"},
              {"speaker":1,"text":" meine Damen","start":"0:00:02","end":"0:00:03"}],
             "buffer_transcription":" und Herren","buffer_diarization":""}
            """)!.AsObject();
        WhisperLiveKitTranscriber.TextOf(update).Should().Be("Guten Abend, meine Damen und Herren");
    }

    [Fact]
    public async Task Without_a_model_the_first_one_the_server_lists_is_used()
    {
        var options = new ApiTranscriberOptions
        {
            BaseUrl = _server.BaseUrl,
            Model = ApiProviderPreset.WhisperLiveKit.DefaultModel,
            ApiKey = "wlk-test",
            ProviderName = "WhisperLiveKit",
            ProviderId = ApiProviderPreset.WhisperLiveKit.Id,
        };
        options.Model.Should().BeEmpty("self-hosted servers have no fixed default model");
        options.DisplayName.Should().Be("WhisperLiveKit");

        var resolved = await ApiTranscribers.WithServerModelAsync(options, Ct);
        resolved.Model.Should().Be("whisper-small");
        resolved.DisplayName.Should().Be("WhisperLiveKit – whisper-small");
        (await ApiTranscribers.WithServerModelAsync(resolved with { Model = "kept" }, Ct)).Model.Should().Be("kept");
    }

    [Fact]
    public void WhisperLiveKit_is_a_streaming_self_hosted_provider()
    {
        var p = ApiProviderPreset.Find("whisperlivekit");
        p.Should().BeSameAs(ApiProviderPreset.WhisperLiveKit);
        p.Streaming.Should().BeTrue();
        p.SelfHosted.Should().BeTrue();
        ApiProviderPreset.Speaches.DefaultModel.Should().BeEmpty();
        ApiTranscribers.IsStreaming("whisperlivekit", "whisper-small").Should().BeTrue();
        ApiTranscribers.IsStreaming("speaches", "Systran/faster-whisper-small").Should().BeFalse();
        ApiTranscribers.IsStreaming("openai", "gpt-realtime-whisper").Should().BeTrue();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    /// <summary>Minimal WhisperLiveKit /asr: one update per audio frame, text = received duration, flush on an empty frame.</summary>
    private sealed class FakeWhisperLiveKit : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<WebSocket> _sockets = [];
        private int _connections;
        private int _ends;
        private int _frames;
        private int _closed;
        private long _lastSessionBytes;

        public FakeWhisperLiveKit()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        public bool RawPcm { get; set; } = true;

        public string? Authorization { get; private set; }

        public string? Query { get; private set; }

        public bool SawWavHeader { get; private set; }

        public int Connections => Volatile.Read(ref _connections);

        public int Ends => Volatile.Read(ref _ends);

        public int AudioFrames => Volatile.Read(ref _frames);

        public int Closed => Volatile.Read(ref _closed);

        /// <summary>Audio received in the most recent session with audio, in ms.</summary>
        public double ReceivedMs => Interlocked.Read(ref _lastSessionBytes) / 2 / 16.0;

        public void DropAll()
        {
            lock (_sockets)
            {
                foreach (var ws in _sockets)
                {
                    ws.Abort();
                }
                _sockets.Clear();
            }
        }

        public async Task WaitForAsync(Func<bool> condition)
        {
            for (int i = 0; i < 200 && !condition(); i++)
            {
                await Task.Delay(10);
            }
            condition().Should().BeTrue("the fake server should have seen it within 2 s");
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(ctx));
            }
        }

        private async Task ServeAsync(HttpListenerContext ctx)
        {
            Authorization = ctx.Request.Headers["Authorization"];
            Query = ctx.Request.Url?.Query;
            if (ctx.Request.Url?.AbsolutePath == "/v1/models")
            {
                // What /v1/models of WhisperLiveKit 0.2.26 reports: the one model the server runs.
                byte[] body = Encoding.UTF8.GetBytes("""{"object":"list","data":[{"id":"whisper-small","object":"model","owned_by":"whisperlivekit"}]}""");
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
                return;
            }
            if (ctx.Request.Url?.AbsolutePath != "/asr" || !ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            if (Authorization != "Bearer wlk-test")
            {
                ctx.Response.StatusCode = 403; // what Starlette answers when the endpoint closes before accepting
                ctx.Response.Close();
                return;
            }
            Interlocked.Increment(ref _connections);
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            lock (_sockets)
            {
                _sockets.Add(ws);
            }
            long bytes = 0;
            bool raw = RawPcm;
            async Task Send(JsonObject o) => await ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, _cts.Token);
            JsonObject Update(bool flushed)
            {
                string ms = FormattableString.Invariant($"{bytes / 2 / 16.0:F0} ms");
                return new JsonObject
                {
                    ["status"] = "active_transcription",
                    ["lines"] = flushed
                        ? new JsonArray(new JsonObject { ["speaker"] = 1, ["text"] = ms, ["start"] = "0:00:00", ["end"] = "0:00:01" })
                        : new JsonArray(),
                    ["buffer_transcription"] = flushed ? "" : ms + " …",
                    ["buffer_diarization"] = "",
                };
            }

            await Send(new JsonObject { ["type"] = "config", ["useAudioWorklet"] = raw, ["mode"] = "full" });
            var buffer = new byte[1 << 20];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buffer, _cts.Token);
                        message.Write(buffer, 0, r.Count);
                    }
                    while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        Interlocked.Increment(ref _closed);
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", _cts.Token);
                        return;
                    }
                    byte[] data = message.ToArray();
                    if (data.Length == 0)
                    {
                        Interlocked.Increment(ref _ends);
                        await Send(Update(flushed: true));
                        await Send(new JsonObject { ["type"] = "ready_to_stop" });
                        continue;
                    }
                    if (!raw && bytes == 0 && data.Length >= 44 && Encoding.ASCII.GetString(data, 0, 4) == "RIFF"
                        && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24)) == 16000)
                    {
                        SawWavHeader = true;
                        data = data[44..];
                        if (data.Length == 0)
                        {
                            continue;
                        }
                    }
                    bytes += data.Length;
                    Interlocked.Exchange(ref _lastSessionBytes, bytes);
                    Interlocked.Increment(ref _frames);
                    await Send(Update(flushed: false));
                }
            }
            catch (Exception) when (_cts.IsCancellationRequested || ws.State != WebSocketState.Open)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            _listener.Close();
            _cts.Dispose();
        }
    }
}
