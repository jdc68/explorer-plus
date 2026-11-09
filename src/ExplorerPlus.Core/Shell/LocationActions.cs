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
