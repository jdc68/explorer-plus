using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public enum FileOpKind { Rename, Move, Copy, Delete, New }

/// <param name="Source">Path of the item before the operation (null for new items).</param>
/// <param name="Result">Path of the item afterwards (null when deleted).</param>
/// <param name="RecycledItem">For a delete that went to the Recycle Bin: the item inside the bin.</param>
public sealed record FileOpEntry(FileOpKind Kind, string? Source, string? Result, Pidl? RecycledItem = null);

// Destination is where a copy, move or rename was going, which may be what was held open (a replace)
public sealed record FileOpFailure(FileOpKind Kind, string? Source, string? Destination, int HResult);

/// <summary>Collects what an IFileOperation really did (final names after collisions, Recycle Bin items) so it can be undone.</summary>
[ComVisible(true)]
public sealed class FileOpLog : IFileOperationProgressSink
{
    private readonly List<FileOpEntry> _entries = [];
    private readonly List<FileOpFailure> _failures = [];

    /// <summary>Raised on the operation's thread with the share of work done (0…1), for taskbar progress.</summary>
    public event Action<double>? Progress;

    public IReadOnlyList<FileOpEntry> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    // errors, and items cancelled or skipped in the shell's in-use dialog (those report COPYENGINE_S_PENDING first)
    public IReadOnlyList<FileOpFailure> Failures
    {
        get { lock (_failures) return _failures.ToList(); }
    }

    // Success codes that still mean "this item is now where the log says, and wasn't there before".
    private const int DontProcessChildren = 0x00270008; // a rename or whole-folder move/copy
    private const int KeepBoth = 0x0027000C;
    private const int CollisionResolved = 0x0027000E;
    private const int UserIgnored = 0x00270005;

    private void Fail(int hr, FileOpKind kind, string? source, string? destination)
    {
        if (hr >= 0 && hr != UserIgnored) return;
        lock (_failures) _failures.Add(new FileOpFailure(kind, source, destination, hr));
    }

    private static string? Target(IShellItem? folder, string? name, string? source)
    {
        var dir = PathOf(folder);
        name ??= source is null ? null : Path.GetFileName(source.TrimEnd('\\'));
        return dir is null || string.IsNullOrEmpty(name) ? null : Path.Combine(dir, name);
    }

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
        var source = PathOf(psiItem);
        Add(hrRename, new FileOpEntry(FileOpKind.Rename, source, PathOf(psiNewlyCreated)));
        Fail(hrRename, FileOpKind.Rename, source,
            source is not null && pszNewName is not null && Path.GetDirectoryName(source.TrimEnd('\\')) is { } dir ? Path.Combine(dir, pszNewName) : null);
        return 0;
    }

    public int PreMoveItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName) => 0;

    public int PostMoveItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated)
    {
        var source = PathOf(psiItem);
        Add(hrMove, new FileOpEntry(FileOpKind.Move, source, PathOf(psiNewlyCreated)));
        Fail(hrMove, FileOpKind.Move, source, Target(psiDestinationFolder, pszNewName, source));
        return 0;
    }

    public int PreCopyItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName) => 0;

    public int PostCopyItem(uint dwFlags, IShellItem? psiItem, IShellItem? psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated)
    {
        var source = PathOf(psiItem);
        Add(hrCopy, new FileOpEntry(FileOpKind.Copy, source, PathOf(psiNewlyCreated)));
        Fail(hrCopy, FileOpKind.Copy, source, Target(psiDestinationFolder, pszNewName, source));
        return 0;
    }

    public int PreDeleteItem(uint dwFlags, IShellItem? psiItem) => 0;

    public int PostDeleteItem(uint dwFlags, IShellItem? psiItem, int hrDelete, IShellItem? psiNewlyCreated)
    {
        var source = PathOf(psiItem);
        Add(hrDelete, new FileOpEntry(FileOpKind.Delete, source, null, PidlOf(psiNewlyCreated)));
        Fail(hrDelete, FileOpKind.Delete, source, null);
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
