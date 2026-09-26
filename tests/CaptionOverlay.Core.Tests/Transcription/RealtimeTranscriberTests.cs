using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CaptionOverlay.Core.Transcription;

namespace CaptionOverlay.Core.Tests.Transcription;

/// <summary>
/// <see cref="OpenAiRealtimeTranscriber"/> against a local fake of the Realtime transcription protocol, modelled on the
/// events observed from the live API (deltas carry the open item's id, commit → committed → completed, clear → cleared).
/// The fake "transcribes" a turn as the duration of the audio it received, so the tests can see what was streamed.
/// </summary>
public sealed class RealtimeTranscriberTests : IAsyncDisposable
{
    private readonly FakeRealtimeServer _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static float[] Tone(double seconds) =>
        [.. Enumerable.Range(0, (int)(seconds * 16000)).Select(i => 0.3f * MathF.Sin(i * 0.2f))];

    private Task<OpenAiRealtimeTranscriber> ConnectAsync(string model = "gpt-realtime-whisper", string key = "sk-test") =>
        OpenAiRealtimeTranscriber.ConnectAsync(new ApiTranscriberOptions
        {
            BaseUrl = _server.BaseUrl,
            Model = model,
            ApiKey = key,
            ProviderName = "OpenAI",
            Language = "de",
            Timeout = TimeSpan.FromSeconds(5),
        }, ct: Ct);

    private static TranscriptionOptions Partial(Guid id) => new("de", "ignored prompt", true, id);

    private static TranscriptionOptions Final(Guid id) => new("de", "ignored prompt", false, id);

    /// <summary>The fake reports "&lt;ms&gt; ms"; the final adds 50 ms of padding that flushes the resampler.</summary>
    private static double Ms(TranscriptionResult r) => double.Parse(r.Text.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Session_is_configured_for_transcription_with_our_turns_and_no_prompt()
    {
        await using var t = await ConnectAsync();
        var update = _server.Received.First(e => (string?)e["type"] == "session.update");
        var input = update["session"]!["audio"]!["input"]!;
        ((string?)update["session"]!["type"]).Should().Be("transcription");
        ((int?)input["format"]!["rate"]).Should().Be(24000);
        ((string?)input["transcription"]!["model"]).Should().Be("gpt-realtime-whisper");
        ((string?)input["transcription"]!["language"]).Should().Be("de");
        input["transcription"]!["prompt"].Should().BeNull("gpt-realtime-whisper rejects prompts");
        input.AsObject().ContainsKey("turn_detection").Should().BeTrue();
        input["turn_detection"].Should().BeNull("the segmenter decides where turns end");
        _server.Authorization.Should().Be("Bearer sk-test");
        t.RuntimeDescription.Should().Contain("streaming");
    }

    [Fact]
    public async Task Partials_stream_only_new_audio_and_the_final_commits_the_turn()
    {
        await using var t = await ConnectAsync();
        var id = Guid.NewGuid();
        float[] audio = Tone(3);

        await t.TranscribeAsync(audio[..16000], Partial(id), Ct);
        await _server.WaitForAsync(() => _server.Deltas >= 1);
        var live = await t.TranscribeAsync(audio[..32000], Partial(id), Ct);
        live.Text.Should().NotBeEmpty("deltas streamed for the open turn are returned as the partial text");
        var final = await t.TranscribeAsync(audio, Final(id), Ct);

        Ms(final).Should().BeApproximately(3050, 30, "each sample is sent once, plus the 50 ms flush");
        _server.Commits.Should().Be(1);
        _server.Clears.Should().Be(0);
    }

    [Fact]
    public async Task Final_shorter_than_the_streamed_audio_keeps_the_trailing_silence_already_sent()
    {
        await using var t = await ConnectAsync();
        var id = Guid.NewGuid();
        float[] audio = Tone(3);
        await t.TranscribeAsync(audio, Partial(id), Ct);

        // The segmenter ends the final shortly after the last speech; the partials already streamed the silence after it.
        var final = await t.TranscribeAsync(audio[..32000], Final(id), Ct);

        _server.Clears.Should().Be(0, "resending would bill the audio twice and delay the transcript");
        Ms(final).Should().BeApproximately(3050, 30);
    }

    [Fact]
    public async Task Partials_hold_back_the_unstable_tail_so_a_forced_cut_needs_no_resend()
    {
        await using var t = await ConnectAsync();
        var id = Guid.NewGuid();
        float[] audio = Tone(3);
        // Only the first 1.5 s can no longer move to the next utterance.
        await t.TranscribeAsync(audio, Partial(id) with { StableSamples = 24000 }, Ct);
        await _server.WaitForAsync(() => _server.Deltas >= 1);

        var final = await t.TranscribeAsync(audio[..32000], Final(id) with { ForcedCut = true }, Ct);

        _server.Clears.Should().Be(0, "nothing beyond the cut was streamed");
        Ms(final).Should().BeApproximately(2050, 30);
    }

    [Fact]
    public async Task Forced_cut_shorter_than_the_streamed_audio_is_resent()
    {
        await using var t = await ConnectAsync();
        var id = Guid.NewGuid();
        float[] audio = Tone(3);
        await t.TranscribeAsync(audio, Partial(id), Ct);

        var final = await t.TranscribeAsync(audio[..32000], Final(id) with { ForcedCut = true }, Ct);

        _server.Clears.Should().Be(1, "the streamed tail belongs to the next utterance");
        Ms(final).Should().BeApproximately(2050, 30);
    }

    [Fact]
    public async Task Cancelled_utterance_is_cleared_and_its_text_does_not_leak_into_the_next()
    {
        await using var t = await ConnectAsync();
        var discarded = Guid.NewGuid();
        await t.TranscribeAsync(Tone(1), Partial(discarded), Ct);
        await _server.WaitForAsync(() => _server.Deltas >= 1);
        t.CancelUtterance(discarded);
        await _server.WaitForAsync(() => _server.Clears == 1);

        var next = Guid.NewGuid();
        await t.TranscribeAsync(Tone(0.5), Partial(next), Ct);
        await _server.WaitForAsync(() => _server.Deltas >= 2);
        await Task.Delay(50, Ct); // let the receive loop handle the delta
        var live = await t.TranscribeAsync(Tone(0.75), Partial(next), Ct);
        live.Text.Should().Contain("item_1").And.NotContain("item_0", "the cleared turn's words must not show up in the next turn");
        var final = await t.TranscribeAsync(Tone(1), Final(next), Ct);
        Ms(final).Should().BeApproximately(1050, 30);
    }

    [Fact]
    public async Task Consecutive_utterances_get_their_own_transcripts()
    {
        await using var t = await ConnectAsync();
        var a = await t.TranscribeAsync(Tone(1), Final(Guid.NewGuid()), Ct);
        var b = await t.TranscribeAsync(Tone(2), Final(Guid.NewGuid()), Ct);
        var c = await t.TranscribeAsync(Tone(0.5), new TranscriptionOptions("de", null, false), Ct); // no id: one-off turn
        Ms(a).Should().BeApproximately(1050, 30);
        Ms(b).Should().BeApproximately(2050, 30);
        Ms(c).Should().BeApproximately(550, 30);
    }

    [Fact]
    public async Task Rejected_session_settings_are_fatal()
    {
        var act = () => ConnectAsync(model: "reject-me");
        var ex = (await act.Should().ThrowAsync<TranscriptionException>()).Which;
        ex.IsFatal.Should().BeTrue();
        ex.Message.Should().Contain("not supported");
    }

    [Fact]
    public async Task Rejected_key_is_fatal_and_not_echoed()
    {
        var act = () => ConnectAsync(key: "sk-bad");
        var ex = (await act.Should().ThrowAsync<TranscriptionException>()).Which;
        ex.IsFatal.Should().BeTrue();
        ex.Message.Should().Contain("API key rejected").And.NotContain("sk-bad");
    }

    [Fact]
    public async Task Dropped_connection_fails_the_turn_as_transient_and_the_next_call_reconnects()
    {
        await using var t = await ConnectAsync();
        _server.DropOnNextCommit = true;
        var act = () => t.TranscribeAsync(Tone(1), Final(Guid.NewGuid()), Ct);
        (await act.Should().ThrowAsync<TranscriptionException>()).Which.IsTransient.Should().BeTrue();

        var retry = await t.TranscribeAsync(Tone(1), Final(Guid.NewGuid()), Ct);
        Ms(retry).Should().BeApproximately(1050, 30);
        _server.Connections.Should().Be(2);
    }

    [Fact]
    public async Task Key_rejected_after_the_handshake_is_fatal_and_not_echoed()
    {
        var act = () => ConnectAsync(key: "sk-late-reject");
        var ex = (await act.Should().ThrowAsync<TranscriptionException>()).Which;
        ex.IsFatal.Should().BeTrue();
        ex.Message.Should().Contain("API key rejected").And.NotContain("sk-late");
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "wss://api.openai.com/v1/realtime?intent=transcription")]
    [InlineData("https://api.openai.com/v1/", "wss://api.openai.com/v1/realtime?intent=transcription")]
    [InlineData("http://localhost:8080/v1", "ws://localhost:8080/v1/realtime?intent=transcription")]
    public void Realtime_endpoint_is_derived_from_the_base_url(string baseUrl, string expected) =>
        OpenAiRealtimeTranscriber.RealtimeEndpoint(baseUrl).ToString().Should().Be(expected);

    [Theory]
    [InlineData("gpt-realtime-whisper", true)]
    [InlineData("gpt-live-transcribe", true)]
    [InlineData("whisper-1", false)]
    [InlineData("gpt-4o-transcribe", false)]
    [InlineData("gpt-realtime", false)] // speech-to-speech, not a transcription model
    [InlineData("gpt-realtime-translate", false)]
    public void Streaming_models_are_recognized(string id, bool expected)
    {
        OpenAiRealtimeTranscriber.IsStreamingModel(id).Should().Be(expected);
        ApiTranscribers.IsUsableModel(id).Should().Be(expected || OpenAiCompatibleTranscriber.IsTranscriptionModel(id));
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    /// <summary>Minimal Realtime transcription server: one item per turn, a delta per append, transcript = received duration.</summary>
    private sealed class FakeRealtimeServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private int _connections;
        private int _commits;
        private int _clears;
        private int _deltas;

        public FakeRealtimeServer()
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

        public ConcurrentQueue<JsonObject> Received { get; } = new();

        public string? Authorization { get; private set; }

        public int Connections => Volatile.Read(ref _connections);

        public int Commits => Volatile.Read(ref _commits);

        public int Clears => Volatile.Read(ref _clears);

        public int Deltas => Volatile.Read(ref _deltas);

        public bool DropOnNextCommit { get; set; }

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
            if (ctx.Request.Url?.AbsolutePath != "/v1/realtime" || ctx.Request.QueryString["intent"] != "transcription" || !ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            if (Authorization is not ("Bearer sk-test" or "Bearer sk-late-reject"))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Close();
                return;
            }
            Interlocked.Increment(ref _connections);
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            int item = 0;
            long bytes = 0;
            async Task Send(JsonObject o) => await ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, _cts.Token);

            await Send(new JsonObject { ["type"] = "session.created", ["session"] = new JsonObject() });
            if (Authorization == "Bearer sk-late-reject")
            {
                // What the live API does: accept the WebSocket, then report the key as an error event (echoing part of it).
                await Send(new JsonObject
                {
                    ["type"] = "error",
                    ["error"] = new JsonObject { ["code"] = "invalid_api_key", ["message"] = "Incorrect API key provided: sk-late***ject." },
                });
            }
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
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", _cts.Token);
                        return;
                    }
                    var evt = (JsonObject)JsonNode.Parse(message.ToArray())!;
                    Received.Enqueue(evt);
                    string itemId = $"item_{item}";
                    switch ((string?)evt["type"])
                    {
                        case "session.update":
                            if ((string?)evt["session"]!["audio"]!["input"]!["transcription"]!["model"] == "reject-me")
                            {
                                await Send(new JsonObject
                                {
                                    ["type"] = "error",
                                    ["error"] = new JsonObject { ["code"] = "invalid_value", ["message"] = "The model is not supported." },
                                });
                            }
                            else
                            {
                                await Send(new JsonObject
                                {
                                    ["type"] = "session.updated",
                                    ["session"] = new JsonObject { ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
                                });
                            }
                            break;
                        case "input_audio_buffer.append":
                            bytes += Convert.FromBase64String((string)evt["audio"]!).Length;
                            Interlocked.Increment(ref _deltas);
                            await Send(new JsonObject { ["type"] = "conversation.item.input_audio_transcription.delta", ["item_id"] = itemId, ["delta"] = $" {itemId}" });
                            break;
                        case "input_audio_buffer.clear":
                            Interlocked.Increment(ref _clears);
                            bytes = 0;
                            item++;
                            await Send(new JsonObject { ["type"] = "input_audio_buffer.cleared" });
                            break;
                        case "input_audio_buffer.commit":
                            Interlocked.Increment(ref _commits);
                            if (DropOnNextCommit)
                            {
                                DropOnNextCommit = false;
                                ws.Abort();
                                return;
                            }
                            double ms = bytes / 2 / 24.0;
                            if (ms < 100)
                            {
                                await Send(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["code"] = "input_audio_buffer_commit_empty", ["message"] = "buffer too small" } });
                                break;
                            }
                            await Send(new JsonObject { ["type"] = "input_audio_buffer.committed", ["item_id"] = itemId });
                            await Send(new JsonObject
                            {
                                ["type"] = "conversation.item.input_audio_transcription.completed",
                                ["item_id"] = itemId,
                                ["transcript"] = FormattableString.Invariant($"{ms:F0} ms"),
                            });
                            bytes = 0;
                            item++;
                            break;
                    }
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
