namespace ExplorerPlus.Core.Shell;

public sealed class FolderVisit
{
    public string Path { get; set; } = "";
    public int Count { get; set; }
    public DateTime LastVisit { get; set; }
}

/// <summary>
/// Explorer+'s own folder history for the frequent folders in Quick access: a visit count and the last visit per
/// folder, ranked by how often and how recently each was opened.
/// </summary>
public static class FolderHistory
{
    public const int Limit = 200;

    // Shell folder that lists File Explorer's pinned and frequent folders
    public const string ExplorerFrequentPlaces = "shell:::{3936E9E4-D92C-4EEE-A85A-BC16D5EA0819}";

    private static readonly string[] TempFolders = GetTempFolders();

    /// <summary>Counts a visit; false when the folder is one that never shows as frequent.</summary>
    public static bool Record(List<FolderVisit> visits, string path, DateTime now)
    {
        path = Normalize(path);
        if (IsExcluded(path)) return false;
        var visit = visits.Find(v => Same(v.Path, path));
        if (visit is null)
        {
            visit = new FolderVisit { Path = path };
            visits.Add(visit);
        }
        visit.Count++;
        visit.LastVisit = now;
        if (visits.Count > Limit)
        {
            var weakest = visits.Where(v => v != visit).MinBy(v => Score(v, now))!;
            visits.Remove(weakest);
        }
        return true;
    }

    public static bool Remove(List<FolderVisit> visits, string path) => visits.RemoveAll(v => Same(v.Path, path)) > 0;

    /// <summary>Visits weighted by age, as browsers rank history: recent visits count for more.</summary>
    public static double Score(FolderVisit visit, DateTime now)
    {
        var age = now - visit.LastVisit;
        var weight = age.TotalHours < 4 ? 4.0
            : age.TotalDays < 1 ? 2.0
            : age.TotalDays < 7 ? 1.0
            : age.TotalDays < 30 ? 0.5
            : 0.25;
        return visit.Count * weight;
    }

    /// <summary>Folder paths, most frequent first, without the ones that never show.</summary>
    public static List<string> Ranked(IEnumerable<FolderVisit> visits, DateTime now) =>
        visits.Where(v => !IsExcluded(v.Path))
            .OrderByDescending(v => Score(v, now))
            .ThenByDescending(v => v.LastVisit)
            .Select(v => v.Path)
            .ToList();

    /// <summary>Recycle Bin, temp folders and drive roots (the sidebar lists drives already).</summary>
    public static bool IsExcluded(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var p = Normalize(path);
        if (p.Length <= 3 || p.StartsWith(@"\\?\", StringComparison.Ordinal)) return true;
        if (p.Contains(@"\$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var temp in TempFolders)
            if (Same(p, temp) || p.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return ShellFilter.Default.IsBlocked(null, p);
    }

    /// <summary>First history from File Explorer's frequent list, in its order (pins left out).</summary>
    public static List<FolderVisit> Seed(IReadOnlyList<string> explorerFrequent, DateTime now)
    {
        var visits = new List<FolderVisit>();
        for (var i = 0; i < explorerFrequent.Count; i++)
        {
            var path = Normalize(explorerFrequent[i]);
            if (IsExcluded(path) || visits.Any(v => Same(v.Path, path))) continue;
            visits.Add(new FolderVisit { Path = path, Count = Math.Max(1, 4 - i / 3), LastVisit = now.AddMinutes(-i) });
        }
        return visits;
    }

    /// <summary>File Explorer's frequent folders, the ones it added itself. Runs on an STA thread.</summary>
    public static List<string> ReadExplorerFrequent()
    {
        var result = new List<string>();
        var folder = ShellNative.TryParse(ExplorerFrequentPlaces);
        if (folder is null) return result;
        foreach (var entry in ShellFolderEnumerator.Enumerate(folder))
        {
            if (!entry.IsFolder || entry.Path is null || entry.Pidl is null) continue;
            var pinned = PropertyReader.Read(null, entry.Pidl, ["System.Home.IsPinned"]);
            if (pinned.TryGetValue("System.Home.IsPinned", out var v) && v.Sort is long b && b != 0) continue;
            result.Add(entry.Path);
        }
        return result;
    }

    public static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    private static string[] GetTempFolders() =>
        new[] { System.IO.Path.GetTempPath(), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
