using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Indexing;

namespace ExplorerPlus.App.Services;

/// <summary>Installs, updates and removes the index service by running its own exe elevated (one UAC prompt each).</summary>
public static class IndexServiceSetup
{
    private const int ErrorCancelled = 1223;

    /// <summary>The service exe shipped next to Explorer+; it copies itself to Program Files when installing.</summary>
    public static string ServiceExePath => Path.Combine(AppContext.BaseDirectory, IndexClient.ServiceExe);

    public static bool IsShipped => File.Exists(ServiceExePath);

    public static string AppVersion => IndexProtocol.Version;

    /// <summary>Runs the service exe elevated with <c>--install</c> or <c>--uninstall</c>. Returns null on success, or what went wrong.</summary>
    public static async Task<string?> RunAsync(bool install)
    {
        if (!IsShipped) return "The index service isn't part of this copy of Explorer+.";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(ServiceExePath, install ? "--install" : "--uninstall")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return "Windows didn't start the installer.";
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : new Win32Exception(process.ExitCode).Message;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return "Cancelled.";
        }
        catch (Win32Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            IndexSearch.Invalidate();
        }
    }

    /// <summary>Uninstalling Explorer+: take the service with it (one UAC prompt), and don't wait for an answer.</summary>
    public static void RemoveOnUninstall()
    {
        if (!IsShipped || IndexClient.QueryService() == IndexServiceState.NotInstalled) return;
        try
        {
            Process.Start(new ProcessStartInfo(ServiceExePath, "--uninstall") { UseShellExecute = true, Verb = "runas" })?.WaitForExit(60_000);
        }
        catch (Win32Exception) { }
    }
}
