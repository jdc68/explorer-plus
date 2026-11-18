using ExplorerPlus.Indexing;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public static class FlatView
{
    public const string Scheme = "explorerplus-flat:";
    public const int Limit = 10_000;
    private const int MaxDepth = 128;

    /// <summary>The extensions (no dot) of a kind such as "pictures"; set by the app from its view rules.</summary>
    public static Func<string, IReadOnlyCollection<string>?>? KindExtensions { get; set; }

    public static bool IsUrl(string? text) => text?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static string Url(string folder, string kind = "") =>
        Scheme + "in=" + Uri.EscapeDataString(folder) + (kind.Length > 0 ? "&kind=" + Uri.EscapeDataString(kind) : "");

    /// <summary>Every file below <paramref name="folder"/>, or only one kind of them. Makes no shell calls.</summary>
    public static ShellLocation Create(ShellLocation folder, string kind = "") => new()
    {
        Pidl = folder.Pidl,
        DisplayName = $"{folder.DisplayName} (flat view)",
        ParsingName = Url(folder.FileSystemPath!, kind),
        Attributes = folder.Attributes | SFGAO.FOLDER,
        SearchScope = folder,
        FlatKind = kind,
        FlatResults = new IndexSearchInfo { Limit = Limit },
    };

    // must run on an STA thread
    public static ShellLocation? FromUrl(string url)
    {
        if (!IsUrl(url)) return null;
        string? folder = null;
        var kind = "";
        foreach (var part in url[Scheme.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string value;
            try { value = Uri.UnescapeDataString(part[(eq + 1)..]); }
            catch (UriFormatException) { continue; }
            switch (part[..eq].ToLowerInvariant())
            {
                case "in": folder = value; break;
                case "kind": kind = value; break;
            }
        }
        if (folder is null || ShellNative.TryParse(folder) is not { } pidl) return null;
        var location = ShellLocation.FromPidl(pidl);
        return location.IsFileSystemFolder ? Create(location, kind) : null;
    }

    // in path order: a folder's files by name, then each subfolder in turn; each folder is its own section
    internal static IEnumerable<FolderEntry> Enumerate(ShellLocation location, CancellationToken ct, bool useIndex = true)
    {
        var info = location.FlatResults!;
        var root = location.SearchScope!.FileSystemPath!.TrimEnd('\\');
        var label = location.SearchScope.DisplayName;
        var options = IndexSearch.Options?.Invoke() ?? new IndexSearchOptions();
        var extensions = location.FlatKind is { Length: > 0 } kind ? KindExtensions?.Invoke(kind) : null;
        var entries = (useIndex ? FromIndex(root, extensions, options, info, ct) : null) ?? Walk(root, extensions, options, info, ct);
        string? folder = null, section = null;
        var rank = -1;
        foreach (var entry in entries)
        {
            var parent = Path.GetDirectoryName(entry.Path!)!.TrimEnd('\\');
            if (!string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase))
            {
                folder = parent;
                section = parent.Length <= root.Length ? label : parent[(root.Length + 1)..];
                rank++;
            }
            entry.Section = section;
            entry.SectionRank = rank;
            yield return entry;
        }
    }

    // null when the service can't answer for this drive, so the folders are walked instead
    private static IEnumerable<FolderEntry>? FromIndex(string root, IReadOnlyCollection<string>? extensions, IndexSearchOptions options,
        IndexSearchInfo info, CancellationToken ct)
    {
        var status = IndexSearch.GetStatusAsync(ct: ct).GetAwaiter().GetResult();
        var drive = Path.GetPathRoot(root + "\\");
        if (status is not { HasFiles: true } ||
            !status.Volumes.Any(v => v.State == VolumeState.Ready && string.Equals(v.Root, drive, StringComparison.OrdinalIgnoreCase)))
            return null;
        var request = new FilesRequest(root.Length <= 2 ? root + "\\" : root, extensions?.ToList(), Limit, options.IncludeHidden, options.IncludeProtected);
        var response = IndexClient.FilesAsync(request, ct).GetAwaiter().GetResult();
        if (response is not { Status: SearchStatus.Ok }) return null;
        info.Total = response.Total;
        info.Count = response.Hits.Count;
        info.Scanning = response.Scanning;
        return IndexSearch.ToEntries(response.Hits, ct);
    }

    private static IEnumerable<FolderEntry> Walk(string root, IReadOnlyCollection<string>? extensions, IndexSearchOptions options,
        IndexSearchInfo info, CancellationToken ct)
    {
        var wanted = extensions is null ? null : new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        info.Total = -1;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            List<FolderEntry> listed;
            try { listed = FastFolderEnumerator.Enumerate(dir.Length <= 2 ? dir + "\\" : dir, ct).ToList(); }
            catch (Exception ex) when (ex is not OperationCanceledException && depth > 0) { continue; }
            foreach (var file in listed.Where(e => !e.IsFolder && Visible(e, options) && (wanted is null || wanted.Contains(e.Extension.TrimStart('.'))))
                         .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (count == Limit)
                {
                    info.Count = count;
                    yield break;
                }
                count++;
                yield return file;
            }
            if (depth + 1 >= MaxDepth) continue;
            // junctions and links aren't followed (they can loop), as the index doesn't follow them either
            foreach (var sub in listed.Where(e => e.IsFolder && (e.FileAttributes & FileAttr.REPARSE_POINT) == 0 && Visible(e, options) &&
                                                  !(depth == 0 && root.Length <= 2 && IsExcluded(e.Name)))
                         .OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase))
                stack.Push((sub.Path!, depth + 1));
        }
        info.Total = info.Count = count;
    }

    private static bool Visible(FolderEntry e, IndexSearchOptions options) =>
        (options.IncludeHidden || !e.IsHidden) && (options.IncludeProtected || !e.IsProtectedSystem);

    private static bool IsExcluded(string name) =>
        name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
}
