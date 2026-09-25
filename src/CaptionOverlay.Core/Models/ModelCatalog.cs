using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaptionOverlay.Core.Models;

public sealed class ModelCatalogEntry
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public List<string> Languages { get; set; } = [];

    public string? ForceLanguage { get; set; }

    public string Repo { get; set; } = "";

    public string File { get; set; } = "";

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = "";

    public string? License { get; set; }

    public string? Tier { get; set; }

    public string? Notes { get; set; }

    [JsonIgnore]
    public Uri DownloadUri => new($"https://{ModelCatalog.AllowedHost}/{Repo}/resolve/main/{Uri.EscapeDataString(File)}");

    [JsonIgnore]
    public bool IsMultilingual => Languages.Contains("multi");
}

public sealed partial class ModelCatalog
{
    public const string AllowedHost = "huggingface.co";
    public const string DefaultRemoteUrl = "https://raw.githubusercontent.com/bergi9/whisper-live-caption/main/catalog/models.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public int Version { get; set; }

    public List<ModelCatalogEntry> Models { get; set; } = [];

    /// <summary>Where this catalog came from ("bundled" or the remote URL).</summary>
    [JsonIgnore]
    public string Source { get; private set; } = "bundled";

    public ModelCatalogEntry? Find(string? id) => Models.FirstOrDefault(m => m.Id == id);

    public static ModelCatalog LoadBundled()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CaptionOverlay.Core.Models.models.json")
            ?? throw new InvalidOperationException("Bundled model catalog missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), out _);
    }

    /// <summary>
    /// Uses the remote catalog if it is valid and at least as new as the bundled one; otherwise the bundled one.
    /// The remote catalog is data only: invalid entries are dropped, never trusted.
    /// </summary>
    public static async Task<ModelCatalog> LoadAsync(HttpClient http, string? remoteUrl = DefaultRemoteUrl,
        TimeSpan? timeout = null, ILogger? logger = null, CancellationToken ct = default)
    {
        logger ??= NullLogger.Instance;
        var bundled = LoadBundled();
        if (string.IsNullOrEmpty(remoteUrl))
        {
            return bundled;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(4));
            string json = await http.GetStringAsync(remoteUrl, cts.Token).ConfigureAwait(false);
            var remote = Parse(json, out var rejected);
            foreach (var reason in rejected)
            {
                logger.LogWarning("Remote catalog entry rejected: {Reason}", reason);
            }
            if (remote.Version >= bundled.Version && remote.Models.Count > 0)
            {
                remote.Source = remoteUrl;
                logger.LogInformation("Using remote model catalog v{Version} ({Count} models)", remote.Version, remote.Models.Count);
                return remote;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            logger.LogInformation("Remote model catalog unavailable ({Message}); using bundled catalog", ex.Message);
        }
        return bundled;
    }

    /// <summary>Parses and validates a catalog. Invalid entries are removed and reported in <paramref name="rejected"/>.</summary>
    public static ModelCatalog Parse(string json, out List<string> rejected)
    {
        rejected = [];
        var catalog = JsonSerializer.Deserialize<ModelCatalog>(json, JsonOptions)
            ?? throw new InvalidDataException("Catalog is empty.");
        if (catalog.Version < 1)
        {
            throw new InvalidDataException("Catalog has no valid version.");
        }

        var valid = new List<ModelCatalogEntry>();
        var ids = new HashSet<string>();
        foreach (var entry in catalog.Models ?? [])
        {
            string? problem = Validate(entry);
            if (problem is null && !ids.Add(entry.Id))
            {
                problem = "duplicate id";
            }
            if (problem is null)
            {
                valid.Add(entry);
            }
            else
            {
                rejected.Add($"{entry?.Id ?? "<null>"}: {problem}");
            }
        }
        catalog.Models = valid;
        return catalog;
    }

    internal static string? Validate(ModelCatalogEntry? e)
    {
        if (e is null)
        {
            return "null entry";
        }
        if (!IdRegex().IsMatch(e.Id))
        {
            return "invalid id";
        }
        if (string.IsNullOrWhiteSpace(e.DisplayName))
        {
            return "missing display name";
        }
        if (!RepoRegex().IsMatch(e.Repo) || e.Repo.Contains("..", StringComparison.Ordinal))
        {
            return "invalid repo";
        }
        if (!FileRegex().IsMatch(e.File) || e.File.Contains("..", StringComparison.Ordinal))
        {
            return "invalid file name";
        }
        if (EnglishOnlyRegex().IsMatch(e.File))
        {
            return "English-only (.en) models are not offered";
        }
        if (e.SizeBytes <= 0)
        {
            return "invalid size";
        }
        if (!Sha256Regex().IsMatch(e.Sha256))
        {
            return "invalid sha256";
        }
        if (e.ForceLanguage is { } lang && !LanguageRegex().IsMatch(lang))
        {
            return "invalid forceLanguage";
        }
        var uri = e.DownloadUri;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase))
        {
            return "download host not allowed";
        }
        return null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RepoRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\.bin$")]
    private static partial Regex FileRegex();

    [GeneratedRegex(@"\.en([-.]|$)")]
    private static partial Regex EnglishOnlyRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[a-z]{2,3}$")]
    private static partial Regex LanguageRegex();
}
