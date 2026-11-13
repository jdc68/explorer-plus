using System.Runtime.InteropServices;

namespace ExplorerPlus.Core.Shell;

public static class RecycleBin
{
    /// <summary>Items and bytes in the Recycle Bins of all drives.</summary>
    public static (long Items, long Bytes) Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : (0, 0);
    }

    // A delete reports what it recycled as the $R… file on disk, but Restore is only on the Recycle Bin's own item
    // for it, so look that up. Must run on an STA thread
    public static List<Pidl> BinItemsFor(IReadOnlyList<Pidl> recycled)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < recycled.Count; i++)
        {
            using var n = recycled[i].ToNative();
            if (ShellNative.GetName(n.Ptr, Interop.SIGDN.DESKTOPABSOLUTEPARSING) is { } name) byName.TryAdd(name, i);
        }
        var found = new Pidl?[recycled.Count];
        if (byName.Count > 0 && ShellNative.TryParse(ShellLocation.Known.RecycleBin) is { } bin)
        {
            var folder = ShellNative.BindToFolder(bin);
            Interop.IEnumIDList? items = null;
            try
            {
                using var binNative = bin.ToNative();
                if (folder.EnumObjects(IntPtr.Zero, Interop.SHCONTF.FOLDERS | Interop.SHCONTF.NONFOLDERS | Interop.SHCONTF.INCLUDEHIDDEN, out items) >= 0 && items is not null)
                    while (byName.Count > 0 && items.Next(1, out var child, out var fetched) == 0 && fetched == 1)
                    {
                        var abs = Interop.Shell32.ILCombine(binNative.Ptr, child);
                        Marshal.FreeCoTaskMem(child);
                        try
                        {
                            if (ShellNative.GetName(abs, Interop.SIGDN.DESKTOPABSOLUTEPARSING) is { } name && byName.Remove(name, out var index))
                                found[index] = Pidl.FromNative(abs);
                        }
                        finally { Interop.Shell32.ILFree(abs); }
                    }
            }
            finally
            {
                ShellNative.Release(items);
                ShellNative.Release(folder);
            }
        }
        return recycled.Select((p, i) => found[i] ?? p).ToList();
    }

    /// <summary>Empties every drive's Recycle Bin with the shell's own confirmation, progress and sound.</summary>
    public static bool Empty(IntPtr owner) => SHEmptyRecycleBinW(owner, null, 0) == 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}

public static class NetworkDrives
{
    private const uint RESOURCETYPE_DISK = 1;

    /// <summary>Windows' "Map Network Drive" dialog. Modal; call on the UI thread.</summary>
    public static void ShowMapDialog(IntPtr owner) => WNetConnectionDialog(owner, RESOURCETYPE_DISK);

    /// <summary>Windows' "Disconnect Network Drives" dialog. Modal; call on the UI thread.</summary>
    public static void ShowDisconnectDialog(IntPtr owner) => WNetDisconnectDialog(owner, RESOURCETYPE_DISK);

    [DllImport("mpr.dll")]
    private static extern int WNetConnectionDialog(IntPtr hwnd, uint dwType);

    [DllImport("mpr.dll")]
    private static extern int WNetDisconnectDialog(IntPtr hwnd, uint dwType);
}
