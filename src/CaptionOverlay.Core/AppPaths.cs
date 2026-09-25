namespace CaptionOverlay.Core;

/// <summary>Per-user locations. Nothing is ever written next to the executable (portable app).</summary>
public static class AppPaths
{
    public const string AppName = "CaptionOverlay";

    /// <summary>Override root for tests / portable experiments (settings, models, logs all go below it).</summary>
    public static string? RootOverride { get; set; }

    public static string RoamingDir => RootOverride is { } root
        ? Path.Combine(root, "roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string LocalDir => RootOverride is { } root
        ? Path.Combine(root, "local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");

    public static string SecretsFile => Path.Combine(RoamingDir, "secrets.dat");

    public static string ModelsDir => Path.Combine(LocalDir, "models");

    public static string LogsDir => Path.Combine(LocalDir, "logs");

    public static string CatalogCacheFile => Path.Combine(LocalDir, "catalog-cache.json");
}
