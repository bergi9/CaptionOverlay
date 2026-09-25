using System.Text.Json;

namespace CaptionOverlay.Core.Models;

public sealed record ResolvedModel(string Id, string DisplayName, string Path, string? ForceLanguage, bool IsCustom);

public sealed class CustomModel
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Path { get; set; } = "";

    public string? ForceLanguage { get; set; }
}

/// <summary>
/// Where models live: <c>%LOCALAPPDATA%\CaptionOverlay\models\{id}\{file}</c>, plus user-imported
/// custom model files registered in <c>models\custom.json</c> (referenced in place, not copied).
/// </summary>
public sealed class ModelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Lock _gate = new();

    public ModelStore(string? root = null)
    {
        Root = root ?? AppPaths.ModelsDir;
    }

    public string Root { get; }

    private string CustomFile => System.IO.Path.Combine(Root, "custom.json");

    public string GetPath(ModelCatalogEntry entry) => System.IO.Path.Combine(Root, entry.Id, entry.File);

    public string GetPartialPath(ModelCatalogEntry entry) => GetPath(entry) + ".part";

    public bool IsInstalled(ModelCatalogEntry entry) => File.Exists(GetPath(entry));

    public long PartialBytes(ModelCatalogEntry entry)
    {
        var info = new FileInfo(GetPartialPath(entry));
        return info.Exists ? info.Length : 0;
    }

    public void Delete(ModelCatalogEntry entry)
    {
        string dir = System.IO.Path.Combine(Root, entry.Id);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public IReadOnlyList<CustomModel> GetCustomModels()
    {
        lock (_gate)
        {
            if (!File.Exists(CustomFile))
            {
                return [];
            }
            try
            {
                return JsonSerializer.Deserialize<List<CustomModel>>(File.ReadAllText(CustomFile), JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public CustomModel AddCustomModel(string path, string displayName, string? forceLanguage)
    {
        lock (_gate)
        {
            var list = GetCustomModels().ToList();
            var model = new CustomModel
            {
                Id = "custom-" + Guid.NewGuid().ToString("N")[..8],
                DisplayName = displayName,
                Path = System.IO.Path.GetFullPath(path),
                ForceLanguage = string.IsNullOrWhiteSpace(forceLanguage) ? null : forceLanguage.Trim().ToLowerInvariant(),
            };
            list.Add(model);
            SaveCustom(list);
            return model;
        }
    }

    public void RemoveCustomModel(string id)
    {
        lock (_gate)
        {
            SaveCustom(GetCustomModels().Where(m => m.Id != id).ToList());
        }
    }

    /// <summary>Finds an installed model (catalog or custom) by id.</summary>
    public ResolvedModel? Resolve(string? id, ModelCatalog catalog)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        if (catalog.Find(id) is { } entry && IsInstalled(entry))
        {
            return new ResolvedModel(entry.Id, entry.DisplayName, GetPath(entry), entry.ForceLanguage, false);
        }
        if (GetCustomModels().FirstOrDefault(m => m.Id == id) is { } custom && File.Exists(custom.Path))
        {
            return new ResolvedModel(custom.Id, custom.DisplayName, custom.Path, custom.ForceLanguage, true);
        }
        return null;
    }

    public IReadOnlyList<ResolvedModel> GetInstalled(ModelCatalog catalog) =>
        catalog.Models.Where(IsInstalled)
            .Select(e => new ResolvedModel(e.Id, e.DisplayName, GetPath(e), e.ForceLanguage, false))
            .Concat(GetCustomModels().Where(c => File.Exists(c.Path))
                .Select(c => new ResolvedModel(c.Id, c.DisplayName, c.Path, c.ForceLanguage, true)))
            .ToList();

    private void SaveCustom(List<CustomModel> list)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(CustomFile, JsonSerializer.Serialize(list, JsonOptions));
    }
}
