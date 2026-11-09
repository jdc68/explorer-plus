using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public enum FileOpKind { Rename, Move, Copy, Delete, New }

/// <param name="Source">Path of the item before the operation (null for new items).</param>
/// <param name="Result">Path of the item afterwards (null when deleted).</param>
/// <param name="RecycledItem">For a delete that went to the Recycle Bin: the item inside the bin.</param>
public sealed record FileOpEntry(FileOpKind Kind, string? Source, string? Result, Pidl? RecycledItem = null);

/// <summary>Collects what an IFileOperation really did (final names after collisions, Recycle Bin items) so it can be undone.</summary>
[ComVisible(true)]
public sealed class FileOpLog : IFileOperationProgressSink
{
    private readonly List<FileOpEntry> _entries = [];

    /// <summary>Raised on the operation's thread with the share of work done (0…1), for taskbar progress.</summary>
    public event Action<double>? Progress;

    public IReadOnlyList<FileOpEntry> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    // Success codes that still mean "this item is now where the log says, and wasn't there before".
    private const int DontProcessChildren = 0x00270008; // a rename or whole-folder move/copy
    private const int KeepBoth = 0x0027000C;
    private const int CollisionResolved = 0x0027000E;

    private void Add(int hr, FileOpEntry entry)
    {
        // Other COPYENGINE_S_* codes mean skipped, merged into an existing folder or overwritten: undoing those
        // would delete something that was there before, so they are not recorded.
        if (hr is not (0 or DontProcessChildren or KeepBoth or CollisionResolved)) return;
        lock (_entries) _entries.Add(entry);
    }

    /// <summary>Entries not nested inside another entry's item (a copied folder also reports each file in it).</summary>
    public IReadOnlyList<FileOpEntry> TopLevel(FileOpKind kind)
    {
        var all = Entries.Where(e => e.Kind == kind).ToList();
        var sources = all.Select(e => e.Source).OfType<string>().Select(Trim).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = all.Select(e => e.Result).OfType<string>().Select(Trim).ToHashSet(StringComparer.OrdinalIgnoreCase);
        static string Trim(string p) => p.TrimEnd('\\');
        static bool UnderAny(string? path, HashSet<string> roots)
        {
            for (var dir = path is null ? null : Path.GetDirectoryName(Trim(path)); dir is not null; dir = Path.GetDirectoryName(dir))
                if (roots.Contains(Trim(dir))) return true;
            return false;
        }
        return all.Where(e => !UnderAny(e.Source, sources) && !UnderAny(e.Result, results)).ToList();
    }

    private static string? PathOf(IShellItem? item) => item is null ? null : ShellNative.GetName(item, SIGDN.FILESYSPATH);

    private static Pidl? PidlOf(IShellItem? item)
    {
        if (item is null || Shell32.SHGetIDListFromObject(item, out var pidl) != 0 || pidl == IntPtr.Zero) return null;
        return Pidl.TakeNative(pidl);
    }

    public int StartOperations() => 0;
    public int FinishOperations(int hrResult) => 0;
    public int PreRenameItem(uint dwFlags, IShellItem? psiItem, string? pszNewName) => 0;

    public int PostRenameItem(uint dwFlags, IShellItem? psiItem, string? pszNewName, int hrRename, IShellItem? psiNewlyCreated)
    {
        Add(hrRename, new FileOpEntry(FileOpKind.Rename, PathOf(psiItem), PathOf(psiNewlyCreated)));
        return 0;
    }

    public int PreMoveItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName) => 0;

    public int PostMoveItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated)
    {
        Add(hrMove, new FileOpEntry(FileOpKind.Move, PathOf(psiItem), PathOf(psiNewlyCreated)));
        return 0;
    }

    public int PreCopyItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName) => 0;

    public int PostCopyItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated)
    {
        Add(hrCopy, new FileOpEntry(FileOpKind.Copy, PathOf(psiItem), PathOf(psiNewlyCreated)));
        return 0;
    }

    public int PreDeleteItem(uint dwFlags, IShellItem? psiItem) => 0;

    public int PostDeleteItem(uint dwFlags, IShellItem? psiItem, int hrDelete, IShellItem? psiNewlyCreated)
    {
        Add(hrDelete, new FileOpEntry(FileOpKind.Delete, PathOf(psiItem), null, PidlOf(psiNewlyCreated)));
        return 0;
    }

    public int PreNewItem(uint dwFlags, IShellItem? psiDestinationFolder, string? pszNewName) => 0;

    public int PostNewItem(uint dwFlags, IShellItem? psiDestinationFolder, string? pszNewName, string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem)
    {
        Add(hrNew, new FileOpEntry(FileOpKind.New, null, PathOf(psiNewItem)));
        return 0;
    }

    public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
    {
        if (iWorkTotal > 0) Progress?.Invoke(Math.Clamp((double)iWorkSoFar / iWorkTotal, 0, 1));
        return 0;
    }
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}
