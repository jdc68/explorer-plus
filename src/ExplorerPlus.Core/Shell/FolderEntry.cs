using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public sealed class FolderEntry
{
    /// <summary>Name within the parent (file name for file-system items).</summary>
    public required string Name { get; init; }

    /// <summary>Name as the shell displays it (e.g. "Local Disk (C:)").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Full file-system path, null for virtual items.</summary>
    public string? Path { get; init; }

    /// <summary>Absolute PIDL; always set for virtual items, created lazily for file-system items.</summary>
    public Pidl? Pidl { get; set; }

    public uint FileAttributes { get; init; }
    public SFGAO ShellAttributes { get; init; }
    public bool IsFolder { get; init; }

    /// <summary>True for folder-like items that are files (zip, cab) — browsable, but listed as files.</summary>
    public bool IsBrowsableFile { get; init; }

    /// <summary>Size in bytes, -1 when not applicable or unknown.</summary>
    public long Size { get; init; } = -1;

    // a folder's total when it came with the listing (instant search), so nothing needs measuring
    public long? KnownFolderSize { get; init; }

    public DateTime? Modified { get; init; }
    public DateTime? Created { get; init; }
    public required string TypeName { get; init; }

    /// <summary>Index into the system image list, -1 if unknown.</summary>
    public int IconIndex { get; init; } = -1;

    /// <summary>The extension-based icon may be wrong (exe, lnk, ico, customised folders): fetch the real one.</summary>
    public bool NeedsRealIcon { get; init; }

    /// <summary>Home only: the section the item is listed in ("Quick access", "Starred", "Recent files").</summary>
    public string? Section { get; set; }

    /// <summary>Home only: order of the section on the page.</summary>
    public int SectionRank { get; set; }

    /// <summary>Home only: position inside the section (pin order, most recent first).</summary>
    public int SectionOrder { get; set; }

    /// <summary>Home only: the Recent folder shortcut this item was found through.</summary>
    public string? RecentLink { get; set; }

    public bool IsHidden => (FileAttributes & FileAttr.HIDDEN) != 0 || ShellAttributes.HasFlag(SFGAO.HIDDEN);
    public bool IsProtectedSystem => (FileAttributes & (FileAttr.HIDDEN | FileAttr.SYSTEM)) == (FileAttr.HIDDEN | FileAttr.SYSTEM);

    public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(Name);

    /// <summary>Returns the PIDL, creating it from the path if needed. Must run on an STA thread for virtual paths.</summary>
    public Pidl GetPidl()
    {
        if (Pidl is not null) return Pidl;
        if (Path is null) throw new InvalidOperationException("Item has neither PIDL nor path.");
        Pidl = ShellNative.Parse(Path);
        return Pidl;
    }

    public Task<Pidl> GetPidlAsync() =>
        Pidl is not null ? Task.FromResult(Pidl) : StaWorkerPool.General.Run(GetPidl);
}
