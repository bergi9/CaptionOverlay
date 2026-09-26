namespace CaptionOverlay.Core.Captions;

/// <summary>
/// Splits a caption into display rows that each fit the overlay's width, so a long utterance (Whisper is more accurate
/// with whole sentences) is shown as short lines that scroll, instead of one block of text. Breaks after a sentence end
/// if that leaves the row at least half full, else after a comma (or similar) if at least 60 % full, else between words.
/// </summary>
public static class CaptionLineSplitter
{
    private const double SentenceBreakMinFill = 0.5;
    private const double ClauseBreakMinFill = 0.6;

    /// <param name="text">The caption text.</param>
    /// <param name="maxWidth">Width of one row; 0 or less: no splitting.</param>
    /// <param name="measure">Width of a piece of text in the same unit as <paramref name="maxWidth"/>.</param>
    public static IReadOnlyList<string> Split(string text, double maxWidth, Func<string, double> measure)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }
        string whole = string.Join(' ', words);
        if (maxWidth <= 0 || measure(whole) <= maxWidth)
        {
            return [whole];
        }

        var rows = new List<string>();
        int start = 0;
        while (start < words.Length)
        {
            // Longest run of words that fits (at least one word: a word wider than the row wraps on its own).
            int end = start + 1;
            while (end < words.Length && measure(Join(words, start, end + 1)) <= maxWidth)
            {
                end++;
            }
            if (end < words.Length)
            {
                end = PreferredBreak(words, start, end, maxWidth, measure);
            }
            rows.Add(Join(words, start, end));
            start = end;
        }
        return rows;
    }

    private static int PreferredBreak(string[] words, int start, int end, double maxWidth, Func<string, double> measure)
    {
        for (int k = end; k > start; k--)
        {
            if (EndsSentence(words[k - 1]))
            {
                if (measure(Join(words, start, k)) >= maxWidth * SentenceBreakMinFill)
                {
                    return k;
                }
                break;
            }
        }
        for (int k = end; k > start; k--)
        {
            if (EndsClause(words[k - 1]))
            {
                if (measure(Join(words, start, k)) >= maxWidth * ClauseBreakMinFill)
                {
                    return k;
                }
                break;
            }
        }
        return end;
    }

    private static bool EndsSentence(string word) => TrimClosers(word) is [.., '.' or '!' or '?' or '…'];

    private static bool EndsClause(string word) => TrimClosers(word) is [.., ',' or ';' or ':'] || word is "–" or "—" or "-";

    private static string TrimClosers(string word) => word.TrimEnd('"', '\'', '”', '’', '»', '«', ')', ']');

    private static string Join(string[] words, int start, int end) => string.Join(' ', words, start, end - start);
}
