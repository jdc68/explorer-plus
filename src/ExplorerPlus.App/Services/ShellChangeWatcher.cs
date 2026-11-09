using System.Runtime.InteropServices;
using System.Windows.Interop;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Services;

public sealed record ShellChange(SHCNE Event, string? Path1, string? Path2);

public sealed class ShellChangeWatcher
{
    private const int BaseMessage = WM.APP + 0x100;

    private readonly HwndSource _window;
    private readonly Dictionary<int, (uint Id, Action<ShellChange> Callback)> _registrations = [];
    private int _next;

    public static ShellChangeWatcher Instance { get; } = new();

    private ShellChangeWatcher()
    {
        var p = new HwndSourceParameters("ExplorerPlusShellNotify") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);
    }

    public IDisposable Watch(Pidl folder, bool recursive, SHCNE events, Action<ShellChange> callback)
    {
        var slot = _next++;
        using var native = folder.ToNative();
        var entry = new SHChangeNotifyEntry { pidl = native.Ptr, fRecursive = recursive };
        var id = Shell32.SHChangeNotifyRegister(_window.Handle,
            SHCNRF.ShellLevel | SHCNRF.InterruptLevel | SHCNRF.NewDelivery | (recursive ? SHCNRF.RecursiveInterrupt : 0),
            (int)events, (uint)(BaseMessage + slot), 1, ref entry);
        if (id == 0) return new Registration(() => { });
        _registrations[slot] = (id, callback);
        return new Registration(() =>
        {
            if (_registrations.Remove(slot, out var r)) Shell32.SHChangeNotifyDeregister(r.Id);
        });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var slot = msg - BaseMessage;
        if (slot < 0 || !_registrations.TryGetValue(slot, out var reg)) return IntPtr.Zero;
        handled = true;

        var hLock = Shell32.SHChangeNotification_Lock(wParam, (uint)lParam.ToInt64(), out var pppidl, out var ev);
        if (hLock == IntPtr.Zero) return IntPtr.Zero;
        ShellChange change;
        try
        {
            var p1 = Marshal.ReadIntPtr(pppidl);
            var p2 = Marshal.ReadIntPtr(pppidl, IntPtr.Size);
            change = new ShellChange((SHCNE)ev, PathOf(p1), PathOf(p2));
        }
        finally { Shell32.SHChangeNotification_Unlock(hLock); }

        try { reg.Callback(change); } catch { /* a bad callback must not break notifications */ }
        return IntPtr.Zero;
    }

    private static string? PathOf(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero) return null;
        if (Shell32.SHGetNameFromIDList(pidl, SIGDN.FILESYSPATH, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }
}
