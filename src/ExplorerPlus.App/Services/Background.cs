using System.Diagnostics;
using System.Runtime;
using System.Windows.Interop;
using ExplorerPlus.Interop;
using Microsoft.Win32;

namespace ExplorerPlus.App.Services;

public static class Background
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Explorer+";
    private const int WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;

    private static HwndSource? _listener;

    /// <summary>
    /// A hidden top-level window, so sign-out, the Restart Manager (installer upgrades) and a plain WM_CLOSE can ask
    /// Explorer+ to exit even while it has no visible window.
    /// </summary>
    public static void Listen(Action exit)
    {
        _listener = new HwndSource(new HwndSourceParameters("Explorer+ background") { WindowStyle = 0, ParentWindow = IntPtr.Zero });
        _listener.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            switch (msg)
            {
                case WM_QUERYENDSESSION:
                    handled = true;
                    return new IntPtr(1);
                case WM_ENDSESSION when wParam != IntPtr.Zero:
                case WM_CLOSE:
                    handled = true;
                    exit();
                    break;
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>Gives memory back after the last window closes; caches that make the next window fast are kept.</summary>
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var self = Process.GetCurrentProcess();
        Kernel32.EmptyWorkingSet(self.Handle);
    }

    /// <summary>
    /// Adds or removes the sign-in entry to match the settings, pointing at this exe. Dev profiles never touch it.
    /// Touches the registry, so call it off the UI thread.
    /// </summary>
    public static void SyncStartupEntry()
    {
        if (AppPaths.Profile is not null || Environment.ProcessPath is not { } exe) return;
        var s = AppSettings.Current;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (s.KeepRunningInBackground && s.StartWithWindows) key.SetValue(RunValue, $"\"{exe}\" {Program.BackgroundArg}");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch { /* policy can lock the Run key; Explorer+ then just isn't started at sign-in */ }
    }

    public static void RemoveStartupEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
