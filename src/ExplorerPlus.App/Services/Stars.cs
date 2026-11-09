using System.IO;

namespace ExplorerPlus.App.Services;

/// <summary>Starred files and folders: Explorer's "Favorites", kept locally (no cloud account involved).</summary>
public static class Stars
{
    private static HashSet<string>? _set;

    /// <summary>Raised on the UI thread after items were starred or unstarred.</summary>
    public static event Action? Changed;

    private static HashSet<string> Set =>
        _set ??= new HashSet<string>(AppSettings.Current.Starred.Select(Normalize), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> All => AppSettings.Current.Starred;

    public static bool IsStarred(string? path) => path is not null && Set.Contains(Normalize(path));

    /// <summary>Stars the items, or unstars them when all of them are already starred. Returns true when they are now starred.</summary>
    public static bool Toggle(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        var star = !paths.All(IsStarred);
        if (star) Add(paths); else Remove(paths);
        return star;
    }

    public static void Add(IEnumerable<string> paths)
    {
        var list = AppSettings.Current.Starred;
        var changed = false;
        foreach (var p in paths.Select(Normalize))
            if (Set.Add(p)) { list.Add(p); changed = true; }
        if (changed) Commit();
    }

    public static void Remove(IEnumerable<string> paths)
    {
        var gone = paths.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (AppSettings.Current.Starred.RemoveAll(p => gone.Contains(Normalize(p))) == 0) return;
        _set = null;
        Commit();
    }

    /// <summary>Keeps a star on an item that was renamed or moved by us.</summary>
    public static void Rename(string oldPath, string newPath)
    {
        var list = AppSettings.Current.Starred;
        var i = list.FindIndex(p => string.Equals(Normalize(p), Normalize(oldPath), StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        list[i] = Normalize(newPath);
        _set = null;
        Commit();
    }

    private static void Commit()
    {
        AppSettings.Current.Save();
        Changed?.Invoke();
    }

    private static string Normalize(string path)
    {
        var p = path.Trim();
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }
}
