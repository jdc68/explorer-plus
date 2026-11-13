using System.IO;

namespace ExplorerPlus.App.Services;

public static class Tags
{
    // raised on the UI thread after tags were added, removed or renamed
    public static event Action? Changed;

    private static Dictionary<string, List<string>> Map => AppSettings.Current.Tags;

    public static IReadOnlyList<string> Names =>
        Map.Values.SelectMany(t => t).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    public static IReadOnlyList<string> Of(string? path) =>
        path is not null && Map.TryGetValue(Normalize(path), out var tags) ? tags : [];

    public static bool Has(string? path, string tag) => Of(path).Contains(tag, StringComparer.CurrentCultureIgnoreCase);

    public static IReadOnlyList<string> PathsWith(string tag) =>
        Map.Where(p => p.Value.Contains(tag, StringComparer.CurrentCultureIgnoreCase)).Select(p => p.Key).ToList();

    // tags the items, or untags them when all of them already have it; true when they now have it
    public static bool Toggle(IReadOnlyList<string> paths, string tag)
    {
        var add = !paths.All(p => Has(p, tag.Trim()));
        Set(paths, tag, add);
        return add;
    }

    public static void Set(IReadOnlyList<string> paths, string tag, bool add)
    {
        tag = tag.Trim();
        if (paths.Count == 0 || tag.Length == 0) return;
        foreach (var path in paths.Select(Normalize))
        {
            if (add)
            {
                if (!Map.TryGetValue(path, out var list)) Map[path] = list = [];
                if (!list.Contains(tag, StringComparer.CurrentCultureIgnoreCase)) list.Add(tag);
            }
            else if (Map.TryGetValue(path, out var list))
            {
                list.RemoveAll(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase));
                if (list.Count == 0) Map.Remove(path);
            }
        }
        Commit();
    }

    public static void RemoveTag(string tag)
    {
        foreach (var path in Map.Keys.ToList())
        {
            var list = Map[path];
            if (list.RemoveAll(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase)) > 0 && list.Count == 0) Map.Remove(path);
        }
        Commit();
    }

    // keeps the tags of an item (and of everything inside a folder) that was renamed or moved by us
    public static void Rename(string oldPath, string newPath)
    {
        string from = Normalize(oldPath), to = Normalize(newPath);
        var changed = false;
        foreach (var path in Map.Keys.ToList())
        {
            string? moved = null;
            if (string.Equals(path, from, StringComparison.OrdinalIgnoreCase)) moved = to;
            else if (path.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)) moved = to + path[from.Length..];
            if (moved is null) continue;
            var tags = Map[path];
            Map.Remove(path);
            Map[moved] = tags;
            changed = true;
        }
        if (changed) Commit();
    }

    private static void Commit()
    {
        AppSettings.Current.Save();
        Changed?.Invoke();
    }

    private static string Normalize(string path)
    {
        var p = Path.GetFullPath(path.Trim());
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }
}
