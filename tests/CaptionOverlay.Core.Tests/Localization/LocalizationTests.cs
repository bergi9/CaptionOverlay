using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CaptionOverlay.Core.Localization;
using CaptionOverlay.Core.Settings;

namespace CaptionOverlay.Core.Tests.Localization;

/// <summary>Language selection and lookup. These never change the process-wide <see cref="Loc.Culture"/> (tests run in parallel).</summary>
public class LocTests
{
    private static readonly CultureInfo EnGb = CultureInfo.GetCultureInfo("en-GB");
    private static readonly CultureInfo DeAt = CultureInfo.GetCultureInfo("de-AT");
    private static readonly CultureInfo FrFr = CultureInfo.GetCultureInfo("fr-FR");

    [Theory]
    [InlineData("system", "de-AT", "de-AT")] // Windows in German: German, keeping the regional variant
    [InlineData(null, "de-AT", "de-AT")]
    [InlineData("", "de-AT", "de-AT")]
    [InlineData("system", "en-GB", "en-GB")]
    [InlineData("system", "fr-FR", "en-US")] // untranslated Windows language: English
    [InlineData("de", "fr-FR", "de-DE")] // explicit choice wins over Windows
    [InlineData("DE", "en-GB", "de-DE")]
    [InlineData("en", "de-AT", "en-US")]
    [InlineData("xx", "de-AT", "en-US")] // unknown setting: English
    public void Resolve_follows_windows_unless_a_language_is_chosen(string? setting, string system, string expected)
    {
        Loc.Resolve(setting, CultureInfo.GetCultureInfo(system)).Name.Should().Be(expected);
    }

    [Fact]
    public void Resolve_keeps_the_users_regional_format_for_the_same_language()
    {
        Loc.Resolve("de", EnGb, regionalCulture: CultureInfo.GetCultureInfo("de-CH")).Name.Should().Be("de-CH");
        Loc.Resolve("system", EnGb, regionalCulture: DeAt).Name.Should().Be("en-GB");
    }

    [Fact]
    public void Lookup_uses_the_translation_and_falls_back_to_english()
    {
        Loc.Get("Pipeline_Paused", DeAt).Should().Be("Untertitel pausiert"); // de-AT → de
        Loc.Get("Pipeline_Paused", FrFr).Should().Be("Captions paused");
        Loc.Get("Pipeline_Paused", CultureInfo.InvariantCulture).Should().Be("Captions paused");
        Loc.Get("No_such_key", DeAt).Should().Be("No_such_key");
        Loc.TryGet("No_such_key").Should().BeNull();
    }

    [Fact]
    public void Default_culture_is_english_so_cli_and_tests_stay_english() =>
        Loc.Culture.TwoLetterISOLanguageName.Should().Be("en");

    [Fact]
    public void Settings_sanitize_restores_language_and_theme_defaults()
    {
        var s = new AppSettings();
        s.General.UiLanguage = " ";
        s.General.Theme = (AppTheme)42;
        s.Sanitize();
        s.General.UiLanguage.Should().Be(Loc.SystemLanguage);
        s.General.Theme.Should().Be(AppTheme.System);
    }
}

/// <summary>Checks the .resx files in the source tree: complete translations, matching placeholders, no unknown keys in code.</summary>
public partial class ResourceFileTests
{
    private static readonly string Root = FindRoot();

    private static readonly string[] Sets = ["src/CaptionOverlay.Core/Localization/CoreStrings", "src/CaptionOverlay.App/Localization/Strings"];

    public static TheoryData<string> ResourceSets => new(Sets);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CaptionOverlay.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Repository root (CaptionOverlay.slnx) not found.");
    }

    private static Dictionary<string, string> Read(string path) =>
        XDocument.Load(Path.Combine(Root, path)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    /// <summary>"{0:F2} of {1}" → ["0:F2", "1"]; the translation must use the same arguments and formats.</summary>
    private static string[] Placeholders(string text) =>
        [.. PlaceholderRegex().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(ResourceSets))]
    public void German_translation_is_complete_and_keeps_placeholders(string set)
    {
        var en = Read(set + ".resx");
        var de = Read(set + ".de.resx");

        en.Keys.Except(de.Keys).Should().BeEmpty("every English string needs a German translation");
        // Model notes are keyed by catalog id; their English text lives in catalog/models.json.
        de.Keys.Where(k => !k.StartsWith("ModelNote_", StringComparison.Ordinal)).Except(en.Keys)
            .Should().BeEmpty("German keys without an English original are never used");
        foreach (var (key, text) in en)
        {
            Placeholders(de[key]).Should().Equal(Placeholders(text), $"placeholders of {key}");
            string.Format(CultureInfo.GetCultureInfo("de-DE"), de[key], 1.5, "x", 2.5, 3.5); // must not throw
        }
    }

    [Fact]
    public void Every_key_used_in_code_exists()
    {
        var known = Sets.SelectMany(s => Read(s + ".resx").Keys).ToHashSet(StringComparer.Ordinal);
        var used = new List<(string File, string Key)>();
        foreach (string project in new[] { "src/CaptionOverlay.App", "src/CaptionOverlay.Core" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(Root, project), "*.*", SearchOption.AllDirectories)
                         .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                string text = File.ReadAllText(file);
                foreach (Match m in KeyUseRegex().Matches(text))
                {
                    foreach (var g in new[] { m.Groups["a"], m.Groups["b"], m.Groups["c"] }.Where(g => g.Success))
                    {
                        used.Add((Path.GetFileName(file), g.Value));
                    }
                }
            }
        }

        used.Should().HaveCountGreaterThan(200, "the scan must actually find the keys");
        used.Where(u => !known.Contains(u.Key)).Select(u => $"{u.File}: {u.Key}").Should().BeEmpty();
    }

    [GeneratedRegex(@"\{(\d+(?::[^}]*)?)\}")]
    private static partial Regex PlaceholderRegex();

    // {l:Loc Key}, Loc.Get("Key"), Loc.Format("Key", …), Localized(x, "Key"), Loc.Get(c ? "A" : "B")
    // A literal followed by "+" is a prefix ("Lang_" + code) and is not checked.
    [GeneratedRegex(@"\{l:Loc (?<a>\w+)|(?:Loc\.(?:Get|Format|TryGet)|Localized\(.*?,)\s*\(?\s*""(?<a>\w+)""(?=\s*[,)])|Loc\.(?:Get|Format)\([^""()]*\?\s*""(?<b>\w+)""\s*:\s*""(?<c>\w+)""")]
    private static partial Regex KeyUseRegex();
}
