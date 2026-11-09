using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public static class ShellFolderEnumerator
{
    private const SFGAO WantedAttributes =
        SFGAO.FOLDER | SFGAO.FILESYSTEM | SFGAO.STREAM | SFGAO.HIDDEN | SFGAO.LINK | SFGAO.CANRENAME |
        SFGAO.CANDELETE | SFGAO.CANCOPY | SFGAO.CANMOVE | SFGAO.HASPROPSHEET | SFGAO.BROWSABLE | SFGAO.REMOVABLE;

    public static IEnumerable<FolderEntry> Enumerate(Pidl folderPidl, CancellationToken ct = default)
    {
        var filter = ShellFilter.Default;
        var folder = ShellNative.BindToFolder(folderPidl);
        IEnumIDList? enumerator = null;
        try
        {
            var flags = SHCONTF.FOLDERS | SHCONTF.NONFOLDERS | SHCONTF.INCLUDEHIDDEN | SHCONTF.INCLUDESUPERHIDDEN;
            var hr = folder.EnumObjects(IntPtr.Zero, flags, out enumerator);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (enumerator is null) yield break;

            using var parentNative = folderPidl.ToNative();
            while (!ct.IsCancellationRequested && enumerator.Next(1, out var child, out var fetched) == 0 && fetched == 1)
            {
                FolderEntry? entry;
                try
                {
                    var abs = Shell32.ILCombine(parentNative.Ptr, child);
                    try { entry = CreateEntry(abs, filter); }
                    finally { Shell32.ILFree(abs); }
                }
                finally { Marshal.FreeCoTaskMem(child); }

                if (entry is not null) yield return entry;
            }
        }
        finally
        {
            ShellNative.Release(enumerator);
            ShellNative.Release(folder);
        }
    }

    private static FolderEntry? CreateEntry(IntPtr absolutePidl, ShellFilter filter)
    {
        var item = ShellNative.TryCreateItem2(absolutePidl);
        if (item is null) return null;
        try
        {
            item.GetAttributes(WantedAttributes, out var attrs);
            var parsing = ShellNative.GetName(item, SIGDN.DESKTOPABSOLUTEPARSING);
            var fsPath = attrs.HasFlag(SFGAO.FILESYSTEM) ? ShellNative.GetName(item, SIGDN.FILESYSPATH) : null;
            if (filter.IsBlocked(parsing, fsPath)) return null;

            var display = ShellNative.GetName(item, SIGDN.PARENTRELATIVE)
                          ?? ShellNative.GetName(item, SIGDN.NORMALDISPLAY) ?? parsing ?? "?";
            var name = ShellNative.GetName(item, SIGDN.PARENTRELATIVEPARSING) ?? display;

            var sizeKey = PropertyKeys.Size;
            long size = item.GetUInt64(ref sizeKey, out var sz) == 0 ? (long)sz : -1;
            var dateKey = PropertyKeys.DateModified;
            DateTime? modified = item.GetFileTime(ref dateKey, out var ft) == 0 && ft > 0 ? DateTime.FromFileTime(ft) : null;

            var (icon, shellType) = ShellInfoCache.GetForPidl(absolutePidl);
            var typeKey = PropertyKeys.ItemTypeText;
            var typeName = ShellNative.GetString(item, typeKey) ?? shellType ?? "";

            var isStream = attrs.HasFlag(SFGAO.STREAM);
            var isFolder = attrs.HasFlag(SFGAO.FOLDER) && !isStream;

            uint fileAttrs = 0;
            if (attrs.HasFlag(SFGAO.HIDDEN)) fileAttrs |= FileAttr.HIDDEN;
            if (isFolder) fileAttrs |= FileAttr.DIRECTORY;

            return new FolderEntry
            {
                Name = name,
                DisplayName = display,
                Path = fsPath,
                Pidl = Pidl.FromNative(absolutePidl),
                ShellAttributes = attrs,
                FileAttributes = fileAttrs,
                IsFolder = isFolder,
                IsBrowsableFile = isStream && attrs.HasFlag(SFGAO.FOLDER),
                Size = size,
                Modified = modified,
                TypeName = typeName,
                IconIndex = icon,
            };
        }
        finally { ShellNative.Release(item); }
    }
}
