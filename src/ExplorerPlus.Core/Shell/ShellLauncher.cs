using System.ComponentModel;
using System.Runtime.InteropServices;
using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public enum TerminalKind { Default, Cmd, PowerShell, Pwsh, WindowsTerminal }

public static class ShellLauncher
{
    private const int SW_SHOWNORMAL = 1;

    public static Task OpenAsync(FolderEntry entry, IntPtr owner, string? verb = null) =>
        StaWorkerPool.General.Run(() => Execute(entry.GetPidl(), owner, verb, null));

    public static Task OpenAsync(Pidl pidl, IntPtr owner, string? verb = null) =>
        StaWorkerPool.General.Run(() => Execute(pidl, owner, verb, null));

    public static Task RunAsync(string file, string? arguments, string? workingDirectory, IntPtr owner, string? verb = null) =>
        StaWorkerPool.General.Run(() =>
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE.MASK_UNICODE,
                hwnd = owner,
                lpVerb = verb,
                lpFile = file,
                lpParameters = arguments,
                lpDirectory = workingDirectory,
                nShow = SW_SHOWNORMAL,
            };
            if (!Shell32.ShellExecuteEx(ref info)) ThrowLast(file);
        });

    public static Task OpenTerminalAsync(string directory, TerminalKind kind, IntPtr owner, bool elevated = false)
    {
        if (kind == TerminalKind.Default) kind = IsOnPath("wt.exe") ? TerminalKind.WindowsTerminal : TerminalKind.PowerShell;
        var verb = elevated ? "runas" : null;
        return kind switch
        {
            TerminalKind.WindowsTerminal => RunAsync("wt.exe", $"-d \"{directory.TrimEnd('\\')}\\.\"", directory, owner, verb),
            TerminalKind.Cmd => RunAsync("cmd.exe", null, directory, owner, verb),
            TerminalKind.Pwsh => RunAsync("pwsh.exe", "-NoLogo", directory, owner, verb),
            _ => RunAsync("powershell.exe", "-NoLogo", directory, owner, verb),
        };
    }

    private static void Execute(Pidl pidl, IntPtr owner, string? verb, string? directory)
    {
        using var native = pidl.ToNative();
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE.MASK_INVOKEIDLIST | SEE.MASK_UNICODE,
            hwnd = owner,
            lpVerb = verb,
            lpIDList = native.Ptr,
            lpDirectory = directory,
            nShow = SW_SHOWNORMAL,
        };
        if (!Shell32.ShellExecuteEx(ref info)) ThrowLast(null);
    }

    private static void ThrowLast(string? what)
    {
        var err = Marshal.GetLastWin32Error();
        if (err == 1223) throw new OperationCanceledException(); // user cancelled (UAC, Open With)
        var msg = new Win32Exception(err).Message;
        throw new InvalidOperationException(what is null ? msg : $"Windows can't open \"{what}\". {msg}");
    }

    public static bool IsOnPath(string exe)
    {
        var localApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        if (File.Exists(Path.Combine(localApps, exe))) return true;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(p =>
        {
            try { return File.Exists(Path.Combine(p.Trim(), exe)); } catch { return false; }
        });
    }
}
