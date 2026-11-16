namespace ExplorerPlus.Core.Shell;

/// <summary>Windows' own Quick access (what File Explorer and the Open/Save dialogs show as pinned). Runs on an STA thread.</summary>
public static class WindowsQuickAccess
{
    public const string HomeFolder = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    public static List<string> ReadPinned()
    {
        var result = new List<string>();
        var home = ShellNative.TryParse(HomeFolder);
        if (home is null) return result;
        foreach (var entry in ShellFolderEnumerator.Enumerate(home))
        {
            if (entry.IsFolder && entry.Path is not null && entry.Pidl is not null && IsPinned(entry.Pidl))
                result.Add(entry.Path);
        }
        return result;
    }

    public static bool IsPinned(Pidl pidl) =>
        PropertyReader.Read(null, pidl, ["System.Home.IsPinned"]).TryGetValue("System.Home.IsPinned", out var v)
        && v.Sort is long b && b != 0;

    public static (List<string> Pin, List<string> Unpin) Plan(IEnumerable<string> wanted, IReadOnlyCollection<string> pinned, Func<string, bool> exists)
    {
        var target = wanted.ToList();
        var pin = target
            .Where(w => exists(w) && !pinned.Any(p => FolderHistory.Same(p, w)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unpin = pinned.Where(p => !target.Any(w => FolderHistory.Same(w, p))).ToList();
        return (pin, unpin);
    }

    public static void Apply(IReadOnlyCollection<string> pin, IReadOnlyCollection<string> unpin)
    {
        if (unpin.Count > 0) UnpinListed(unpin);
        foreach (var path in pin) Verb(ShellNative.TryParse(path), "pintohome");
    }

    // the unpin verb is only offered on the item inside Quick access, not on the plain folder
    private static void UnpinListed(IReadOnlyCollection<string> paths)
    {
        var home = ShellNative.TryParse(HomeFolder);
        if (home is null) return;
        foreach (var entry in ShellFolderEnumerator.Enumerate(home))
        {
            var path = entry.Path;
            if (!entry.IsFolder || path is null || entry.Pidl is null) continue;
            if (paths.Any(p => FolderHistory.Same(p, path)) && IsPinned(entry.Pidl)) Verb(entry.Pidl, "unpinfromhome");
        }
    }

    private static void Verb(Pidl? pidl, string verb)
    {
        if (pidl is null) return;
        using var menu = ShellContextMenu.ForItems([pidl], IntPtr.Zero, null);
        menu?.InvokeVerb(verb, IntPtr.Zero);
    }
}
