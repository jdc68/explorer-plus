using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public enum DriveKind { Unknown, Removable, Fixed, Network, Optical, RamDisk }

public sealed record DriveEntry(
    string Root,
    string DisplayName,
    DriveKind Kind,
    ulong TotalBytes,
    ulong FreeBytes,
    int IconIndex,
    string? VolumeId)
{
    public bool HasSpaceInfo => TotalBytes > 0;
    public double UsedFraction => TotalBytes == 0 ? 0 : 1.0 - (double)FreeBytes / TotalBytes;
}

public static class DriveService
{
    private static readonly TimeSpan PerDriveTimeout = TimeSpan.FromMilliseconds(1500);

    public static async Task<IReadOnlyList<DriveEntry>> GetDrivesAsync()
    {
        var roots = Environment.GetLogicalDrives();
        var tasks = roots.Select(root => QueryWithTimeout(root)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var seenVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DriveEntry>();
        foreach (var d in results)
        {
            if (d is null) continue;
            if (d.VolumeId is not null && !seenVolumes.Add(d.VolumeId)) continue;
            list.Add(d);
        }
        return list;
    }

    private static async Task<DriveEntry?> QueryWithTimeout(string root)
    {
        var kind = ToKind(Kernel32.GetDriveTypeW(root));
        if (kind == DriveKind.Unknown) return null;
        var query = Task.Run(() => Query(root, kind));
        var done = await Task.WhenAny(query, Task.Delay(PerDriveTimeout)).ConfigureAwait(false);
        if (done == query) return await query.ConfigureAwait(false);
        // Not responding: still show it (Explorer does), but without space info.
        return new DriveEntry(root, $"{DefaultLabel(kind)} ({root.TrimEnd('\\')})", kind, 0, 0, ShellInfoCache.Get("", true).IconIndex, null);
    }

    private static DriveEntry Query(string root, DriveKind kind)
    {
        ulong total = 0, free = 0;
        if (kind != DriveKind.Optical || IsReady(root))
            Kernel32.GetDiskFreeSpaceExW(root, out _, out total, out free);

        var info = new SHFILEINFO();
        Shell32.SHGetFileInfo(root, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI.DISPLAYNAME | SHGFI.SYSICONINDEX | SHGFI.SMALLICON);
        var name = string.IsNullOrWhiteSpace(info.szDisplayName) ? $"{DefaultLabel(kind)} ({root.TrimEnd('\\')})" : info.szDisplayName;

        string? volumeId = null;
        var buf = new char[64];
        if (kind != DriveKind.Network && Kernel32.GetVolumeNameForVolumeMountPointW(root, buf, (uint)buf.Length))
            volumeId = new string(buf).TrimEnd('\0');

        return new DriveEntry(root, name, kind, total, free, info.iIcon, volumeId);
    }

    private static bool IsReady(string root)
    {
        try { return new DriveInfo(root).IsReady; } catch { return false; }
    }

    private static DriveKind ToKind(uint t) => t switch
    {
        2 => DriveKind.Removable,
        3 => DriveKind.Fixed,
        4 => DriveKind.Network,
        5 => DriveKind.Optical,
        6 => DriveKind.RamDisk,
        _ => DriveKind.Unknown,
    };

    private static string DefaultLabel(DriveKind k) => k switch
    {
        DriveKind.Network => "Network Drive",
        DriveKind.Optical => "DVD Drive",
        DriveKind.Removable => "USB Drive",
        _ => "Local Disk",
    };
}
