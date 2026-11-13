using System.Text.RegularExpressions;
using ExplorerPlus.Indexing;

namespace ExplorerPlus.Core.Shell;

/// <summary>What the last load of an instant-search location found (written by the loader, read by the status bar).</summary>
public sealed class IndexSearchInfo
{
    public volatile int Total;
    public volatile int Count;
    public volatile bool Scanning;

    /// <summary>The service had more matches than it returned (before its access check).</summary>
    public bool Truncated => Total > Count && Count >= IndexSearch.Limit;
}

/// <summary>
/// Instant search: file names from the Explorer+ index service (which reads the NTFS master file table), shown as a
/// search-results location. Its <c>explorerplus-search:</c> URL keeps the query and the scope, so such tabs come back
/// with the session. When the service isn't installed or the scope isn't on an indexed drive, Windows Search
/// (<see cref="SearchLocation"/>) is used instead.
/// </summary>
public static partial class IndexSearch
{
    public const string Scheme = "explorerplus-search:";

    /// <summary>At most this many results are listed (best matches first).</summary>
    public const int Limit = 1000;

    private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(20);
    private static readonly Lock Gate = new();
    private static DateTime _statusAt;
    private static StatusResponse? _status;

    /// <summary>Whether hidden and protected files are searched; set by the app from its settings.</summary>
    public static Func<IndexSearchOptions>? Options { get; set; }

    public static bool IsUrl(string? text) => text is not null && text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Text meant for Windows Search's query language (<c>kind:music</c>, <c>date:today</c>, <c>size:&gt;1MB</c>), which
    /// the index can't answer. A drive letter (<c>C:\</c>) doesn't count.
    /// </summary>
    public static bool IsWindowsSearchQuery(string query) => PropertyRegex().IsMatch(query);

    [GeneratedRegex(@"(^|[\s(])[A-Za-z][A-Za-z.]+:", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyRegex();

    /// <summary>Forget the cached service state (after it was installed or removed).</summary>
    public static void Invalidate()
    {
        lock (Gate) _status = null;
    }

    /// <summary>The service's state, cached for a few seconds; null when it isn't running.</summary>
    public static async Task<StatusResponse?> GetStatusAsync(bool fresh = false, CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (!fresh && _status is not null && DateTime.UtcNow - _statusAt < StatusLifetime) return _status;
        }
        StatusResponse? status = null;
        if (await Task.Run(IndexClient.QueryService, ct).ConfigureAwait(false) == IndexServiceState.Running)
            status = await IndexClient.GetStatusAsync(ct).ConfigureAwait(false);
        lock (Gate)
        {
            _status = status;
            _statusAt = DateTime.UtcNow;
        }
        return status;
    }

    /// <summary>Whether instant search can look inside <paramref name="scope"/>: This PC, or a folder on an indexed drive.</summary>
    public static async Task<bool> CanSearchAsync(ShellLocation? scope)
    {
        if (scope is null || scope.IsSearch) return false;
        if (!scope.IsThisPC && scope.FileSystemPath is not { Length: >= 3 } path) return false;
        var status = await GetStatusAsync().ConfigureAwait(false);
        if (status is null) return false;
        if (scope.IsThisPC) return status.Volumes.Count > 0;
        var root = Path.GetPathRoot(scope.FileSystemPath!);
        return status.Volumes.Any(v => string.Equals(v.Root, root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The results location for <paramref name="query"/> in <paramref name="scope"/>. Makes no shell calls.</summary>
    public static ShellLocation Create(string query, ShellLocation scope)
    {
        query = query.Trim();
        var url = Scheme + "query=" + Uri.EscapeDataString(query) +
                  (scope.FileSystemPath is { } path && !scope.IsThisPC ? "&in=" + Uri.EscapeDataString(path) : "");
        return new ShellLocation
        {
            Pidl = scope.Pidl,
            DisplayName = $"Search results in {scope.DisplayName}",
            ParsingName = url,
            Attributes = scope.Attributes | Interop.SFGAO.FOLDER,
            SearchScope = scope,
            SearchQuery = query,
            IndexResults = new IndexSearchInfo(),
        };
    }

    /// <summary>Rebuilds a search from its URL (session restore, the address bar). Must run on an STA thread.</summary>
    public static ShellLocation? FromUrl(string url)
    {
        if (!IsUrl(url)) return null;
        string? query = null, scopePath = null;
        foreach (var part in url[Scheme.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string value;
            try { value = Uri.UnescapeDataString(part[(eq + 1)..]); }
            catch (UriFormatException) { continue; }
            if (part[..eq].Equals("query", StringComparison.OrdinalIgnoreCase)) query = value;
            else if (part[..eq].Equals("in", StringComparison.OrdinalIgnoreCase)) scopePath = value;
        }
        if (string.IsNullOrWhiteSpace(query)) return null;
        var pidl = ShellNative.TryParse(scopePath ?? ShellLocation.Known.ThisPC);
        return pidl is null ? null : Create(query, ShellLocation.FromPidl(pidl));
    }

    /// <summary>Asks the service, then stats the hits a chunk at a time so the first rows show at once.</summary>
    internal static IEnumerable<FolderEntry> Enumerate(ShellLocation location, CancellationToken ct)
    {
        var info = location.IndexResults!;
        var options = Options?.Invoke() ?? new IndexSearchOptions();
        var scope = location.SearchScope is { IsThisPC: false, FileSystemPath: { } path } ? path : null;
        var request = new SearchRequest(location.SearchQuery ?? "", scope, Limit, options.IncludeHidden, options.IncludeProtected);
        var response = IndexClient.SearchAsync(request, ct).GetAwaiter().GetResult()
                       ?? throw new IOException("The Explorer+ index service isn't responding.");
        if (response.Status == SearchStatus.NotIndexed) throw new IOException("This folder isn't on a drive the index service reads.");
        info.Total = response.Total;
        info.Count = response.Hits.Count;
        info.Scanning = response.Scanning;

        const int chunk = 256;
        for (var i = 0; i < response.Hits.Count; i += chunk)
        {
            ct.ThrowIfCancellationRequested();
            var paths = response.Hits.Skip(i).Take(chunk).Select(h => h.Path).ToList();
            foreach (var entry in HomeFeed.StatAll(paths, ct)) yield return entry;
        }
    }
}
