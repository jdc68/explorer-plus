using System.Runtime.InteropServices;
using ExplorerPlus.Interop;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace ExplorerPlus.Core.Shell;

/// <param name="QuickAccess">Folders pinned by the user (plus the standard user folders), in display order.</param>
/// <param name="Starred">Starred files and folders, in the order they were starred.</param>
/// <param name="IncludeRecent">Whether to list recently opened files.</param>
/// <param name="RecentFolder">Where Windows keeps its recent-item shortcuts (defaults to the user's Recent folder).</param>
public sealed record HomeFeedOptions(
    IReadOnlyList<string> QuickAccess,
    IReadOnlyList<string> Starred,
    bool IncludeRecent,
    string? RecentFolder = null,
    int RecentLimit = 40);

/// <summary>
/// Content of Explorer+'s Home page: Quick access, Starred and Recent files. Unlike Explorer's Home there are no
/// "Recommended" or "Shared" cloud feeds and no automatically added frequent folders. Everything listed is a
/// real file-system item, so every action (open, menus, drag, rename, delete) works as in a folder.
/// </summary>
public static class HomeFeed
{
    public const string QuickAccessSection = "Quick access";
    public const string StarredSection = "Starred";
    public const string RecentSection = "Recent files";

    /// <summary>A path that doesn't answer in this time (offline share, sleeping NAS) is left out of this listing.</summary>
    private static readonly TimeSpan StatTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>Runs on an STA thread (shortcuts are read through the shell).</summary>
    public static IEnumerable<FolderEntry> Enumerate(HomeFeedOptions options, CancellationToken ct = default)
    {
        var result = new List<FolderEntry>();
        AddSection(result, StatAll(options.QuickAccess, ct), QuickAccessSection, 0);
        ct.ThrowIfCancellationRequested();
        AddSection(result, StatAll(options.Starred, ct), StarredSection, 1);
        ct.ThrowIfCancellationRequested();
        if (options.IncludeRecent)
            AddSection(result, RecentFiles(options.RecentFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Recent), options.RecentLimit, ct), RecentSection, 2);
        return result;
    }

    private static void AddSection(List<FolderEntry> into, IEnumerable<FolderEntry> entries, string section, int rank)
    {
        var order = 0;
        foreach (var e in entries)
        {
            e.Section = section;
            e.SectionRank = rank;
            e.SectionOrder = order++;
            into.Add(e);
        }
    }

    /// <summary>Stats every path in parallel and keeps the input order; missing or unreachable paths are skipped.</summary>
    internal static List<FolderEntry> StatAll(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var distinct = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tasks = distinct.Select(p => Task.Run(() => StatOne(p), ct)).ToArray();
        try { Task.WaitAll(tasks, (int)StatTimeout.TotalMilliseconds, ct); }
        catch (AggregateException) { }
        return tasks.Where(t => t.IsCompletedSuccessfully && t.Result is not null).Select(t => t.Result!).ToList();
    }

    private static FolderEntry? StatOne(string path)
    {
        var trimmed = path.TrimEnd('\\');
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            // A drive root has no directory entry of its own to read.
            var root = trimmed + "\\";
            if (!Directory.Exists(root)) return null;
            return new FolderEntry
            {
                Name = root,
                DisplayName = root,
                Path = root,
                IsFolder = true,
                FileAttributes = FileAttr.DIRECTORY,
                TypeName = "Drive",
                IconIndex = ShellInfoCache.GetIconIndexForPath(root),
            };
        }
        return FastFolderEnumerator.Stat(trimmed);
    }

    /// <summary>Files from the Recent folder's shortcuts, newest first, skipping folders, duplicates and missing targets.</summary>
    internal static List<FolderEntry> RecentFiles(string recentFolder, int limit, CancellationToken ct)
    {
        if (limit <= 0 || !Directory.Exists(recentFolder)) return [];
        var links = FastFolderEnumerator.Enumerate(recentFolder, ct)
            .Where(e => !e.IsFolder && e.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Modified ?? DateTime.MinValue)
            .Take(limit * 4)
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Target, string Link)>();
        foreach (var link in links)
        {
            ct.ThrowIfCancellationRequested();
            var target = ReadLinkTarget(link.Path!);
            if (target is null || !seen.Add(target)) continue;
            // Folders show up in Recent too, but Home lists files only (folders belong in Quick access).
            if (Path.GetExtension(target).Length == 0 && Directory.Exists(target)) continue;
            candidates.Add((target, link.Path!));
            if (candidates.Count >= limit * 2) break;
        }

        var stats = StatAll(candidates.Select(c => c.Target).ToList(), ct)
            .ToDictionary(e => e.Path!, StringComparer.OrdinalIgnoreCase);
        var result = new List<FolderEntry>();
        foreach (var (target, link) in candidates)
        {
            if (!stats.TryGetValue(target.TrimEnd('\\'), out var entry) || entry.IsFolder) continue;
            entry.RecentLink = link;
            result.Add(entry);
            if (result.Count >= limit) break;
        }
        return result;
    }

    /// <summary>The file-system path a shortcut points to, without resolving (no searching for moved targets).</summary>
    internal static string? ReadLinkTarget(string lnkPath)
    {
        object? link = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ShellLinkClsid.CLSID_ShellLink, throwOnError: true)!;
            link = Activator.CreateInstance(type)!;
            ((ComTypes.IPersistFile)link).Load(lnkPath, 0 /* STGM_READ */);
            if (((IShellLinkW)link).GetIDList(out var pidl) != 0 || pidl == IntPtr.Zero) return null;
            try { return ShellNative.GetName(pidl, SIGDN.FILESYSPATH); }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidCastException)
        {
            return null;
        }
        finally { ShellNative.Release(link); }
    }
}
