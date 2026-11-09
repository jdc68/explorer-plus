using System.ComponentModel;
using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public static class FastFolderEnumerator
{
    public static IEnumerable<FolderEntry> Enumerate(string directory, CancellationToken ct = default)
    {
        var dir = directory.TrimEnd('\\');
        var pattern = ToLongPath(dir) + @"\*";

        var handle = Kernel32.FindFirstFileExW(pattern, Kernel32.FindExInfoBasic, out var data,
            Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FIND_FIRST_EX_LARGE_FETCH);
        if (handle == Kernel32.INVALID_HANDLE_VALUE)
        {
            var err = Marshal.GetLastWin32Error();
            if (err is 2 or 18) yield break; // file not found / no more files: empty folder
            throw ToException(err, directory);
        }

        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var entry = ToEntry(dir, ref data);
                if (entry is not null) yield return entry;
            }
            while (Kernel32.FindNextFileW(handle, out data));
        }
        finally
        {
            Kernel32.FindClose(handle);
        }
    }

    /// <summary>Reads a single item (used for incremental change notifications).</summary>
    public static FolderEntry? Stat(string path)
    {
        var parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\'));
        if (parent is null) return null;
        var handle = Kernel32.FindFirstFileExW(ToLongPath(path.TrimEnd('\\')), Kernel32.FindExInfoBasic, out var data,
            Kernel32.FindExSearchNameMatch, IntPtr.Zero, 0);
        if (handle == Kernel32.INVALID_HANDLE_VALUE) return null;
        try { return ToEntry(parent.TrimEnd('\\'), ref data); }
        finally { Kernel32.FindClose(handle); }
    }

    private static unsafe FolderEntry? ToEntry(string dir, ref WIN32_FIND_DATAW data)
    {
        string name;
        fixed (char* p = data.cFileName) name = new string(p);
        if (name is "." or "..") return null;

        var attrs = data.dwFileAttributes;
        var isDir = (attrs & FileAttr.DIRECTORY) != 0;
        var ext = isDir ? "" : System.IO.Path.GetExtension(name);
        var (typeName, icon) = ShellInfoCache.Get(ext, isDir);

        // Customised folders (desktop.ini) carry ReadOnly or System; junctions show a different icon.
        var needsRealIcon = isDir
            ? (attrs & (FileAttr.READONLY | FileAttr.SYSTEM)) != 0
            : ShellInfoCache.HasPerFileIcon(ext);

        return new FolderEntry
        {
            Name = name,
            DisplayName = name,
            Path = dir + "\\" + name,
            FileAttributes = attrs,
            IsFolder = isDir,
            Size = isDir ? -1 : ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
            Modified = FromFileTime(data.ftLastWriteTime),
            Created = FromFileTime(data.ftCreationTime),
            TypeName = typeName,
            IconIndex = icon,
            NeedsRealIcon = needsRealIcon,
            IsBrowsableFile = !isDir && IsArchiveFolder(ext),
        };
    }

    // Windows' built-in compressed folder handler makes these browsable like folders.
    private static bool IsArchiveFolder(string ext) => ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);

    private static DateTime? FromFileTime(FILETIME32 ft)
    {
        var v = ft.ToLong();
        if (v <= 0) return null;
        try { return DateTime.FromFileTime(v); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static string ToLongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    private static Exception ToException(int error, string path) => error switch
    {
        5 => new UnauthorizedAccessException($"You don't have permission to open \"{path}\"."),
        3 => new DirectoryNotFoundException($"\"{path}\" no longer exists."),
        21 => new IOException($"The device \"{path}\" is not ready."),
        _ => new IOException($"Can't open \"{path}\": {new Win32Exception(error).Message}"),
    };
}
