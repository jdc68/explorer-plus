using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public static class ShellInfoCache
{
    private static readonly ConcurrentDictionary<string, (string TypeName, int IconIndex)> ByExtension = new(StringComparer.OrdinalIgnoreCase);

    // Extensions whose icon lives inside the file (or depends on the target), so the extension icon is a placeholder.
    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".ico", ".lnk", ".url", ".cur", ".ani", ".scr", ".cpl", ".msc", ".appref-ms", ".website",
    };

    public static bool HasPerFileIcon(string extension) => PerFileIconExtensions.Contains(extension);

    public static (string TypeName, int IconIndex) Get(string extension, bool isFolder)
    {
        var key = isFolder ? "<folder>" : (string.IsNullOrEmpty(extension) ? "<noext>" : extension);
        return ByExtension.GetOrAdd(key, static (k, folder) => Lookup(k, folder), isFolder);
    }

    private static (string, int) Lookup(string key, bool isFolder)
    {
        var info = new SHFILEINFO();
        var name = isFolder ? "folder" : key.StartsWith('<') ? "file" : "file" + key;
        var attrs = isFolder ? FileAttr.DIRECTORY : FileAttr.NORMAL;
        Shell32.SHGetFileInfo(name, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI.USEFILEATTRIBUTES | SHGFI.TYPENAME | SHGFI.SYSICONINDEX | SHGFI.SMALLICON);
        var type = string.IsNullOrEmpty(info.szTypeName)
            ? (key.StartsWith('<') ? "File" : key.TrimStart('.').ToUpperInvariant() + " File")
            : info.szTypeName;
        return (type, info.iIcon);
    }

    /// <summary>Real icon index for a specific file path (reads the file's icon resource). Call off the UI thread.</summary>
    public static int GetIconIndexForPath(string path)
    {
        var info = new SHFILEINFO();
        var r = Shell32.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI.SYSICONINDEX | SHGFI.SMALLICON);
        return r == IntPtr.Zero ? -1 : info.iIcon;
    }

    /// <summary>Icon index and type name for a shell item by PIDL. Call off the UI thread.</summary>
    public static (int IconIndex, string? TypeName) GetForPidl(IntPtr pidl)
    {
        var info = new SHFILEINFO();
        var r = Shell32.SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI.PIDL | SHGFI.SYSICONINDEX | SHGFI.SMALLICON | SHGFI.TYPENAME);
        return r == IntPtr.Zero ? (-1, null) : (info.iIcon, info.szTypeName);
    }
}
