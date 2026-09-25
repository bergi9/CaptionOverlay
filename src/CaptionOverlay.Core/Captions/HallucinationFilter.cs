using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CaptionOverlay.Core.Captions;

public sealed record FilterInput(
    string Text,
    string? Language,
    string? PreviousCommitted,
    float AudioRms,
    float? AverageProbability);

public sealed record FilterResult(bool Keep, string Text, string? Reason)
{
    public static FilterResult Drop(string reason) => new(false, "", reason);
}

/// <summary>Removes typical Whisper hallucinations (silence phrases, tags, repetition loops).</summary>
public sealed partial class HallucinationFilter
{
    /// <summary>Below this RMS (≈ -40 dBFS) an utterance is considered low-energy.</summary>
    public const float LowRms = 0.01f;

    private readonly Dictionary<string, List<string>> _phrases;

    public HallucinationFilter(IReadOnlyDictionary<string, IReadOnlyList<string>>? phrases = null)
    {
        var source = phrases ?? LoadEmbedded();
        _phrases = source.ToDictionary(
            kv => kv.Key.ToLowerInvariant(),
            kv => kv.Value.Select(Normalize).Where(p => p.Length > 0).Distinct().ToList());
    }

    public FilterResult Apply(FilterInput input)
    {
        string text = StripTags(input.Text);
        if (!HasLetters(text))
        {
            return FilterResult.Drop("empty or tags only");
        }

        string normalized = Normalize(text);
        if (IsKnownHallucination(normalized))
        {
            return FilterResult.Drop("known silence hallucination");
        }

        if (input.PreviousCommitted is { } previous
            && Normalize(previous) == normalized
            && input.AudioRms < LowRms)
        {
            return FilterResult.Drop("repetition on low-energy audio");
        }

        if (input.AverageProbability is { } p && p < 0.4f && input.AudioRms < LowRms)
        {
            return FilterResult.Drop("low confidence on low-energy audio");
        }

        return new FilterResult(true, text, null);
    }

    /// <summary>Light cleanup for tentative text: strips tags, keeps everything else.</summary>
    public string CleanPartial(string text)
    {
        string stripped = StripTags(text);
        return HasLetters(stripped) ? stripped : "";
    }

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }
        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * s;
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }

    // All languages are checked, not just the detected one: language detection is least reliable
    // on exactly the (near-silent) audio that produces these phrases.
    private bool IsKnownHallucination(string normalized) =>
        _phrases.Values.SelectMany(list => list).Any(phrase =>
            normalized == phrase || (phrase.Length >= 15 && normalized.Contains(phrase, StringComparison.Ordinal)));

    internal static string StripTags(string text)
    {
        string result = TagRegex().Replace(text, " ");
        result = result.Replace("♪", " ", StringComparison.Ordinal).Replace("♫", " ", StringComparison.Ordinal);
        return WhitespaceRegex().Replace(result, " ").Trim();
    }

    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c) || c is '-' or '\'')
            {
                sb.Append(' ');
            }
        }
        return WhitespaceRegex().Replace(sb.ToString(), " ").Trim();
    }

    private static bool HasLetters(string text) => text.Any(char.IsLetterOrDigit);

    private static Dictionary<string, IReadOnlyList<string>> LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CaptionOverlay.Core.Captions.hallucinations.json")
            ?? throw new InvalidOperationException("Embedded hallucination list missing.");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stream) ?? [];
        return parsed.ToDictionary(kv => kv.Key.ToLower(CultureInfo.InvariantCulture), kv => (IReadOnlyList<string>)kv.Value);
    }

    // [Music], (Musik), *applause*, <laughs>
    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)|\*[^\*]*\*|<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
