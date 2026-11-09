namespace ExplorerPlus.App.ViewModels;

public static class Grouping
{
    /// <summary>Sets GroupKey and GroupRank on every item, using Explorer's group names for the built-in columns.</summary>
    public static void Assign(IReadOnlyList<ItemViewModel> items, string groupBy)
    {
        var now = DateTime.Now;
        switch (groupBy)
        {
            case ColumnCatalog.Section:
                foreach (var i in items) (i.GroupKey, i.GroupRank) = (i.Entry.Section ?? "Other", i.Entry.Section is null ? 99 : i.Entry.SectionRank);
                break;
            case ColumnCatalog.Name:
                foreach (var i in items) (i.GroupKey, i.GroupRank) = NameGroup(i.DisplayName);
                break;
            case ColumnCatalog.Modified:
                foreach (var i in items) (i.GroupKey, i.GroupRank) = DateGroup(i.Entry.Modified, now);
                break;
            case ColumnCatalog.Created:
                foreach (var i in items) (i.GroupKey, i.GroupRank) = DateGroup(i.Entry.Created, now);
                break;
            case ColumnCatalog.Size:
                foreach (var i in items) (i.GroupKey, i.GroupRank) = SizeGroup(i.SortSize, i.IsFolder);
                break;
            case ColumnCatalog.Type:
                RankByText(items, i => string.IsNullOrEmpty(i.TypeName) ? "Unspecified" : i.TypeName);
                break;
            default:
                RankByText(items, i => i.GetPropertyText(groupBy) is { Length: > 0 } t ? t : "Unspecified");
                break;
        }
    }

    private static void RankByText(IReadOnlyList<ItemViewModel> items, Func<ItemViewModel, string> key)
    {
        foreach (var i in items) i.GroupKey = key(i);
        var order = items.Select(i => i.GroupKey).Distinct().OrderBy(k => k == "Unspecified" ? 1 : 0)
            .ThenBy(k => k, StringComparer.CurrentCultureIgnoreCase).Select((k, n) => (k, n)).ToDictionary(t => t.k, t => t.n);
        foreach (var i in items) i.GroupRank = order[i.GroupKey];
    }

    private static (string, int) NameGroup(string name)
    {
        var c = name.Length > 0 ? char.ToUpperInvariant(name[0]) : ' ';
        return c switch
        {
            >= '0' and <= '9' => ("0 – 9", 0),
            >= 'A' and <= 'H' => ("A – H", 1),
            >= 'I' and <= 'P' => ("I – P", 2),
            >= 'Q' and <= 'Z' => ("Q – Z", 3),
            _ when char.IsLetter(c) => ("Other letters", 4),
            _ => ("Other", 5),
        };
    }

    private static (string, int) DateGroup(DateTime? value, DateTime now)
    {
        if (value is not { } d) return ("Unspecified", 9);
        var today = now.Date;
        var startOfWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        var startOfMonth = new DateTime(today.Year, today.Month, 1);
        if (d >= today.AddDays(1)) return ("In the future", 0);
        if (d >= today) return ("Today", 1);
        if (d >= today.AddDays(-1)) return ("Yesterday", 2);
        if (d >= startOfWeek) return ("Earlier this week", 3);
        if (d >= startOfWeek.AddDays(-7)) return ("Last week", 4);
        if (d >= startOfMonth) return ("Earlier this month", 5);
        if (d >= startOfMonth.AddMonths(-1)) return ("Last month", 6);
        if (d.Year == today.Year) return ("Earlier this year", 7);
        return ("A long time ago", 8);
    }

    private static (string, int) SizeGroup(long size, bool folder) => size switch
    {
        < 0 => (folder ? "Folders" : "Unspecified", 8),
        0 => ("Empty (0 KB)", 0),
        <= 16 * 1024 => ("Tiny (0 – 16 KB)", 1),
        <= 1024 * 1024 => ("Small (16 KB – 1 MB)", 2),
        <= 128L * 1024 * 1024 => ("Medium (1 – 128 MB)", 3),
        <= 1024L * 1024 * 1024 => ("Large (128 MB – 1 GB)", 4),
        <= 4L * 1024 * 1024 * 1024 => ("Huge (1 – 4 GB)", 5),
        _ => ("Gigantic (> 4 GB)", 6),
    };
}
