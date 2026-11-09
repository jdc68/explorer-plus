namespace ExplorerPlus.Core.Navigation;

public sealed record ExplorerCommandLine(IReadOnlyList<string> Paths, string? SelectPath, bool NewWindow)
{
    public static ExplorerCommandLine Parse(IReadOnlyList<string> args)
    {
        // Explorer accepts both "/select,C:\x" as one argument and "/select," "C:\x" split by the shell.
        var tokens = new List<string>();
        foreach (var raw in args)
        {
            foreach (var part in SplitCommas(raw))
            {
                var t = part.Trim().Trim('"').Trim();
                if (t.Length > 0) tokens.Add(t);
            }
        }

        var paths = new List<string>();
        string? select = null;
        var newWindow = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var lower = t.ToLowerInvariant();
            switch (lower)
            {
                case "/n":
                case "-n":
                    newWindow = true;
                    break;
                case "/e":
                case "/separate":
                    break;
                case "/select":
                    if (i + 1 < tokens.Count) select = tokens[++i];
                    break;
                case "/root":
                    if (i + 1 < tokens.Count) paths.Add(tokens[++i]);
                    break;
                default:
                    if (!t.StartsWith('/') || t.StartsWith("//")) paths.Add(t);
                    break;
            }
        }
        return new ExplorerCommandLine(paths, select, newWindow);
    }

    // Splits on commas that are outside quotes and that follow a switch ("/select,path").
    private static IEnumerable<string> SplitCommas(string raw)
    {
        if (!raw.TrimStart().StartsWith('/')) { yield return raw; yield break; }
        var inQuotes = false;
        var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '"') inQuotes = !inQuotes;
            else if (raw[i] == ',' && !inQuotes)
            {
                yield return raw[start..i];
                start = i + 1;
                // Everything after the first switch's comma is the value (paths may contain commas).
                var rest = raw[start..];
                if (!rest.TrimStart().StartsWith('/')) { yield return rest; yield break; }
            }
        }
        yield return raw[start..];
    }

    public string[] ToArgs()
    {
        var list = new List<string>();
        if (NewWindow) list.Add("/n");
        if (SelectPath is not null) list.Add("/select," + SelectPath);
        list.AddRange(Paths);
        return list.ToArray();
    }
}
