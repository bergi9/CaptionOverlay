using System.Globalization;
using System.Resources;

namespace CaptionOverlay.Core.Localization;

/// <summary>
/// Localized text. English is the neutral resource set and the fallback for every missing translation;
/// German lives in satellite assemblies. There is one process-wide UI culture (<see cref="Culture"/>), so text
/// built on background threads (errors, download progress) matches the UI. Hosts that never call
/// <see cref="SetLanguage"/> (CLI, tests) stay English.
/// </summary>
public static class Loc
{
    /// <summary>Settings value meaning "use the Windows display language".</summary>
    public const string SystemLanguage = "system";

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static ResourceManager[] _resources = [CoreStrings.ResourceManager];
    private static CultureInfo _culture = English;

    /// <summary>UI languages with translations. Anything else falls back to English.</summary>
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["en", "de"];

    /// <summary>The Windows display language at startup (before anything could change the thread culture).</summary>
    public static CultureInfo SystemUICulture { get; } = CultureInfo.CurrentUICulture;

    /// <summary>Culture for resource lookup and number formatting in UI text.</summary>
    public static CultureInfo Culture => Volatile.Read(ref _culture);

    /// <summary>Raised (on the thread that changed it) after <see cref="Culture"/> changed.</summary>
    public static event Action? CultureChanged;

    /// <summary>Adds a host's resource set (e.g. the WPF app's UI strings); it is searched before the Core strings.</summary>
    public static void AddResources(ResourceManager resources)
    {
        var current = _resources;
        if (!current.Contains(resources))
        {
            Volatile.Write(ref _resources, [resources, .. current]);
        }
    }

    /// <summary>
    /// Maps the settings value ("system", "en", "de", …) to a supported culture. "system" follows the Windows display
    /// language; unsupported languages fall back to English. The regional variant (de-AT, en-GB) is kept when it is
    /// the same language, so numbers are formatted the way the user is used to.
    /// </summary>
    public static CultureInfo Resolve(string? setting, CultureInfo systemUiCulture, CultureInfo? regionalCulture = null)
    {
        string wanted = string.IsNullOrWhiteSpace(setting) || setting.Trim().Equals(SystemLanguage, StringComparison.OrdinalIgnoreCase)
            ? systemUiCulture.TwoLetterISOLanguageName
            : setting.Trim().ToLowerInvariant();
        if (!SupportedLanguages.Contains(wanted))
        {
            wanted = "en";
        }
        foreach (var candidate in new[] { regionalCulture, systemUiCulture })
        {
            if (candidate is { IsNeutralCulture: false } && candidate.TwoLetterISOLanguageName == wanted)
            {
                return candidate;
            }
        }
        return wanted == "de" ? CultureInfo.GetCultureInfo("de-DE") : English;
    }

    /// <summary>Applies the UI language setting ("system" = Windows display language).</summary>
    public static void SetLanguage(string? setting) => SetCulture(Resolve(setting, SystemUICulture, CultureInfo.CurrentCulture));

    public static void SetCulture(CultureInfo culture)
    {
        if (Equals(Interlocked.Exchange(ref _culture, culture), culture))
        {
            return;
        }
        CultureChanged?.Invoke();
    }

    /// <summary>The text for <paramref name="key"/> in the current culture, English if untranslated, the key itself if unknown.</summary>
    public static string Get(string key) => Get(key, Culture);

    public static string Get(string key, CultureInfo culture)
    {
        foreach (var resources in Volatile.Read(ref _resources))
        {
            if (resources.GetString(key, culture) is { } text)
            {
                return text;
            }
        }
        return key;
    }

    /// <summary>The text for <paramref name="key"/> if any resource set defines it (no key fallback).</summary>
    public static string? TryGet(string key)
    {
        string text = Get(key);
        return text == key ? null : text;
    }

    /// <summary><see cref="string.Format(IFormatProvider, string, object[])"/> with the localized format string and culture.</summary>
    public static string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);
}

/// <summary>Core's own messages (errors, status, hardware advice): <c>Localization/CoreStrings*.resx</c>.</summary>
internal static class CoreStrings
{
    public static ResourceManager ResourceManager { get; } = new("CaptionOverlay.Core.Localization.CoreStrings", typeof(CoreStrings).Assembly);
}
