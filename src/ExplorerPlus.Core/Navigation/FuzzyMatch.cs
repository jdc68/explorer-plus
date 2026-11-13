namespace ExplorerPlus.Core.Navigation;

public static class FuzzyMatch
{
    /// <summary>
    /// Scores <paramref name="text"/> against what was typed, or returns null when the letters of the query don't all
    /// appear in order. Higher is better: a prefix beats a word start, a word start beats a run in the middle, and a
    /// run beats scattered letters. Case is ignored.
    /// </summary>
    public static int? Score(string query, string text)
    {
        query = query.Trim();
        if (query.Length == 0) return 0;
        if (text.Length == 0) return null;

        var contiguous = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (contiguous == 0) return 10_000 - text.Length;
        if (contiguous > 0 && IsWordStart(text, contiguous)) return 8_000 - text.Length;
        if (contiguous > 0) return 6_000 - contiguous * 10 - text.Length;

        // Letters in order: reward each one that starts a word or follows the previous match.
        var score = 0;
        var last = -2;
        var t = 0;
        foreach (var q in query)
        {
            if (char.IsWhiteSpace(q)) continue;
            var found = -1;
            for (; t < text.Length; t++)
            {
                if (char.ToUpperInvariant(text[t]) != char.ToUpperInvariant(q)) continue;
                found = t++;
                break;
            }
            if (found < 0) return null;
            score += found == last + 1 ? 30 : IsWordStart(text, found) ? 40 : 1;
            last = found;
        }
        return score * 10 - text.Length;
    }

    private static bool IsWordStart(string text, int i) =>
        i == 0 || !char.IsLetterOrDigit(text[i - 1]) || char.IsUpper(text[i]) && char.IsLower(text[i - 1]);
}
