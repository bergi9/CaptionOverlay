using System.Globalization;

namespace CaptionOverlay.Cli;

public sealed class CliException(string message) : Exception(message);

/// <summary>Tiny argument parser: <c>command [positional...] [--flag value | --switch]</c>.</summary>
public sealed class CliArgs
{
    private static readonly HashSet<string> Switches = ["help", "verbose", "realtime", "no-vad", "no-partials", "cpu"];

    public string? Command { get; private init; }

    public List<string> Positional { get; } = [];

    public Dictionary<string, string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static CliArgs Parse(string[] args)
    {
        var result = new CliArgs { Command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null };
        for (int i = result.Command is null ? 0 : 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                string name = a[2..];
                if (Switches.Contains(name) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    result.Flags[name] = "true";
                }
                else
                {
                    result.Flags[name] = args[++i];
                }
            }
            else
            {
                result.Positional.Add(a);
            }
        }
        return result;
    }

    public string? Get(string name) => Flags.GetValueOrDefault(name);

    public bool Has(string name) => Flags.ContainsKey(name);

    public double GetDouble(string name, double fallback) =>
        Flags.TryGetValue(name, out var v)
            ? double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new CliException($"--{name} expects a number")
            : fallback;

    public string RequirePositional(int index, string what) =>
        Positional.Count > index ? Positional[index] : throw new CliException($"missing {what}");
}
