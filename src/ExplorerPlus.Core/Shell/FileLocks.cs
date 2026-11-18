using System.Runtime.InteropServices;
using System.Text;
using ExplorerPlus.Interop;
using Microsoft.Win32.SafeHandles;

namespace ExplorerPlus.Core.Shell;

public enum HolderKind { App, Console, Service, Explorer, System, Self }

public sealed record FileHolder(int ProcessId, long StartTime, string Name, string? ExePath, HolderKind Kind, bool CanClose, string? Note);

public static class FileLocks
{
    // how much of a folder is looked at
    internal const int MaxFiles = 5000;
    internal const int MaxFolders = 64;
    private const int MaxPaths = 32;

    public static IReadOnlyList<string> PathsToCheck(IEnumerable<FileOpFailure> failures) =>
        failures.SelectMany(f => new[] { f.Source, f.Destination }).OfType<string>()
            .Select(p => p.TrimEnd('\\')).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPaths).ToList();

    public static bool IsSameOrUnder(string path, string root)
    {
        path = path.TrimEnd('\\');
        root = root.TrimEnd('\\');
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
               || path.Length > root.Length && path[root.Length] == '\\' && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    // touches the disk, so never on the UI thread; local drives only
    public static IReadOnlyList<FileHolder> FindHolders(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var files = new List<string>();
        var folders = new List<string>();
        foreach (var path in paths.Where(IsLocal).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) Walk(path, files, folders, ct);
            else if (File.Exists(path)) files.Add(path);
        }

        // Only what can't be deleted right now counts, so an app that merely watches a folder or keeps a file open
        // for others to delete isn't blamed. A running program's file passes that test and still can't be deleted;
        // when nothing is blocked, every file is asked about.
        var blockedFiles = files.Where(IsBlocked).ToArray();
        var blockedFolders = folders.Where(IsBlocked).ToList();
        if (blockedFiles.Length == 0 && blockedFolders.Count == 0) blockedFiles = files.ToArray();

        var infos = new Dictionary<int, RM_PROCESS_INFO>();
        if (blockedFiles.Length > 0)
            foreach (var info in RmList(blockedFiles, null)) infos.TryAdd(info.Process.dwProcessId, info);

        // Restart Manager doesn't take folders; a folder held open (a terminal's current directory) only shows this way
        var folderPids = new HashSet<int>();
        Parallel.ForEach(blockedFolders, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, folder =>
        {
            foreach (var pid in ProcessesUsing(folder))
                lock (folderPids) folderPids.Add(pid);
        });
        var apps = folderPids.Where(p => !infos.ContainsKey(p))
            .Select(p => new RM_UNIQUE_PROCESS { dwProcessId = p, ProcessStartTime = StartTimeOf(p) })
            .Where(a => a.ProcessStartTime != 0 || a.dwProcessId == SystemPid).ToArray();
        if (apps.Length > 0)
        {
            foreach (var info in RmList(null, apps.Where(a => a.ProcessStartTime != 0).ToArray())) infos.TryAdd(info.Process.dwProcessId, info);
            // Restart Manager won't describe every process (the System process); those get their name from the exe
            foreach (var app in apps.Where(a => !infos.ContainsKey(a.dwProcessId)))
                infos[app.dwProcessId] = new RM_PROCESS_INFO
                {
                    Process = app, strAppName = "", strServiceShortName = "",
                    TSSessionId = ProcessNative.ProcessIdToSessionId(app.dwProcessId, out var s) ? (uint)s : uint.MaxValue,
                };
        }

        ProcessNative.ProcessIdToSessionId(Environment.ProcessId, out var session);
        return infos.Values.Select(i => ToHolder(i, (uint)session))
            .OrderBy(h => h.Kind == HolderKind.Self).ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private const int SystemPid = 4;

    // opening for delete fails with a sharing violation while someone holds it without FILE_SHARE_DELETE
    internal static bool IsBlocked(string path)
    {
        const uint Delete = 0x00010000, OpenReparsePoint = 0x00200000;
        const int SharingViolation = 32;
        using var handle = VolumeNative.CreateFileW(path, Delete,
            VolumeNative.FILE_SHARE_READ | VolumeNative.FILE_SHARE_WRITE | VolumeNative.FILE_SHARE_DELETE, IntPtr.Zero,
            VolumeNative.OPEN_EXISTING, VolumeNative.FILE_FLAG_BACKUP_SEMANTICS | OpenReparsePoint, IntPtr.Zero);
        return handle.IsInvalid && Marshal.GetLastWin32Error() == SharingViolation;
    }

    public static bool IsRunning(FileHolder holder)
    {
        using var process = OpenSame(holder, ProcessNative.SYNCHRONIZE);
        return process is not null && ProcessNative.WaitForSingleObject(process, 0) != ProcessNative.WAIT_OBJECT_0;
    }

    // the polite way, as Windows does before an update, so the app can save first; true when it's gone
    public static async Task<bool> AskToCloseAsync(FileHolder holder, TimeSpan wait)
    {
        if (!holder.CanClose) return !IsRunning(holder);
        using var process = OpenSame(holder, ProcessNative.SYNCHRONIZE);
        if (process is null) return true;
        // Restart Manager gives an app without a window 30 s before it gives up, so it isn't waited for
        _ = Task.Run(() => Shutdown(holder));
        return await Task.Run(() => ProcessNative.WaitForSingleObject(process, (uint)wait.TotalMilliseconds) == ProcessNative.WAIT_OBJECT_0);
    }

    // like Task Manager's End task: unsaved work in it is lost
    public static bool End(FileHolder holder)
    {
        if (!holder.CanClose) return false;
        using var process = OpenSame(holder, ProcessNative.PROCESS_TERMINATE | ProcessNative.SYNCHRONIZE);
        if (process is null) return true;
        if (!ProcessNative.TerminateProcess(process, 1)) return false;
        return ProcessNative.WaitForSingleObject(process, 5000) == ProcessNative.WAIT_OBJECT_0;
    }

    private static void Walk(string folder, List<string> files, List<string> folders, CancellationToken ct)
    {
        folders.Add(folder);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    if (folders.Count < MaxFolders) folders.Add(entry.FullName);
                }
                else if (files.Count < MaxFiles) files.Add(entry.FullName);
                else break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsLocal(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        const uint DriveRemote = 4;
        return Kernel32.GetDriveTypeW(Path.GetPathRoot(path)!) != DriveRemote;
    }

    private static List<RM_PROCESS_INFO> RmList(string[]? files, RM_UNIQUE_PROCESS[]? apps)
    {
        if (RestartManager.RmStartSession(out var session, 0, new StringBuilder(RestartManager.SessionKeyLength)) != 0) return [];
        try
        {
            if (RestartManager.RmRegisterResources(session, (uint)(files?.Length ?? 0), files, (uint)(apps?.Length ?? 0), apps, 0, null) != 0) return [];
            for (var attempt = 0; attempt < 3; attempt++)
            {
                uint count = 0;
                var rc = RestartManager.RmGetList(session, out var needed, ref count, null, out _);
                if (rc == 0) return [];
                if (rc != RestartManager.ERROR_MORE_DATA) return [];
                var list = new RM_PROCESS_INFO[needed];
                count = needed;
                rc = RestartManager.RmGetList(session, out _, ref count, list, out _);
                // ERROR_MORE_DATA again: a process opened one of the files in between
                if (rc == 0) return list.Take((int)count).ToList();
                if (rc != RestartManager.ERROR_MORE_DATA) return [];
            }
            return [];
        }
        finally { RestartManager.RmEndSession(session); }
    }

    private static void Shutdown(FileHolder holder)
    {
        if (RestartManager.RmStartSession(out var session, 0, new StringBuilder(RestartManager.SessionKeyLength)) != 0) return;
        try
        {
            RM_UNIQUE_PROCESS[] apps = [new() { dwProcessId = holder.ProcessId, ProcessStartTime = holder.StartTime }];
            if (RestartManager.RmRegisterResources(session, 0, null, 1, apps, 0, null) == 0)
                RestartManager.RmShutdown(session, 0, IntPtr.Zero);
        }
        finally { RestartManager.RmEndSession(session); }
    }

    internal static IReadOnlyList<int> ProcessesUsing(string path)
    {
        using var handle = VolumeNative.CreateFileW(path, VolumeNative.FILE_READ_ATTRIBUTES,
            VolumeNative.FILE_SHARE_READ | VolumeNative.FILE_SHARE_WRITE | VolumeNative.FILE_SHARE_DELETE, IntPtr.Zero,
            VolumeNative.OPEN_EXISTING, VolumeNative.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return [];
        for (var size = 1024; size <= 1 << 20; size *= 4)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = ProcessNative.NtQueryInformationFile(handle, out _, buffer, size, ProcessNative.FileProcessIdsUsingFileInformation);
                if (status == ProcessNative.STATUS_INFO_LENGTH_MISMATCH) continue;
                if (status != 0) return [];
                var count = Marshal.ReadInt32(buffer);
                var pids = new List<int>(count);
                for (var i = 0; i < count && IntPtr.Size * (i + 2) <= size; i++)
                    pids.Add((int)Marshal.ReadIntPtr(buffer, IntPtr.Size * (i + 1)));
                return pids;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    private static FileHolder ToHolder(RM_PROCESS_INFO info, uint session)
    {
        var pid = info.Process.dwProcessId;
        var exe = ExePathOf(pid);
        var name = info.strAppName is { Length: > 0 } app ? app
            : info.strServiceShortName is { Length: > 0 } service ? service
            : exe is not null ? Path.GetFileNameWithoutExtension(exe) : $"Process {pid}";
        var kind = pid == Environment.ProcessId ? HolderKind.Self
            : pid == SystemPid ? HolderKind.System
            : info.ApplicationType switch
            {
                RM_APP_TYPE.Service => HolderKind.Service,
                RM_APP_TYPE.Explorer => HolderKind.Explorer,
                RM_APP_TYPE.Critical => HolderKind.System,
                RM_APP_TYPE.Console => HolderKind.Console,
                _ => HolderKind.App,
            };
        if (kind == HolderKind.Self) name = "Explorer+";
        string? note = kind switch
        {
            HolderKind.Self => "This app",
            HolderKind.Service => "A Windows service",
            HolderKind.Explorer => "Windows Explorer",
            HolderKind.System => "Part of Windows",
            _ => null,
        };
        if (note is null && info.TSSessionId != session) note = "Another user's app";
        if (note is null && !CanTerminate(info.Process)) note = "Needs admin";
        return new FileHolder(pid, info.Process.ProcessStartTime, name, exe, kind, note is null, note);
    }

    private static bool CanTerminate(RM_UNIQUE_PROCESS process)
    {
        using var handle = ProcessNative.OpenProcess(ProcessNative.PROCESS_TERMINATE | ProcessNative.PROCESS_QUERY_LIMITED_INFORMATION, false, process.dwProcessId);
        return !handle.IsInvalid;
    }

    private static SafeProcessHandle? OpenSame(FileHolder holder, uint access)
    {
        var handle = ProcessNative.OpenProcess(access | ProcessNative.PROCESS_QUERY_LIMITED_INFORMATION, false, holder.ProcessId);
        if (handle.IsInvalid || StartTimeOf(handle) != holder.StartTime)
        {
            // gone, or the id now belongs to another process
            handle.Dispose();
            return null;
        }
        return handle;
    }

    private static long StartTimeOf(int pid)
    {
        using var handle = ProcessNative.OpenProcess(ProcessNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        return handle.IsInvalid ? 0 : StartTimeOf(handle);
    }

    private static long StartTimeOf(SafeProcessHandle handle) =>
        ProcessNative.GetProcessTimes(handle, out var created, out _, out _, out _) ? created : 0;

    private static string? ExePathOf(int pid)
    {
        using var handle = ProcessNative.OpenProcess(ProcessNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024];
        var size = buffer.Length;
        return ProcessNative.QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }
}
