using System.Runtime.InteropServices;
using System.Text;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

/// <summary>
/// Windows Search results as a browsable location. The shell's search folder (ISearchFolderItemFactory) gives
/// exactly Explorer's search: the index where a folder is indexed, a crawl where it isn't, and the full query
/// syntax (kind:, date:, size:, ext:, author: …) through the system query parser. Results stream in through
/// IShellFolder::EnumObjects like any other virtual folder. A <c>search-ms:</c> URL names the search for the
/// address bar and sessions; the shell parses those too, but its parsed folder can't be enumerated outside
/// Explorer, so we rebuild the search from the URL ourselves.
/// </summary>
public static class SearchLocation
{
    public const string Scheme = "search-ms:";

    public static bool IsSearchUrl(string? text) => text is not null && text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when Windows Search can look inside this location (a file-system folder, or every drive from This PC).</summary>
    public static bool CanSearch(ShellLocation? scope) => scope is not null && !scope.IsSearch && (scope.IsThisPC || scope.FileSystemPath is not null);

    public static string BuildUrl(string query, IReadOnlyList<string> scopes, string displayName)
    {
        var sb = new StringBuilder(Scheme);
        sb.Append("displayname=").Append(Uri.EscapeDataString(displayName));
        sb.Append("&query=").Append(Uri.EscapeDataString(query));
        foreach (var scope in scopes) sb.Append("&crumb=location:").Append(Uri.EscapeDataString(scope));
        return sb.ToString();
    }

    public static (string? Query, List<string> Scopes) ParseUrl(string url)
    {
        string? query = null;
        var scopes = new List<string>();
        if (!IsSearchUrl(url)) return (null, scopes);
        foreach (var part in url[Scheme.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var value = Unescape(part[(eq + 1)..]);
            if (key.Equals("query", StringComparison.OrdinalIgnoreCase)) query = value;
            else if (key.Equals("crumb", StringComparison.OrdinalIgnoreCase) && value.StartsWith("location:", StringComparison.OrdinalIgnoreCase))
                scopes.Add(value["location:".Length..]);
        }
        return (query, scopes);
    }

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    /// <summary>The folders Windows Search should look in for this scope.</summary>
    public static IReadOnlyList<string> ScopePaths(ShellLocation scope)
    {
        if (scope.FileSystemPath is { } path) return [path];
        if (!scope.IsThisPC) return [];
        // Local drives only: asking for an unreachable network drive would stall the whole search.
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
    }

    /// <summary>Search results for <paramref name="query"/> inside <paramref name="scope"/>. Must run on an STA thread.</summary>
    public static ShellLocation? Create(string query, ShellLocation scope)
    {
        query = query.Trim();
        if (query.Length == 0 || !CanSearch(scope)) return null;
        var paths = ScopePaths(scope);
        if (paths.Count == 0) return null;
        var display = $"Search results in {scope.DisplayName}";
        var url = BuildUrl(query, paths, display);
        var pidl = BuildSearchFolder(query, paths, display);
        if (pidl is null) return null;
        var shell = ShellLocation.FromPidl(pidl);
        return new ShellLocation
        {
            Pidl = pidl,
            DisplayName = display,
            ParsingName = url,
            Attributes = shell.Attributes | SFGAO.FOLDER,
            SearchScope = scope,
            SearchQuery = query,
        };
    }

    private static Pidl? BuildSearchFolder(string query, IReadOnlyList<string> scopes, string displayName)
    {
        var scopePidls = scopes.Select(ShellNative.TryParse).OfType<Pidl>().ToList();
        if (scopePidls.Count == 0) return null;
        object? condition = null;
        ISearchFolderItemFactory? factory = null;
        try
        {
            condition = ParseQuery(query);
            if (condition is null) return null;
            factory = (ISearchFolderItemFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(SearchClsids.SearchFolderItemFactory, true)!)!;
            factory.SetDisplayName(displayName);
            using var array = ShellItemArray.Create(scopePidls);
            if (factory.SetScope(array.Ptr) != 0 || factory.SetCondition(condition) != 0) return null;
            return factory.GetIDList(out var pidl) == 0 && pidl != IntPtr.Zero ? Pidl.TakeNative(pidl) : null;
        }
        catch (COMException) { return null; }
        finally
        {
            ShellNative.Release(factory);
            ShellNative.Release(condition);
        }
    }

    /// <summary>Turns Explorer search-box text into a search condition, with Explorer's own parser and keywords.</summary>
    private static object? ParseQuery(string query)
    {
        IQueryParserManager? manager = null;
        object? parser = null;
        IQuerySolution? solution = null;
        object? raw = null;
        try
        {
            manager = (IQueryParserManager)Activator.CreateInstance(Type.GetTypeFromCLSID(SearchClsids.QueryParserManager, true)!)!;
            var iid = SearchClsids.IID_IQueryParser;
            if (manager.CreateLoadedParser("SystemIndex", SearchClsids.LANG_USER_DEFAULT, ref iid, out parser) != 0) return null;
            var qp = (IQueryParser)parser;
            manager.InitializeOptions(false, true, qp);
            if (qp.Parse(query, IntPtr.Zero, out solution) != 0 || solution is null) return null;
            if (solution.GetQuery(out raw, IntPtr.Zero) != 0 || raw is null) return null;
            var now = SYSTEMTIME.Now();
            return solution.Resolve(raw, SearchClsids.SQRO_DONT_SPLIT_WORDS, ref now, out var resolved) == 0 ? resolved : null;
        }
        finally
        {
            ShellNative.Release(raw);
            ShellNative.Release(solution);
            ShellNative.Release(parser);
            ShellNative.Release(manager);
        }
    }

    /// <summary>Rebuilds a search from its URL (typed in the address bar, restored from a session). Must run on an STA thread.</summary>
    public static ShellLocation? FromUrl(string url)
    {
        var (query, scopes) = ParseUrl(url);
        ShellLocation? scope = null;
        if (scopes.Count == 1 && ShellNative.TryParse(scopes[0]) is { } scopePidl) scope = ShellLocation.FromPidl(scopePidl);
        else if (scopes.Count > 1 && ShellNative.TryParse(ShellLocation.Known.ThisPC) is { } pc) scope = ShellLocation.FromPidl(pc);
        if (query is not null && scope is not null && Create(query, scope) is { } search) return search;

        return null;
    }
}
