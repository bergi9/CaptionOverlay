using System.Net;
using System.Text;
using CaptionOverlay.Core.Transcription;

namespace CaptionOverlay.Core.Tests.Transcription;

public class OpenAiCompatibleTranscriberTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<string> Bodies { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            string body = request.Content is null ? "" : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            Bodies.Add(body);
            Requests.Add(request);
            return respond(request, body, Calls);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static OpenAiCompatibleTranscriber Create(StubHandler handler, string key = "sk-test") =>
        new(new ApiTranscriberOptions { BaseUrl = "https://api.example.com/v1/", Model = "whisper-large-v3-turbo", ApiKey = key, ProviderName = "Test" },
            new HttpClient(handler));

    [Fact]
    public async Task Sends_multipart_wav_with_model_language_and_prompt()
    {
        var handler = new StubHandler((_, _, _) => Json(HttpStatusCode.OK,
            """{"text":" Hallo Welt ","language":"german","segments":[{"start":0,"end":1.2,"text":" Hallo Welt","avg_logprob":-0.1}]}"""));
        await using var t = Create(handler);

        var result = await t.TranscribeAsync(new float[16000], new TranscriptionOptions("de", "vorher gesagt", false), TestContext.Current.CancellationToken);

        result.Text.Should().Be("Hallo Welt");
        result.DetectedLanguage.Should().Be("de");
        result.AverageProbability.Should().BeApproximately((float)Math.Exp(-0.1), 1e-4f);
        var request = handler.Requests.Single();
        request.RequestUri!.ToString().Should().Be("https://api.example.com/v1/audio/transcriptions");
        request.Headers.Authorization!.Parameter.Should().Be("sk-test");
        string body = handler.Bodies.Single();
        body.Should().Contain("name=model").And.Contain("whisper-large-v3-turbo");
        body.Should().Contain("name=language").And.Contain("\r\nde\r\n");
        body.Should().Contain("name=prompt").And.Contain("vorher gesagt");
        body.Should().Contain("RIFF").And.Contain("WAVE");
    }

    [Fact]
    public async Task Rejected_key_is_fatal()
    {
        var handler = new StubHandler((_, _, _) => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API Key"}}"""));
        await using var t = Create(handler);
        var act = () => t.TranscribeAsync(new float[16000], new TranscriptionOptions(null, null, false), TestContext.Current.CancellationToken);
        var ex = (await act.Should().ThrowAsync<TranscriptionException>()).Which;
        ex.IsFatal.Should().BeTrue();
        ex.Message.Should().Contain("API key rejected").And.NotContain("sk-test");
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Server_errors_are_retried_once()
    {
        var handler = new StubHandler((_, _, call) => call == 1
            ? Json(HttpStatusCode.BadGateway, "{}")
            : Json(HttpStatusCode.OK, """{"text":"ok"}"""));
        await using var t = Create(handler);
        var result = await t.TranscribeAsync(new float[16000], new TranscriptionOptions(null, null, false), TestContext.Current.CancellationToken);
        result.Text.Should().Be("ok");
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Rate_limit_surfaces_retry_after_without_immediate_retry()
    {
        var handler = new StubHandler((_, _, _) =>
        {
            var r = Json(HttpStatusCode.TooManyRequests, "{}");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        });
        await using var t = Create(handler);
        var act = () => t.TranscribeAsync(new float[16000], new TranscriptionOptions(null, null, false), TestContext.Current.CancellationToken);
        var ex = (await act.Should().ThrowAsync<TranscriptionException>()).Which;
        ex.IsTransient.Should().BeTrue();
        ex.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Falls_back_to_json_when_verbose_json_is_unsupported()
    {
        var handler = new StubHandler((_, body, _) => body.Contains("verbose_json")
            ? Json(HttpStatusCode.BadRequest, """{"error":{"message":"response_format 'verbose_json' is not compatible"}}""")
            : Json(HttpStatusCode.OK, """{"text":"plain"}"""));
        await using var t = Create(handler);
        var result = await t.TranscribeAsync(new float[16000], new TranscriptionOptions(null, null, false), TestContext.Current.CancellationToken);
        result.Text.Should().Be("plain");
    }

    [Fact]
    public async Task Network_errors_are_transient()
    {
        var handler = new StubHandler((_, _, _) => throw new HttpRequestException("no route"));
        await using var t = Create(handler);
        var act = () => t.TranscribeAsync(new float[16000], new TranscriptionOptions(null, null, false), TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<TranscriptionException>()).Which.IsTransient.Should().BeTrue();
        handler.Calls.Should().Be(2, "one retry");
    }

    [Fact]
    public async Task Lists_models_from_the_models_endpoint_with_the_key()
    {
        var handler = new StubHandler((_, _, _) => Json(HttpStatusCode.OK,
            """{"object":"list","data":[{"id":"whisper-1"},{"id":"gpt-4o-mini-transcribe"},{"id":"gpt-realtime-whisper"},{"id":"tts-1"}]}"""));
        await using var t = Create(handler);
        var models = await t.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Select(m => m.Id).Should().Equal("gpt-4o-mini-transcribe", "gpt-realtime-whisper", "tts-1", "whisper-1");
        models.Should().OnlyContain(m => m.Task == null);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.ToString().Should().Be("https://api.example.com/v1/models");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("sk-test");
    }

    [Fact]
    public async Task Speaches_models_are_filtered_by_their_task_not_their_name()
    {
        // Shape of Speaches' GET /v1/models (v0.8.2): speech recognition and text-to-speech models in one list.
        var handler = new StubHandler((_, _, _) => Json(HttpStatusCode.OK,
            """
            {"object":"list","data":[
              {"id":"Systran/faster-whisper-small","created":0,"object":"model","owned_by":"Systran","language":["en","de"],"task":"automatic-speech-recognition"},
              {"id":"Systran/faster-distil-whisper-small.en","created":0,"object":"model","owned_by":"Systran","language":["en"],"task":"automatic-speech-recognition"},
              {"id":"nvidia/parakeet-tdt-0.6b-v3","created":0,"object":"model","owned_by":"nvidia","task":"automatic-speech-recognition"},
              {"id":"speaches-ai/Kokoro-82M-v1.0-ONNX","created":0,"object":"model","owned_by":"speaches-ai","task":"text-to-speech"},
              {"id":"rhasspy/piper-voices-whisper-demo","created":0,"object":"model","owned_by":"rhasspy","task":"text-to-speech"}
            ]}
            """));
        await using var t = Create(handler);
        var models = await t.ListModelsAsync(TestContext.Current.CancellationToken);

        models.Where(ApiTranscribers.IsUsableModel).Select(m => m.Id).Should().Equal(
            "Systran/faster-distil-whisper-small.en", "Systran/faster-whisper-small", "nvidia/parakeet-tdt-0.6b-v3");
    }

    [Fact]
    public void Speaches_is_a_self_hosted_provider_with_a_model_list()
    {
        var speaches = ApiProviderPreset.Find("speaches");
        speaches.Should().BeSameAs(ApiProviderPreset.Speaches);
        speaches.SelfHosted.Should().BeTrue();
        speaches.ListsModels.Should().BeTrue();
        ApiProviderPreset.Custom.ListsModels.Should().BeFalse();
        ApiProviderPreset.OpenAi.SelfHosted.Should().BeFalse();
    }

    [Fact]
    public async Task Model_list_with_a_rejected_key_is_fatal()
    {
        var handler = new StubHandler((_, _, _) => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key"}}"""));
        await using var t = Create(handler);
        var act = () => t.ListModelsAsync(TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<TranscriptionException>()).Which.IsFatal.Should().BeTrue();
    }

    [Theory]
    [InlineData("whisper-1", true)]
    [InlineData("whisper-large-v3-turbo", true)] // Groq
    [InlineData("distil-whisper-large-v3-en", true)]
    [InlineData("gpt-4o-transcribe", true)]
    [InlineData("gpt-4o-mini-transcribe-2025-12-15", true)]
    [InlineData("gpt-transcribe", true)]
    [InlineData("gpt-realtime-whisper", false)] // 404 on /audio/transcriptions (Realtime API only)
    [InlineData("gpt-live-transcribe", false)] // same
    [InlineData("gpt-4o-transcribe-diarize", false)] // rejects prompts
    [InlineData("gpt-4o-mini-tts", false)]
    [InlineData("gpt-5.5", false)]
    public void Only_models_that_work_for_captions_are_offered(string id, bool expected) =>
        OpenAiCompatibleTranscriber.IsTranscriptionModel(id).Should().Be(expected);
}
