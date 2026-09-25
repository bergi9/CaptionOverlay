using System.Security.Cryptography;
using System.Text.Json;

// Shared by the CLI and the test project (linked source file).
namespace CaptionOverlay.Cli.Fixtures;

/// <summary><c>manifest.json</c> of a fixture folder: where the files came from and their hashes.</summary>
public sealed record FixtureManifest(
    string Generator,
    int GeneratorVersion,
    string Dataset,
    string Revision,
    string Subset,
    string License,
    IReadOnlyList<string> Selected,
    IReadOnlyList<FixtureFile> Files)
{
    public static string PathIn(string folder) => Path.Combine(folder, "manifest.json");

    /// <summary>Checks that every file listed in the manifest exists with the recorded SHA-256. Returns the problems found.</summary>
    public static IReadOnlyList<string> Verify(string folder)
    {
        string manifestPath = PathIn(folder);
        if (!File.Exists(manifestPath))
        {
            return [$"{manifestPath} is missing"];
        }
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(manifestPath), GermanComposite.Json);
        if (manifest is null || manifest.Files.Count == 0)
        {
            return [$"{manifestPath} lists no files"];
        }
        var problems = new List<string>();
        foreach (var f in manifest.Files)
        {
            string path = Path.Combine(folder, f.Path);
            if (!File.Exists(path))
            {
                problems.Add($"{f.Path} is missing");
            }
            else if (Sha256(path) is var actual && actual != f.Sha256)
            {
                problems.Add($"{f.Path}: SHA-256 {actual} does not match the manifest ({f.Sha256})");
            }
        }
        if (!File.Exists(Path.Combine(folder, "ATTRIBUTION.md")))
        {
            problems.Add("ATTRIBUTION.md is missing");
        }
        return problems;
    }

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }
}

public sealed record FixtureFile(string Path, string Sha256, long Size);
