using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CaptionOverlay.App.Infrastructure;

public sealed record UpdateInfo(Version Version, string Tag, string Url);

/// <summary>Optional "new version available" check against GitHub Releases. Never downloads anything.</summary>
public static class UpdateChecker
{
    public const string Repository = "bergi9/whisper-live-caption";

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentVersionText
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return info?.Split('+')[0] ?? CurrentVersion.ToString(3);
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CaptionOverlay", CurrentVersion.ToString(3)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string url = doc.RootElement.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases";
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && latest > CurrentVersion)
            {
                return new UpdateInfo(latest, tag, url);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            logger.LogInformation("Update check failed: {Message}", ex.Message);
        }
        return null;
    }
}
