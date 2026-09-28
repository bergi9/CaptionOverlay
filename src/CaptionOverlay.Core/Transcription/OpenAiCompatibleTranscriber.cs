using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaptionOverlay.Core.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaptionOverlay.Core.Transcription;

public sealed record ApiTranscriberOptions
{
    public required string BaseUrl { get; init; }

    public required string Model { get; init; }

    public string? ApiKey { get; init; }

    public string ProviderName { get; init; } = "API";

    /// <summary>The <see cref="ApiProviderPreset.Id"/>; decides the protocol for providers that always stream (WhisperLiveKit).</summary>
    public string? ProviderId { get; init; }

    public bool EnablePartials { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Spoken language for streaming sessions (set once per session); null = detect.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// Streaming latency/accuracy trade-off of gpt-live-transcribe ("minimal", "low", "medium", "high", "xhigh"); null = the
    /// server default (best accuracy). Other models ignore it.
    /// </summary>
    public string? StreamingDelay { get; init; }

    /// <summary>Maximum prompt length sent to the API (OpenAI caps prompts at ~224 tokens).</summary>
    public int MaxPromptChars { get; init; } = 200;

    /// <summary>"Provider – model", or just the provider while no model is known.</summary>
    public string DisplayName => Model.Length > 0 ? $"{ProviderName} – {Model}" : ProviderName;
}

/// <summary>
/// POSTs WAV audio to <c>{baseUrl}/audio/transcriptions</c> (OpenAI, Groq, Speaches, self-hosted whisper.cpp server...).
/// Retries once on 5xx/timeouts. Never logs the API key or transcript text.
/// </summary>
public sealed class OpenAiCompatibleTranscriber : IApiTranscriber
{
    private readonly ApiTranscriberOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger _logger;
    private bool _verboseJsonUnsupported;

    public OpenAiCompatibleTranscriber(ApiTranscriberOptions options, HttpClient? httpClient = null, ILogger? logger = null)
    {
        if (!Uri.TryCreate(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new TranscriptionException(Loc.Format("Api_InvalidBaseUrl", options.BaseUrl)) { IsFatal = true };
        }

        _options = options;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeouts below
        _logger = logger ?? NullLogger.Instance;
        Endpoint = new Uri(baseUri, "audio/transcriptions");
    }

    public Uri Endpoint { get; }

    public string DisplayName => _options.DisplayName;

    public string RuntimeDescription => $"API ({_options.ProviderName})";

    public bool SupportsPartials => _options.EnablePartials;

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, TranscriptionOptions opts, CancellationToken ct)
    {
        byte[] wav = WavIO.EncodePcm16(samples16kMono);
        var sw = Stopwatch.StartNew();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var result = await SendAsync(wav, opts, ct).ConfigureAwait(false);
                return result with { InferenceTime = sw.Elapsed };
            }
            catch (TranscriptionException ex) when (ex.IsTransient && ex.RetryAfter is null && attempt == 1 && !ct.IsCancellationRequested)
            {
                var jitter = TimeSpan.FromMilliseconds(250 + Random.Shared.Next(500));
                _logger.LogWarning("API request failed ({Reason}); retrying once in {Delay} ms", ex.Message, (int)jitter.TotalMilliseconds);
                await Task.Delay(jitter, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sends 1 s of silence and reports the round-trip latency. Throws on failure.</summary>
    public async Task<TimeSpan> TestConnectionAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await SendAsync(WavIO.EncodePcm16(new float[16000]), new TranscriptionOptions(null, null, false), ct).ConfigureAwait(false);
        return sw.Elapsed;
    }

    /// <summary>
    /// All models from <c>GET {baseUrl}/models</c>, sorted by id; filter with <see cref="ApiTranscribers.IsUsableModel(ApiModelInfo)"/>.
    /// Throws <see cref="TranscriptionException"/> (fatal for a rejected key) if the list cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<ApiModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, "../models"));
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new TranscriptionException(Loc.Get("Api_KeyRejected")) { IsFatal = true };
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new TranscriptionException(Loc.Format("Api_Error", (int)response.StatusCode, ExtractError(body)));
            }
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new TranscriptionException(Loc.Get("Api_UnexpectedResponse"));
            }
            return [.. data.EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(m => new ApiModelInfo(
                    m.GetProperty("id").GetString()!,
                    m.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.String ? task.GetString() : null))
                .DistinctBy(m => m.Id, StringComparer.Ordinal)
                .OrderBy(m => m.Id, StringComparer.Ordinal)];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranscriptionException(Loc.Get("Api_Timeout")) { IsTransient = true };
        }
        catch (HttpRequestException ex)
        {
            throw new TranscriptionException(Loc.Format("Common_NetworkError", ex.Message), ex) { IsTransient = true };
        }
        catch (JsonException ex)
        {
            throw new TranscriptionException(Loc.Get("Api_UnexpectedResponse"), ex);
        }
    }

    /// <summary>
    /// Whether a listed model can be used for captions via <c>/audio/transcriptions</c>: Whisper models and the
    /// "*-transcribe" models. Probed against OpenAI (2026-09): realtime/live models (<c>gpt-realtime-whisper</c>,
    /// <c>gpt-live-transcribe</c>) only work over the Realtime (WebSocket) API and answer 404 here; diarization models
    /// reject the prompt the pipeline sends; text-to-speech models are not transcribers.
    /// </summary>
    public static bool IsTranscriptionModel(string id)
    {
        string m = id.ToLowerInvariant();
        return (m.Contains("whisper", StringComparison.Ordinal) || m.Contains("transcribe", StringComparison.Ordinal))
            && !m.Contains("realtime", StringComparison.Ordinal)
            && !m.Contains("live", StringComparison.Ordinal)
            && !m.Contains("diarize", StringComparison.Ordinal)
            && !m.Contains("tts", StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<TranscriptionResult> SendAsync(byte[] wav, TranscriptionOptions opts, CancellationToken ct)
    {
        bool verbose = !_verboseJsonUnsupported;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = BuildForm(wav, opts, verbose),
        };
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        HttpResponseMessage response;
        var sw = Stopwatch.StartNew();
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranscriptionException(Loc.Get("Api_Timeout")) { IsTransient = true };
        }
        catch (HttpRequestException ex)
        {
            throw new TranscriptionException(Loc.Format("Common_NetworkError", ex.Message), ex) { IsTransient = true };
        }

        using (response)
        {
            _logger.LogInformation("API {Provider} responded {Status} in {Ms} ms", _options.ProviderName, (int)response.StatusCode, sw.ElapsedMilliseconds);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new TranscriptionException(Loc.Get("Api_KeyRejected")) { IsFatal = true };
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                    ?? TimeSpan.FromSeconds(5);
                throw new TranscriptionException(Loc.Get("Api_RateLimited")) { IsTransient = true, RetryAfter = retryAfter };
            }
            if (response.StatusCode == HttpStatusCode.BadRequest && verbose && body.Contains("response_format", StringComparison.OrdinalIgnoreCase))
            {
                // Some models (e.g. newer GPT-4o transcription models) only support plain json.
                _verboseJsonUnsupported = true;
                return await SendAsync(wav, opts, ct).ConfigureAwait(false);
            }
            if ((int)response.StatusCode >= 500)
            {
                throw new TranscriptionException(Loc.Format("Api_ServerError", (int)response.StatusCode)) { IsTransient = true };
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new TranscriptionException(Loc.Format("Api_Error", (int)response.StatusCode, ExtractError(body))) { IsFatal = response.StatusCode == HttpStatusCode.NotFound };
            }

            return Parse(body);
        }
    }

    private MultipartFormDataContent BuildForm(byte[] wav, TranscriptionOptions opts, bool verbose)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(_options.Model), "model");
        form.Add(new StringContent(verbose ? "verbose_json" : "json"), "response_format");
        if (!string.IsNullOrEmpty(opts.Language) && opts.Language != "auto")
        {
            form.Add(new StringContent(opts.Language), "language");
        }
        if (!string.IsNullOrWhiteSpace(opts.Prompt))
        {
            string prompt = opts.Prompt.Length > _options.MaxPromptChars ? opts.Prompt[^_options.MaxPromptChars..] : opts.Prompt;
            form.Add(new StringContent(prompt), "prompt");
        }
        return form;
    }

    internal static TranscriptionResult Parse(string body)
    {
        ApiResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, ApiJsonContext.Default.ApiResponse);
        }
        catch (JsonException ex)
        {
            throw new TranscriptionException(Loc.Get("Api_UnexpectedResponse"), ex);
        }
        if (parsed is null)
        {
            throw new TranscriptionException(Loc.Get("Api_EmptyResponse"));
        }

        var segments = parsed.Segments?.Select(s => new TranscribedSegment(
            s.Text ?? "",
            TimeSpan.FromSeconds(s.Start),
            TimeSpan.FromSeconds(s.End),
            s.AvgLogprob is { } lp ? (float)Math.Exp(lp) : null)).ToList() ?? [];
        return new TranscriptionResult((parsed.Text ?? "").Trim(), NormalizeLanguage(parsed.Language), segments, TimeSpan.Zero);
    }

    private static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? "";
                }
                if (error.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? "";
                }
            }
        }
        catch (JsonException)
        {
        }
        return body.Length > 200 ? body[..200] : body;
    }

    /// <summary>verbose_json reports language names ("german"); map the common ones to ISO codes.</summary>
    private static string? NormalizeLanguage(string? language) => language?.ToLowerInvariant() switch
    {
        null or "" => null,
        "english" => "en",
        "german" => "de",
        "french" => "fr",
        "spanish" => "es",
        "italian" => "it",
        "dutch" => "nl",
        "portuguese" => "pt",
        "polish" => "pl",
        "russian" => "ru",
        "japanese" => "ja",
        "chinese" => "zh",
        var other => other,
    };

    internal sealed class ApiResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("segments")]
        public List<ApiSegment>? Segments { get; set; }
    }

    internal sealed class ApiSegment
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("avg_logprob")]
        public double? AvgLogprob { get; set; }
    }
}

[JsonSerializable(typeof(OpenAiCompatibleTranscriber.ApiResponse))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;

/// <summary>A model from a provider's <c>/models</c> list. <paramref name="Task"/> is Speaches' extra field ("automatic-speech-recognition", "text-to-speech"); OpenAI and Groq send none.</summary>
public sealed record ApiModelInfo(string Id, string? Task = null);

public sealed record ApiProviderPreset(string Id, string Name, string BaseUrl, string DefaultModel)
{
    public static readonly ApiProviderPreset OpenAi = new("openai", "OpenAI", "https://api.openai.com/v1", "whisper-1");
    public static readonly ApiProviderPreset Groq = new("groq", "Groq", "https://api.groq.com/openai/v1", "whisper-large-v3-turbo");

    /// <summary>
    /// Self-hosted Speaches (faster-whisper). Per-utterance upload, not its Realtime API: that one only transcribes the whole
    /// buffer at the commit (no deltas) after resampling our audio from 24 kHz, see ADR-025. No default model: each server
    /// has its own, so the first one it lists is used until the user picks another.
    /// </summary>
    public static readonly ApiProviderPreset Speaches = new("speaches", "Speaches", "http://localhost:8000/v1", "") { SelfHosted = true };

    /// <summary>
    /// Self-hosted WhisperLiveKit: streams over its own WebSocket (<c>/asr</c>), text appears while someone speaks (ADR-028).
    /// The model is chosen on the server; <c>/v1/models</c> only reports it.
    /// </summary>
    public static readonly ApiProviderPreset WhisperLiveKit = new("whisperlivekit", "WhisperLiveKit", "http://localhost:8000/v1", "")
    {
        SelfHosted = true,
        Streaming = true,
    };

    public static readonly ApiProviderPreset Custom = new("custom", "Custom", "http://localhost:8080/v1", "whisper-1") { SelfHosted = true, ListsModels = false };

    public static IReadOnlyList<ApiProviderPreset> All { get; } = [OpenAi, Groq, Speaches, WhisperLiveKit, Custom];

    /// <summary>The user enters the server address; <see cref="BaseUrl"/> is only the server's default.</summary>
    public bool SelfHosted { get; init; }

    /// <summary>The model is picked from the provider's <c>/models</c> list (ADR-018) instead of typed.</summary>
    public bool ListsModels { get; init; } = true;

    /// <summary>Every model streams (the provider's own protocol), not only OpenAI's realtime models.</summary>
    public bool Streaming { get; init; }

    public static ApiProviderPreset Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Custom;
}

/// <summary>Creates the right API transcriber for a model: streaming (Realtime API) or per-utterance upload.</summary>
public static class ApiTranscribers
{
    /// <summary>Whether a listed model can be offered at all: per-utterance or streaming.</summary>
    public static bool IsUsableModel(string id) =>
        OpenAiCompatibleTranscriber.IsTranscriptionModel(id) || OpenAiRealtimeTranscriber.IsStreamingModel(id);

    /// <summary>A model whose server names its task (Speaches) is usable when it is speech recognition, whatever its id; otherwise by id.</summary>
    public static bool IsUsableModel(ApiModelInfo model) =>
        model.Task is { } task ? task == "automatic-speech-recognition" : IsUsableModel(model.Id);

    /// <summary>
    /// The first model the server offers for captions, for self-hosted providers without a default model (no fixed
    /// default fits every server). Null if the list is empty; throws like <see cref="OpenAiCompatibleTranscriber.ListModelsAsync"/>.
    /// </summary>
    public static async Task<string?> FirstServerModelAsync(ApiTranscriberOptions options, CancellationToken ct)
    {
        var preset = ApiProviderPreset.Find(options.ProviderId);
        await using var list = new OpenAiCompatibleTranscriber(options with { Model = "list" });
        var models = await list.ListModelsAsync(ct).ConfigureAwait(false);
        return models.FirstOrDefault(m => preset.Streaming || IsUsableModel(m))?.Id;
    }

    /// <summary>Options with the server's first model filled in when none is set (self-hosted providers).</summary>
    public static async Task<ApiTranscriberOptions> WithServerModelAsync(ApiTranscriberOptions options, CancellationToken ct) =>
        options.Model.Length == 0 && ApiProviderPreset.Find(options.ProviderId).ListsModels
            && await FirstServerModelAsync(options, ct).ConfigureAwait(false) is { } model
            ? options with { Model = model }
            : options;

    /// <summary>Whether the provider/model streams audio while someone speaks (partials every 250 ms, live text).</summary>
    public static bool IsStreaming(string? providerId, string? model) =>
        ApiProviderPreset.Find(providerId).Streaming || OpenAiRealtimeTranscriber.IsStreamingModel(model);

    /// <summary>Streaming transcribers connect here, so a wrong key or model is reported before listening starts.</summary>
    public static async Task<IApiTranscriber> CreateAsync(ApiTranscriberOptions options, ILogger? logger = null, CancellationToken ct = default)
    {
        if (ApiProviderPreset.Find(options.ProviderId).Streaming)
        {
            return await WhisperLiveKitTranscriber.ConnectAsync(options, logger, ct).ConfigureAwait(false);
        }
        return OpenAiRealtimeTranscriber.IsStreamingModel(options.Model)
            ? await OpenAiRealtimeTranscriber.ConnectAsync(options, logger, ct).ConfigureAwait(false)
            : new OpenAiCompatibleTranscriber(options, logger: logger);
    }
}
