using ExplorerPlus.Indexing;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public enum RecentSpan { FifteenMinutes, Hour, Today, Week }

public static class RecentChanges
{
    public const string Scheme = "explorerplus-recent:";
    public const int Limit = 10_000;

    private static readonly (RecentSpan Span, string Key)[] Keys =
        [(RecentSpan.FifteenMinutes, "15m"), (RecentSpan.Hour, "1h"), (RecentSpan.Today, "today"), (RecentSpan.Week, "7d")];

    public static bool IsUrl(string? text) => text?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static string Url(RecentSpan span, string? folder = null, bool includeAppData = false) =>
        Scheme + "since=" + Keys.First(k => k.Span == span).Key +
        (folder is null ? "" : "&in=" + Uri.EscapeDataString(folder)) +
        (includeAppData ? "&appdata=1" : "");

    /// <summary>The results location for changes in <paramref name="scope"/> (This PC or a folder). Makes no shell calls.</summary>
    public static ShellLocation Create(RecentSpan span, ShellLocation scope, bool includeAppData = false)
    {
        var folder = scope.IsThisPC ? null : scope.FileSystemPath;
        return new ShellLocation
        {
            Pidl = scope.Pidl,
            DisplayName = folder is null ? "Recent changes" : $"Recent changes in {scope.DisplayName}",
            ParsingName = Url(span, folder, includeAppData),
            Attributes = scope.Attributes | SFGAO.FOLDER,
            SearchScope = scope,
            RecentSpan = span,
            RecentIncludesAppData = includeAppData,
            RecentResults = new IndexSearchInfo { Limit = Limit },
        };
    }

    // must run on an STA thread
    public static ShellLocation? FromUrl(string url)
    {
        if (!IsUrl(url)) return null;
        var span = RecentSpan.Hour;
        string? folder = null;
        var appData = false;
        foreach (var part in url[Scheme.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string value;
            try { value = Uri.UnescapeDataString(part[(eq + 1)..]); }
            catch (UriFormatException) { continue; }
            switch (part[..eq].ToLowerInvariant())
            {
                case "since":
                    foreach (var (s, key) in Keys)
                        if (key.Equals(value, StringComparison.OrdinalIgnoreCase)) span = s;
                    break;
                case "in": folder = value; break;
                case "appdata": appData = value == "1"; break;
            }
        }
        var pidl = ShellNative.TryParse(folder ?? ShellLocation.Known.ThisPC);
        return pidl is null ? null : Create(span, ShellLocation.FromPidl(pidl), appData);
    }

    public static DateTime Start(RecentSpan span, DateTime now) => span switch
    {
        RecentSpan.FifteenMinutes => now.AddMinutes(-15),
        RecentSpan.Hour => now.AddHours(-1),
        RecentSpan.Today => now.Date,
        _ => now.Date.AddDays(-6),
    };

    // newest first, in the order the service gives (files by the newer of modified and created, folders by created)
    internal static IEnumerable<FolderEntry> Enumerate(ShellLocation location, CancellationToken ct)
    {
        var info = location.RecentResults!;
        var options = IndexSearch.Options?.Invoke() ?? new IndexSearchOptions();
        var scope = location.SearchScope is { IsThisPC: false, FileSystemPath: { } path } ? path : null;
        var since = Start(location.RecentSpan!.Value, DateTime.Now).ToFileTime();
        var request = new RecentRequest(scope, since, Limit, options.IncludeHidden, options.IncludeProtected, location.RecentIncludesAppData);
        var response = IndexClient.RecentAsync(request, ct).GetAwaiter().GetResult() ?? throw new IOException(WhyNoAnswer(ct));
        if (response.Status == SearchStatus.NotIndexed) throw new IOException("This folder isn't on a drive the index service reads.");
        info.Total = response.Total;
        info.Count = response.Hits.Count;
        info.Scanning = response.Scanning;
        var order = 0;
        foreach (var hit in response.Hits)
        {
            ct.ThrowIfCancellationRequested();
            if (FastFolderEnumerator.FromIndex(hit.Path, hit.Attributes, hit.Size, hit.Modified, hit.Created) is not { } entry) continue;
            entry.SectionOrder = order++;
            yield return entry;
        }
    }

    private static string WhyNoAnswer(CancellationToken ct)
    {
        // a service from before recent changes still answers a status request
        var status = IndexSearch.GetStatusAsync(fresh: true, ct).GetAwaiter().GetResult();
        return status switch
        {
            null => "Recent changes come from the index service, which isn't running. Turn it on in Settings ▸ Instant search.",
            { Speaks: false } => "The index service is an older version. Update it in Settings ▸ Instant search to see recent changes.",
            _ => "The index service didn't answer.",
        };
    }
}
