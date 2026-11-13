using System.Globalization;
using System.Text.RegularExpressions;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.Core.Shell;

public enum NameCase { Unchanged, Lower, Upper, Title }

public sealed record BatchRenameOptions
{
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool UseRegex { get; init; }
    public bool MatchCase { get; init; }

    /// <summary>The new name. Tokens: {name}, {n} or {n:000}, {date} or {date:yyyyMMdd}, {ext}, {parent}.</summary>
    public string Pattern { get; init; } = "{name}";

    public NameCase Case { get; init; }

    /// <summary>Apply everything to the whole file name; otherwise the extension is kept as it is.</summary>
    public bool IncludeExtension { get; init; }

    public int Start { get; init; } = 1;
    public int Step { get; init; } = 1;
}

public sealed record BatchRenameItem(string Path, bool IsFolder, DateTime? Modified = null);

public sealed record BatchRenamePlan(string Path, string OldName, string NewName, string? Error)
{
    public bool IsChanged => !string.Equals(OldName, NewName, StringComparison.Ordinal);
}

public sealed record BatchRenameResult(FileOpResult Result, IReadOnlyList<(string NewPath, string OldName)> Renamed);

public static partial class BatchRename
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex(@"\{(name|n|date|ext|parent)(?::([^{}]*))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    /// <summary>Works out every new name and what, if anything, stops it. <paramref name="exists"/> answers whether
    /// a path is taken (the file system by default); items in the batch don't count, since they move out of the way.</summary>
    public static IReadOnlyList<BatchRenamePlan> Plan(IReadOnlyList<BatchRenameItem> items, BatchRenameOptions options, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        Regex? find = null;
        string? patternError = null;
        if (options.Find.Length > 0)
        {
            var flags = options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            try { find = new Regex(options.UseRegex ? options.Find : Regex.Escape(options.Find), flags, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException ex) { patternError = "Invalid search pattern: " + ex.Message; }
        }

        var names = new List<(BatchRenameItem Item, string Old, string New, string? Error)>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var old = System.IO.Path.GetFileName(item.Path.TrimEnd('\\'));
            if (patternError is not null) { names.Add((item, old, old, patternError)); continue; }
            try { names.Add((item, old, NewName(item, old, options, find, options.Start + i * options.Step), null)); }
            catch (RegexMatchTimeoutException) { names.Add((item, old, old, "The search pattern takes too long.")); }
            catch (FormatException) { names.Add((item, old, old, "A number or date format in the pattern is invalid.")); }
        }

        var sources = items.Select(i => Normalize(i.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = names.GroupBy(n => TargetPath(n.Item.Path, n.New), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return names.Select(n =>
        {
            var error = n.Error ?? Validate(n.New);
            if (error is null && !string.Equals(n.Old, n.New, StringComparison.Ordinal))
            {
                var target = TargetPath(n.Item.Path, n.New);
                if (targets[target] > 1) error = "Another item would get the same name.";
                else if (!sources.Contains(target) && exists(target)) error = "An item with this name already exists.";
            }
            return new BatchRenamePlan(n.Item.Path, n.Old, n.New, error);
        }).ToList();
    }

    private static string NewName(BatchRenameItem item, string old, BatchRenameOptions o, Regex? find, int number)
    {
        var ext = item.IsFolder ? "" : System.IO.Path.GetExtension(old);
        string stem = old, keptExt = "";
        // ".gitignore" is all extension; it is renamed as a whole.
        if (!item.IsFolder && !o.IncludeExtension && ext.Length > 0 && ext.Length < old.Length)
        {
            stem = old[..^ext.Length];
            keptExt = ext;
        }

        var name = find is null ? stem : find.Replace(stem, o.UseRegex ? o.Replace : o.Replace.Replace("$", "$$"));
        var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(item.Path.TrimEnd('\\')) ?? "") ?? "";
        var result = TokenRegex().Replace(o.Pattern.Length == 0 ? "{name}" : o.Pattern, m =>
        {
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;
            return m.Groups[1].Value.ToLowerInvariant() switch
            {
                "name" => name,
                "n" => format is null ? number.ToString(CultureInfo.CurrentCulture) : FormatNumber(number, format),
                "date" => (item.Modified ?? DateTime.Now).ToString(string.IsNullOrEmpty(format) ? "yyyy-MM-dd" : format, CultureInfo.CurrentCulture),
                "ext" => ext.TrimStart('.'),
                "parent" => parent,
                _ => m.Value,
            };
        });
        result = o.Case switch
        {
            NameCase.Lower => result.ToLower(CultureInfo.CurrentCulture),
            NameCase.Upper => result.ToUpper(CultureInfo.CurrentCulture),
            NameCase.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(result.ToLower(CultureInfo.CurrentCulture)),
            _ => result,
        };
        return result + keptExt;
    }

    /// <summary>"000" pads to three digits; anything else is a .NET number format.</summary>
    private static string FormatNumber(int number, string format) =>
        format.Length > 0 && format.All(c => c == '0')
            ? number.ToString(CultureInfo.InvariantCulture).PadLeft(format.Length, '0')
            : number.ToString(format, CultureInfo.CurrentCulture);

    public static string? Validate(string name)
    {
        if (name.Trim().Length == 0) return "The name can't be empty.";
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return "A name can't contain \\ / : * ? \" < > |";
        if (name.EndsWith(' ') || name.EndsWith('.')) return "A name can't end with a space or a dot.";
        if (name.Length > 255) return "The name is too long.";
        var stem = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem)) return $"“{stem}” is reserved by Windows.";
        return null;
    }

    private static string Normalize(string path) => path.TrimEnd('\\');

    private static string TargetPath(string path, string newName) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Normalize(path)) ?? "", newName);

    /// <summary>
    /// Renames items through the shell. When a new name is another item's current name (swaps, shifted numbering),
    /// everything goes to a temporary name first; if the second step fails, what's left goes back to its old name.
    /// </summary>
    public static async Task<BatchRenameResult> ApplyAsync(IReadOnlyList<(string Path, string NewName)> renames, IntPtr owner)
    {
        // Deepest first, so renaming a folder never invalidates an item inside it that is renamed too.
        var work = renames
            .Select(r => (Path: Normalize(r.Path), r.NewName))
            .Where(r => !string.Equals(System.IO.Path.GetFileName(r.Path), r.NewName, StringComparison.Ordinal))
            .OrderByDescending(r => r.Path.Count(c => c == '\\'))
            .ToList();
        if (work.Count == 0) return new BatchRenameResult(FileOpResult.Succeeded, []);

        var sources = work.Select(w => w.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chained = work.Any(w =>
        {
            var target = TargetPath(w.Path, w.NewName);
            return !string.Equals(target, w.Path, StringComparison.OrdinalIgnoreCase) && sources.Contains(target);
        });

        if (!chained)
        {
            var (result, map) = await RenameStepAsync(work, owner);
            return new BatchRenameResult(result, Done(work, map, null));
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        var temps = work.Select((w, i) => (w.Path, NewName: $"~eprename-{tag}-{i}")).ToList();
        var (first, toTemp) = await RenameStepAsync(temps, owner);
        var second = new List<(string Path, string NewName)>();
        foreach (var w in work)
            if (toTemp.TryGetValue(w.Path, out var t)) second.Add((t, w.NewName));
        var (result2, toFinal) = await RenameStepAsync(second, owner);

        // Put back whatever is still on a temporary name.
        var stranded = work.Where(w => toTemp.TryGetValue(w.Path, out var t) && !toFinal.ContainsKey(t))
            .Select(w => (Path: toTemp[w.Path], NewName: System.IO.Path.GetFileName(w.Path))).ToList();
        if (stranded.Count > 0) await RenameStepAsync(stranded, owner);

        var overall = first == FileOpResult.Succeeded ? result2 : first;
        return new BatchRenameResult(overall, Done(work, toTemp, toFinal));
    }

    private static List<(string, string)> Done(List<(string Path, string NewName)> work, Dictionary<string, string> step1, Dictionary<string, string>? step2)
    {
        var done = new List<(string, string)>();
        foreach (var w in work)
        {
            if (!step1.TryGetValue(w.Path, out var now)) continue;
            if (step2 is not null && !step2.TryGetValue(now, out now)) continue;
            done.Add((now, System.IO.Path.GetFileName(w.Path)));
        }
        return done;
    }

    /// <summary>One shell operation; returns old path → new path for every item it renamed.</summary>
    private static async Task<(FileOpResult, Dictionary<string, string>)> RenameStepAsync(List<(string Path, string NewName)> step, IntPtr owner)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (step.Count == 0) return (FileOpResult.Succeeded, map);
        var parsed = await StaWorkerPool.General.Run(() => step.Select(s => (Pidl: ShellNative.TryParse(s.Path), s.NewName)).ToList());
        var items = parsed.Where(p => p.Pidl is not null).Select(p => (p.Pidl!, p.NewName)).ToList();
        if (items.Count == 0) return (FileOpResult.Failed, map);
        var log = new FileOpLog();
        var result = await FileOperations.RenameManyAsync(items, owner, log);
        foreach (var e in log.TopLevel(FileOpKind.Rename))
            if (e.Source is not null && e.Result is not null) map[Normalize(e.Source)] = Normalize(e.Result);
        if (result == FileOpResult.Succeeded && items.Count < step.Count) result = FileOpResult.Failed;
        return (result, map);
    }
}
