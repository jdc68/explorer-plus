using ExplorerPlus.Indexing;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public sealed record CleanupQuery(CleanupKind Kind, int Years = CleanupList.DefaultYears, int MinMegabytes = CleanupList.DefaultMegabytes,
    bool IncludeSystem = false);

public static class CleanupList
{
    public const string Scheme = "explorerplus-cleanup:";
    public const int LargestLimit = 1_000;
    public const int Limit = 10_000;
    public const int DefaultYears = 2;
    public const int DefaultMegabytes = 10;
    private const int MaxDepth = 64;

    private static readonly (CleanupKind Kind, string Key)[] Keys =
        [(CleanupKind.Largest, "largest"), (CleanupKind.Old, "old"), (CleanupKind.Empty, "empty")];

    public static bool IsUrl(string? text) => text?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static int LimitOf(CleanupKind kind) => kind == CleanupKind.Largest ? LargestLimit : Limit;

    public static string Url(CleanupQuery query, string? folder = null) =>
        Scheme + "list=" + Keys.First(k => k.Kind == query.Kind).Key +
        (query.Kind == CleanupKind.Old ? $"&years={query.Years}&mb={query.MinMegabytes}" : "") +
        (folder is null ? "" : "&in=" + Uri.EscapeDataString(folder)) +
        (query.IncludeSystem ? "&system=1" : "");

    public static string Title(CleanupKind kind) => kind switch
    {
        CleanupKind.Largest => "Largest files",
        CleanupKind.Old => "Old files",
        _ => "Empty folders",
    };

    /// <summary>The results location for one clean-up list in <paramref name="scope"/> (This PC or a folder). Makes no shell calls.</summary>
    public static ShellLocation Create(CleanupQuery query, ShellLocation scope)
    {
        var folder = scope.IsThisPC ? null : scope.FileSystemPath;
        return new ShellLocation
        {
            Pidl = scope.Pidl,
            DisplayName = folder is null ? Title(query.Kind) : $"{Title(query.Kind)} in {scope.DisplayName}",
            ParsingName = Url(query, folder),
            Attributes = scope.Attributes | SFGAO.FOLDER,
            SearchScope = scope,
            Cleanup = query,
            CleanupResults = new IndexSearchInfo { Limit = LimitOf(query.Kind) },
        };
    }

    // must run on an STA thread
    public static ShellLocation? FromUrl(string url)
    {
        if (!IsUrl(url)) return null;
        var kind = CleanupKind.Largest;
        int years = DefaultYears, megabytes = DefaultMegabytes;
        string? folder = null;
        var system = false;
        foreach (var part in url[Scheme.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string value;
            try { value = Uri.UnescapeDataString(part[(eq + 1)..]); }
            catch (UriFormatException) { continue; }
            switch (part[..eq].ToLowerInvariant())
            {
                case "list":
                    foreach (var (k, key) in Keys)
                        if (key.Equals(value, StringComparison.OrdinalIgnoreCase)) kind = k;
                    break;
                case "years" when int.TryParse(value, out var y) && y is > 0 and <= 100: years = y; break;
                case "mb" when int.TryParse(value, out var mb) && mb is >= 0 and <= 1_000_000: megabytes = mb; break;
                case "in": folder = value; break;
                case "system": system = value == "1"; break;
            }
        }
        var pidl = ShellNative.TryParse(folder ?? ShellLocation.Known.ThisPC);
        return pidl is null ? null : Create(new CleanupQuery(kind, years, megabytes, system), ShellLocation.FromPidl(pidl));
    }

    public static long Before(int years, DateTime now) => now.AddYears(-years).ToFileTime();

    // in the service's order: files biggest first, empty folders by path
    internal static IEnumerable<FolderEntry> Enumerate(ShellLocation location, CancellationToken ct)
    {
        var info = location.CleanupResults!;
        var query = location.Cleanup!;
        var status = IndexSearch.GetStatusAsync(fresh: true, ct).GetAwaiter().GetResult();
        if (status is not { HasCleanup: true })
            throw new IOException(status is null
                ? "Clean up uses the index service, which isn't running. Turn it on in Settings ▸ Instant search."
                : "The index service is an older version. Update it in Settings ▸ Instant search to clean up.");
        var scope = location.SearchScope is { IsThisPC: false, FileSystemPath: { } path } ? path : null;
        var request = new CleanupRequest(scope, query.Kind, Before(query.Years, DateTime.Now), query.MinMegabytes * 1024L * 1024,
            LimitOf(query.Kind), query.IncludeSystem);
        var response = IndexClient.CleanupAsync(request, ct).GetAwaiter().GetResult() ?? throw new IOException("The index service didn't answer.");
        if (response.Status == SearchStatus.NotIndexed) throw new IOException("This folder isn't on a drive the index service reads.");
        info.Total = response.Total;
        info.Count = response.Hits.Count;
        info.Scanning = response.Scanning;
        var order = 0;
        var hits = query.Kind == CleanupKind.Empty ? response.Hits.Where(h => IsEmptyOnDisk(h.Path, 0, ct)).ToList() : response.Hits;
        foreach (var entry in IndexSearch.ToEntries(hits, ct))
        {
            entry.SectionOrder = order++;
            yield return entry;
        }
    }

    // the index has one name per file, so a folder holding only a second hard link looks empty there; and it may lag
    internal static bool IsEmptyOnDisk(string folder, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth) return false;
        try
        {
            foreach (var entry in FastFolderEnumerator.Enumerate(folder, ct))
            {
                if (!entry.IsFolder || (entry.FileAttributes & FileAttr.REPARSE_POINT) != 0) return false;
                if (!IsEmptyOnDisk(entry.Path!, depth + 1, ct)) return false;
            }
            return Directory.Exists(folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }
}
