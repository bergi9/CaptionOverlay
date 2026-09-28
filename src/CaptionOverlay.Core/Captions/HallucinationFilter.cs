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
    float? AverageProbability,
    bool KeepSoundTags = false);

public sealed record FilterResult(bool Keep, string Text, string? Reason)
{
    public static FilterResult Drop(string reason) => new(false, "", reason);
}

/// <summary>Removes typical Whisper hallucinations (silence phrases, tags, repetition loops).</summary>
public sealed partial class HallucinationFilter
{
    /// <summary>Below this RMS (≈ -40 dBFS) an utterance is considered low-energy.</summary>
    public const float LowRms = 0.01f;

    /// <summary>Below this RMS (≈ -60 dBFS) nothing is audible, so any text is a hallucination.</summary>
    public const float SilentRms = 0.001f;

    /// <summary>A word sequence or character group repeated this often in a row is a decoding loop, not speech.</summary>
    public const int LoopRepeats = 4;

    /// <summary>Longest word sequence checked for loops ("zu sparen zu sparen …" is a 2-word loop).</summary>
    private const int MaxLoopWords = 8;

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
        // Sound tags ([Musik], (Applaus), ♪) help deaf viewers, so they can be kept; a tag-only line then counts as content.
        string text = input.KeepSoundTags ? StripNonSoundTags(input.Text) : StripTags(input.Text);
        if (!HasContent(text))
        {
            return FilterResult.Drop("empty or tags only");
        }

        // A loop ("a lot a lot a lot …") must not reach the screen or, as the prompt of the next request, the next
        // utterance: small models continue a looped prompt, so one loop turned every following line into the same loop.
        // Mostly loop: drop. A short one ("Nein nein nein nein") is kept, collapsed, since it may have been said.
        string collapsed = CollapseLoops(text);
        if (collapsed.Length * 4 <= text.Length && text.Length >= 40)
        {
            return FilterResult.Drop("repetition loop");
        }
        text = collapsed;

        // Whisper still "hears" phrases such as "Vielen Dank." in digital silence (found with the German fine-tune).
        if (input.AudioRms < SilentRms)
        {
            return FilterResult.Drop("no audible signal");
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

    /// <summary>Light cleanup for tentative text: strips tags (unless kept) and collapses loops, keeps everything else.</summary>
    public string CleanPartial(string text, bool keepSoundTags = false)
    {
        string cleaned = CollapseLoops(keepSoundTags ? StripNonSoundTags(text) : StripTags(text));
        return HasContent(cleaned) ? cleaned : "";
    }

    /// <summary>
    /// Keeps one occurrence of every decoding loop: a sequence of up to <see cref="MaxLoopWords"/> words repeated at least
    /// <see cref="LoopRepeats"/> times in a row (compared without case and punctuation), and a short character group
    /// repeated inside one word ("'u'u'u'u…"). Groups without letters ("1000000") are left alone.
    /// </summary>
    internal static string CollapseLoops(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] keys = [.. words.Select(w => Normalize(w))];
        var kept = new List<string>(words.Length);
        int i = 0;
        while (i < words.Length)
        {
            int period = 0, count = 0;
            for (int p = 1; p <= MaxLoopWords && i + p * LoopRepeats <= words.Length; p++)
            {
                if (!string.Concat(keys.AsSpan(i, p)).Any(char.IsLetter))
                {
                    continue; // "0 0 0 0" or "... ... ..." is not a loop worth removing
                }
                int n = 1;
                while (i + (n + 1) * p <= words.Length && keys.AsSpan(i, p).SequenceEqual(keys.AsSpan(i + n * p, p)))
                {
                    n++;
                }
                if (n >= LoopRepeats && n * p > count * period)
                {
                    (period, count) = (p, n);
                }
            }
            if (count == 0)
            {
                kept.Add(words[i++]);
                continue;
            }
            kept.AddRange(words.AsSpan(i, period));
            int end = i + count * period;
            // A loop usually stops mid-sequence ("a lot a lot a"): drop that unfinished repetition too.
            for (int k = 0; k < period - 1 && end < words.Length && keys[end] == keys[i + k]; k++)
            {
                end++;
            }
            i = end;
        }
        // At least 8 characters, so "hmmmm" or "hahaha" stay as spoken.
        return string.Join(' ', kept.Select(w => CharLoopRegex().Replace(w,
            m => m.Length >= 8 && m.Groups[1].Value.Any(char.IsLetter) ? m.Groups[1].Value : m.Value)));
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

    /// <summary>Removes only tags that describe no sound: whisper.cpp's [BLANK_AUDIO] and "silence" markers.</summary>
    internal static string StripNonSoundTags(string text) =>
        WhitespaceRegex().Replace(NonSoundTagRegex().Replace(text, " "), " ").Trim();

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

    /// <summary>Letters, digits or a music note (only present when sound tags are kept).</summary>
    private static bool HasContent(string text) => text.Any(c => char.IsLetterOrDigit(c) || c is '♪' or '♫');

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

    [GeneratedRegex(@"[\[\(\*<]\s*(?:BLANK_AUDIO|silence|stille|no speech|keine sprache)\s*[\]\)\*>]", RegexOptions.IgnoreCase)]
    private static partial Regex NonSoundTagRegex();

    // A group of 1–10 characters followed by at least three copies of itself.
    [GeneratedRegex(@"(.{1,10}?)\1{3,}")]
    private static partial Regex CharLoopRegex();
}
