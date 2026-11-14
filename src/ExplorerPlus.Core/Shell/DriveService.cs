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

/// <summary>What This PC shows about a drive besides its space: file system, kind of disk, model, Recycle Bin size.</summary>
public sealed record DriveFacts(string? FileSystem, string? Media, string? Model, long RecycleBinBytes, long RecycleBinItems)
{
    public string Summary => string.Join(" · ", new[] { Media, FileSystem }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed record DriveReading((ulong Total, ulong Free)? Space, DriveFacts? Facts);

public static class DriveService
{
    private static readonly TimeSpan PerDriveTimeout = TimeSpan.FromMilliseconds(1500);

    public static bool IsDriveRoot(string? path) =>
        path is { Length: 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    public static DriveKind KindOf(string root) => ToKind(Kernel32.GetDriveTypeW(root));

    /// <summary>Total and free bytes, or null when the drive has no media or doesn't answer. Blocks on slow drives.</summary>
    public static (ulong Total, ulong Free)? QuerySpace(string root, DriveKind kind)
    {
        if (kind == DriveKind.Optical && !IsReady(root)) return null;
        return Kernel32.GetDiskFreeSpaceExW(root, out _, out var total, out var free) && total > 0 ? (total, free) : null;
    }

    public static string? VolumeIdOf(string root, DriveKind kind)
    {
        if (kind == DriveKind.Network) return null;
        var buf = new char[64];
        return Kernel32.GetVolumeNameForVolumeMountPointW(root, buf, (uint)buf.Length) ? new string(buf).TrimEnd('\0') : null;
    }

    private static readonly Dictionary<string, Task<DriveReading>> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a drive's space (and facts) off the calling thread; null when it doesn't answer in time. A drive that
    /// hangs (a lost network share) keeps one query running, which later calls wait on instead of starting more.
    /// </summary>
    public static async Task<DriveReading?> ReadAsync(string root, bool withFacts, int timeoutMs = 2000)
    {
        Task<DriveReading> task;
        lock (Pending)
        {
            if (!Pending.TryGetValue(root, out task!) || task.IsCompleted)
            {
                task = Task.Run(() =>
                {
                    try
                    {
                        var kind = KindOf(root);
                        return new DriveReading(QuerySpace(root, kind), withFacts ? QueryFacts(root, kind) : null);
                    }
                    catch { return new DriveReading(null, null); }
                });
                Pending[root] = task;
            }
        }
        var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        return done == task ? await task.ConfigureAwait(false) : null;
    }

    public static DriveFacts QueryFacts(string root, DriveKind kind)
    {
        string? fs = null;
        var fsName = new char[32];
        if ((kind != DriveKind.Optical || IsReady(root)) && VolumeNative.GetVolumeInformationW(root, null, 0, out _, out _, out _, fsName, fsName.Length))
            fs = new string(fsName).TrimEnd('\0');

        var (media, model) = kind switch
        {
            DriveKind.Network => ("Network drive", null),
            DriveKind.Optical => ("CD/DVD drive", null),
            DriveKind.RamDisk => ("RAM disk", null),
            _ => QueryDisk(root, kind),
        };

        long binBytes = 0, binItems = 0;
        if (kind is DriveKind.Fixed or DriveKind.Removable)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (Shell32.SHQueryRecycleBinW(root, ref info) >= 0) (binBytes, binItems) = (info.i64Size, info.i64NumItems);
        }
        return new DriveFacts(fs, media, model, binBytes, binItems);
    }

    private enum StorageBus { Scsi = 1, Atapi = 2, Ata = 3, Usb = 7, Raid = 8, Sas = 0xA, Sata = 0xB, Sd = 0xC, Mmc = 0xD, Virtual = 0xE, FileBackedVirtual = 0xF, Spaces = 0x10, Nvme = 0x11 }

    // what kind of disk holds the volume: the bus from the device descriptor, SSD or not from the seek penalty
    private static unsafe (string? Media, string? Model) QueryDisk(string root, DriveKind kind)
    {
        using var volume = VolumeNative.CreateFileW(@"\\.\" + root.TrimEnd('\\'), 0, VolumeNative.FILE_SHARE_READ | VolumeNative.FILE_SHARE_WRITE,
            IntPtr.Zero, VolumeNative.OPEN_EXISTING, 0, IntPtr.Zero);
        if (volume.IsInvalid) return (null, null);

        var query = stackalloc int[3];
        var buffer = stackalloc byte[1024];
        query[0] = 0; // StorageDeviceProperty
        if (!VolumeNative.DeviceIoControl(volume, VolumeNative.IOCTL_STORAGE_QUERY_PROPERTY, query, 12, buffer, 1024, out var got, IntPtr.Zero) || got < 32)
            return (null, null);
        var bus = (StorageBus)(*(int*)(buffer + 28));
        var model = string.Join(" ", new[] { AnsiAt(buffer, got, 12), AnsiAt(buffer, got, 16) }.Where(s => s.Length > 0));

        bool? seeks = null;
        query[0] = 7; // StorageDeviceSeekPenaltyProperty
        var penalty = stackalloc byte[12];
        if (VolumeNative.DeviceIoControl(volume, VolumeNative.IOCTL_STORAGE_QUERY_PROPERTY, query, 12, penalty, 12, out var got2, IntPtr.Zero) && got2 >= 9)
            seeks = penalty[8] != 0;

        return (MediaLabel(bus, seeks, kind), model.Length > 0 ? model : null);
    }

    private static string? MediaLabel(StorageBus bus, bool? seeks, DriveKind kind)
    {
        var disk = seeks switch { true => "hard disk", false => "SSD", _ => null };
        return bus switch
        {
            StorageBus.Nvme => "NVMe SSD",
            StorageBus.Sata or StorageBus.Ata => disk is null ? "SATA disk" : $"SATA {disk}",
            StorageBus.Usb when kind == DriveKind.Removable || disk is null => "USB drive",
            StorageBus.Usb => $"USB {disk}",
            StorageBus.Sd or StorageBus.Mmc => "Memory card",
            StorageBus.Virtual or StorageBus.FileBackedVirtual => "Virtual disk",
            StorageBus.Spaces => "Storage Spaces",
            StorageBus.Scsi or StorageBus.Sas or StorageBus.Raid or StorageBus.Atapi => disk is null ? null : char.ToUpperInvariant(disk[0]) + disk[1..],
            _ => null,
        };
    }

    private static unsafe string AnsiAt(byte* buffer, int length, int offsetField)
    {
        var offset = *(int*)(buffer + offsetField);
        if (offset <= 0 || offset >= length) return "";
        var end = offset;
        while (end < length && buffer[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buffer + offset, end - offset).Trim();
    }

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
