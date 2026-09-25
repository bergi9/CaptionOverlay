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

    public bool EnablePartials { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum prompt length sent to the API (OpenAI caps prompts at ~224 tokens).</summary>
    public int MaxPromptChars { get; init; } = 200;
}

/// <summary>
/// POSTs WAV audio to <c>{baseUrl}/audio/transcriptions</c> (OpenAI, Groq, self-hosted whisper.cpp server...).
/// Retries once on 5xx/timeouts. Never logs the API key or transcript text.
/// </summary>
public sealed class OpenAiCompatibleTranscriber : ITranscriber
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
            throw new TranscriptionException($"Invalid API base URL: {options.BaseUrl}") { IsFatal = true };
        }

        _options = options;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeouts below
        _logger = logger ?? NullLogger.Instance;
        Endpoint = new Uri(baseUri, "audio/transcriptions");
    }

    public Uri Endpoint { get; }

    public string DisplayName => $"{_options.ProviderName} – {_options.Model}";

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
            throw new TranscriptionException("The API did not respond in time.") { IsTransient = true };
        }
        catch (HttpRequestException ex)
        {
            throw new TranscriptionException($"Network error: {ex.Message}", ex) { IsTransient = true };
        }

        using (response)
        {
            _logger.LogInformation("API {Provider} responded {Status} in {Ms} ms", _options.ProviderName, (int)response.StatusCode, sw.ElapsedMilliseconds);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new TranscriptionException("API key rejected. Check the key in Settings → API.") { IsFatal = true };
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                    ?? TimeSpan.FromSeconds(5);
                throw new TranscriptionException("API rate limit reached; backing off.") { IsTransient = true, RetryAfter = retryAfter };
            }
            if (response.StatusCode == HttpStatusCode.BadRequest && verbose && body.Contains("response_format", StringComparison.OrdinalIgnoreCase))
            {
                // Some models (e.g. newer GPT-4o transcription models) only support plain json.
                _verboseJsonUnsupported = true;
                return await SendAsync(wav, opts, ct).ConfigureAwait(false);
            }
            if ((int)response.StatusCode >= 500)
            {
                throw new TranscriptionException($"API server error ({(int)response.StatusCode}).") { IsTransient = true };
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new TranscriptionException($"API error ({(int)response.StatusCode}): {ExtractError(body)}") { IsFatal = response.StatusCode == HttpStatusCode.NotFound };
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
            throw new TranscriptionException("The API returned an unexpected response.", ex);
        }
        if (parsed is null)
        {
            throw new TranscriptionException("The API returned an empty response.");
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

public sealed record ApiProviderPreset(string Id, string Name, string BaseUrl, string DefaultModel)
{
    public static readonly ApiProviderPreset OpenAi = new("openai", "OpenAI", "https://api.openai.com/v1", "whisper-1");
    public static readonly ApiProviderPreset Groq = new("groq", "Groq", "https://api.groq.com/openai/v1", "whisper-large-v3-turbo");
    public static readonly ApiProviderPreset Custom = new("custom", "Custom", "http://localhost:8080/v1", "whisper-1");

    public static IReadOnlyList<ApiProviderPreset> All { get; } = [OpenAi, Groq, Custom];

    public static ApiProviderPreset Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Custom;
}
