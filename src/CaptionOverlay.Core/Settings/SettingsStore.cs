using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaptionOverlay.Core.Settings;

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON, with a schema-version migration hook.</summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    public SettingsStore(string? path = null, ILogger? logger = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        _logger = logger ?? NullLogger.Instance;
    }

    public string FilePath => _path;

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(_path), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject ?? [];
                Migrate(node);
                return node.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Settings file unreadable; backing it up and using defaults");
                TryBackup();
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>Upgrades older JSON in place. Add a step per schema bump.</summary>
    internal static void Migrate(JsonObject node)
    {
        int version = node["schemaVersion"]?.GetValue<int>() ?? 0;
        if (version < 1)
        {
            // v0 → v1: nothing to transform (v1 is the first released schema).
            node["schemaVersion"] = 1;
        }
    }

    private void TryBackup()
    {
        try
        {
            File.Copy(_path, _path + ".broken", overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
