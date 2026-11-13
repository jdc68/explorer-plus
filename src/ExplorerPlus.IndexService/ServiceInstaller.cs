using System.ComponentModel;
using System.Runtime.InteropServices;
using ExplorerPlus.Indexing;
using ExplorerPlus.Interop;

namespace ExplorerPlus.IndexService;

/// <summary>
/// Installs, updates and removes the service. Runs elevated. The service's files are copied to Program Files first:
/// Explorer+ itself lives in the user's own (writable) AppData, and a LocalSystem service must never run a file
/// that a normal process could replace.
/// </summary>
internal static class ServiceInstaller
{
    private const uint ServiceAccess = ServiceNative.SERVICE_QUERY_STATUS | ServiceNative.SERVICE_START | ServiceNative.SERVICE_STOP |
                                       ServiceNative.SERVICE_CHANGE_CONFIG | ServiceNative.SERVICE_QUERY_CONFIG | ServiceNative.DELETE;

    /// <summary>The files the service needs, all of which sit next to this exe.</summary>
    private static IEnumerable<string> ServiceFiles(string folder) =>
        Directory.EnumerateFiles(folder, "ExplorerPlus.IndexService.*")
            .Concat([Path.Combine(folder, "ExplorerPlus.Indexing.dll"), Path.Combine(folder, "ExplorerPlus.Interop.dll")])
            .Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));

    public static int Install()
    {
        var target = IndexClient.InstallFolder;
        var exe = Path.Combine(target, IndexClient.ServiceExe);
        var manager = OpenManager();
        try
        {
            var service = ServiceNative.OpenServiceW(manager, IndexClient.ServiceName, ServiceAccess);
            if (service != IntPtr.Zero)
            {
                StopAndWait(service);
            }
            CopyFiles(AppContext.BaseDirectory, target);

            var path = $"\"{exe}\" --service";
            if (service == IntPtr.Zero)
            {
                service = ServiceNative.CreateServiceW(manager, IndexClient.ServiceName, IndexClient.ServiceDisplayName, ServiceAccess,
                    ServiceNative.SERVICE_WIN32_OWN_PROCESS, ServiceNative.SERVICE_AUTO_START, ServiceNative.SERVICE_ERROR_NORMAL,
                    path, null, IntPtr.Zero, null, null, null);
                if (service == IntPtr.Zero) throw new Win32Exception();
            }
            else if (!ServiceNative.ChangeServiceConfigW(service, ServiceNative.SERVICE_NO_CHANGE, ServiceNative.SERVICE_AUTO_START,
                         ServiceNative.SERVICE_NO_CHANGE, path, null, IntPtr.Zero, null, null, null, IndexClient.ServiceDisplayName))
            {
                throw new Win32Exception();
            }
            try
            {
                Configure(service);
                if (!ServiceNative.StartServiceW(service, 0, IntPtr.Zero)) throw new Win32Exception();
            }
            finally { ServiceNative.CloseServiceHandle(service); }
            Log.Write($"Installed {IndexProtocol.Version} from {AppContext.BaseDirectory}");
            return 0;
        }
        finally { ServiceNative.CloseServiceHandle(manager); }
    }

    public static int Uninstall()
    {
        var manager = OpenManager();
        try
        {
            var service = ServiceNative.OpenServiceW(manager, IndexClient.ServiceName, ServiceAccess);
            if (service != IntPtr.Zero)
            {
                try
                {
                    StopAndWait(service);
                    if (!ServiceNative.DeleteService(service) && Marshal.GetLastWin32Error() != ServiceNative.ERROR_SERVICE_MARKED_FOR_DELETE)
                        throw new Win32Exception();
                }
                finally { ServiceNative.CloseServiceHandle(service); }
            }
        }
        finally { ServiceNative.CloseServiceHandle(manager); }

        var target = IndexClient.InstallFolder;
        if (!Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\').Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.Delete(target, recursive: true); } catch (DirectoryNotFoundException) { } catch (IOException) { }
        }
        return 0;
    }

    private static IntPtr OpenManager()
    {
        var manager = ServiceNative.OpenSCManagerW(null, null, ServiceNative.SC_MANAGER_CONNECT | ServiceNative.SC_MANAGER_CREATE_SERVICE);
        return manager != IntPtr.Zero ? manager : throw new Win32Exception();
    }

    private static void Configure(IntPtr service)
    {
        var description = new SERVICE_DESCRIPTION
        {
            Description = "Keeps a list of the file names on your NTFS drives, so Explorer+ can search them instantly. " +
                          "Searches only return files the searching user is allowed to see.",
        };
        ServiceNative.ChangeServiceDescription(service, ServiceNative.SERVICE_CONFIG_DESCRIPTION, ref description);

        // Restart after a crash: after 5 seconds, then after a minute; the count resets after a day.
        SC_ACTION[] actions = [new() { Type = ServiceNative.SC_ACTION_RESTART, Delay = 5_000 }, new() { Type = ServiceNative.SC_ACTION_RESTART, Delay = 60_000 }];
        var handle = GCHandle.Alloc(actions, GCHandleType.Pinned);
        try
        {
            var failure = new SERVICE_FAILURE_ACTIONS { ResetPeriod = 86_400, ActionCount = (uint)actions.Length, Actions = handle.AddrOfPinnedObject() };
            ServiceNative.ChangeServiceFailureActions(service, ServiceNative.SERVICE_CONFIG_FAILURE_ACTIONS, ref failure);
        }
        finally { handle.Free(); }
    }

    private static void StopAndWait(IntPtr service)
    {
        if (!ServiceNative.QueryServiceStatus(service, out var status) || status.CurrentState == ServiceNative.SERVICE_STOPPED) return;
        ServiceNative.ControlService(service, ServiceNative.SERVICE_CONTROL_STOP, out _);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (ServiceNative.QueryServiceStatus(service, out status) && status.CurrentState == ServiceNative.SERVICE_STOPPED) break;
            Thread.Sleep(200);
        }
        // The process can outlive the "stopped" state for a moment, holding its files.
        Thread.Sleep(500);
    }

    private static void CopyFiles(string from, string to)
    {
        from = Path.GetFullPath(from);
        if (from.TrimEnd('\\').Equals(Path.GetFullPath(to).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(to);
        foreach (var file in ServiceFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(file));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(file, dest, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(250);
                }
            }
        }
    }
}
