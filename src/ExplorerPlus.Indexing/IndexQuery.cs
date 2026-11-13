namespace ExplorerPlus.Indexing;

/// <summary>
/// A parsed instant-search query. Words separated by spaces must all match (AND), case-insensitively:
/// a plain word matches anywhere in the name, a word with <c>*</c> or <c>?</c> must match the whole name,
/// a word containing <c>\</c> is matched against the full path instead, <c>"quoted text"</c> keeps its spaces,
/// and a leading <c>!</c> excludes what the word matches.
/// </summary>
public sealed class IndexQuery
{
    private readonly Term[] _nameTerms;
    private readonly Term[] _pathTerms;

    private IndexQuery(Term[] nameTerms, Term[] pathTerms)
    {
        _nameTerms = nameTerms;
        _pathTerms = pathTerms;
    }

    /// <summary>True when nothing would be searched for (empty text, or only exclusions).</summary>
    public bool IsEmpty => !_nameTerms.Concat(_pathTerms).Any(t => !t.Negate);

    public bool HasPathTerms => _pathTerms.Length > 0;

    public static IndexQuery Parse(string text)
    {
        var names = new List<Term>();
        var paths = new List<Term>();
        foreach (var (word, quoted) in Split(text))
        {
            var negate = !quoted && word.Length > 1 && word[0] == '!';
            var body = negate ? word[1..] : word;
            if (body.Length == 0) continue;
            var isPath = body.Contains('\\') || body.Contains('/');
            if (isPath) body = body.Replace('/', '\\');
            var term = new Term(body, body.AsSpan().IndexOfAny('*', '?') >= 0, negate);
            (isPath ? paths : names).Add(term);
        }
        return new IndexQuery([.. names], [.. paths]);
    }

    private static IEnumerable<(string Word, bool Quoted)> Split(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) yield break;
            if (text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length;
                var word = text[(i + 1)..end];
                if (word.Length > 0) yield return (word, true);
                i = end + 1;
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            yield return (text[start..i], false);
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> passes every name word. <paramref name="rank"/> orders the hits: 0 when the
    /// first word is the whole name (with or without its extension), 1 when the name starts with it, 2 otherwise.
    /// </summary>
    public bool MatchesName(ReadOnlySpan<char> name, out int rank)
    {
        rank = 2;
        var ranked = false;
        foreach (var term in _nameTerms)
        {
            if (term.Matches(name) == term.Negate) return false;
            if (ranked || term.Negate) continue;
            ranked = true;
            rank = term.Wildcard ? 1 : RankOf(name, term.Text);
        }
        return true;
    }

    public bool MatchesPath(ReadOnlySpan<char> path)
    {
        foreach (var term in _pathTerms)
            if (term.Matches(path) == term.Negate) return false;
        return true;
    }

    private static int RankOf(ReadOnlySpan<char> name, string word)
    {
        if (!name.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Length == word.Length) return 0;
        var dot = name.LastIndexOf('.');
        return dot == word.Length ? 0 : 1;
    }

    private sealed record Term(string Text, bool Wildcard, bool Negate)
    {
        public bool Matches(ReadOnlySpan<char> text) =>
            Wildcard ? Glob.IsMatch(Text, text) : text.Contains(Text, StringComparison.OrdinalIgnoreCase);
    }
}

public static class Glob
{
    /// <summary>Case-insensitive match of the whole <paramref name="text"/> against <c>*</c> and <c>?</c> wildcards.</summary>
    public static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && (pattern[p] == '?' || Same(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool Same(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
