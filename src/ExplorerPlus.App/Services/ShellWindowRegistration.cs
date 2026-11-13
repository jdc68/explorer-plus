using System.Runtime.InteropServices;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Services;

public sealed class ShellWindowRegistration : IDisposable
{
    // MTA, so calls into the site don't wait for the UI thread
    private static readonly object Gate = new();
    private static Task _queue = Task.CompletedTask;
    private static IShellWindows? _shellWindows;

    private readonly ShellWindowSite _site;
    private readonly IntPtr _hwnd;
    private IShellWindows? _registeredWith;
    private readonly List<int> _cookies = [];
    private bool _disposed;

    public ShellWindowRegistration(IntPtr hwnd, Action<Pidl, uint> select, Action<Pidl?, uint> browse, Action close)
    {
        _hwnd = hwnd;
        _site = new ShellWindowSite(hwnd, select, browse, close);
    }

    public void Navigated(ShellLocation? location)
    {
        // app-made locations borrow This PC's PIDL
        _site.Location = location is null || location.IsTagView || location.IsDuplicates || location.IsIndexSearch ? null : location;
        Enqueue(Sync);
    }

    public void Activated(bool active) => Enqueue(() =>
    {
        if (_registeredWith is not { } sw) return;
        foreach (var cookie in _cookies) sw.OnActivated(cookie, active);
    });

    // after explorer.exe restarts
    public void Resync() => Enqueue(Sync);

    public void Dispose()
    {
        Enqueue(() =>
        {
            _disposed = true;
            Revoke();
        });
    }

    // so explorer.exe keeps no entries for closed windows
    public static void Flush(TimeSpan timeout)
    {
        Task queue;
        lock (Gate) queue = _queue;
        queue.Wait(timeout);
    }

    private static void Enqueue(Action action)
    {
        lock (Gate)
            _queue = _queue.ContinueWith(_ =>
            {
                try { action(); }
                catch { }
            }, TaskScheduler.Default);
    }

    private void Sync()
    {
        if (_disposed) return;
        if (_site.Location is not { } location)
        {
            Revoke();
            return;
        }
        var forms = FormsOf(location);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var sw = _shellWindows ??= (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowClass.CLSID_ShellWindows, true)!)!;
                if (!ReferenceEquals(_registeredWith, sw))
                {
                    _cookies.Clear();
                    _registeredWith = sw;
                }
                while (_cookies.Count < forms.Count)
                {
                    _cookies.Add(sw.Register(_site, (int)_hwnd, ShellWindowClass.SWC_BROWSER));
                }
                while (_cookies.Count > forms.Count)
                {
                    sw.Revoke(_cookies[^1]);
                    _cookies.RemoveAt(_cookies.Count - 1);
                }
                for (var i = 0; i < forms.Count; i++)
                {
                    object loc = forms[i];
                    sw.OnNavigate(_cookies[i], ref loc);
                }
                return;
            }
            catch when (attempt == 0)
            {
                _shellWindows = null;
                _registeredWith = null;
                _cookies.Clear();
            }
        }
    }

    // The shell only matches PIDLs with the same root. On a drive also listed as removable, a parsed folder path
    // is rooted there, while files and File Explorer's windows go through This PC
    private static List<byte[]> FormsOf(ShellLocation location)
    {
        var forms = new List<byte[]> { location.Pidl.ToArray() };
        if (location.IsFileSystemFolder && location.FileSystemPath is { } path)
        {
            AddForm(forms, Shell32.ILCreateFromPathW(path));
            if (Shell32.SHParseDisplayName(path, IntPtr.Zero, out var parsed, 0, out _) == 0) AddForm(forms, parsed);
        }
        return forms;
    }

    private static void AddForm(List<byte[]> forms, IntPtr pidl)
    {
        if (pidl == IntPtr.Zero) return;
        try
        {
            var bytes = new byte[Shell32.ILGetSize(pidl)];
            Marshal.Copy(pidl, bytes, 0, bytes.Length);
            if (!forms.Any(f => f.AsSpan().SequenceEqual(bytes)) && !forms.Any(f => SameKind(f, bytes))) forms.Add(bytes);
        }
        finally { Shell32.ILFree(pidl); }
    }

    // same root item, the shell matches these itself
    private static bool SameKind(byte[] a, byte[] b)
    {
        if (a.Length < 2 || b.Length < 2) return false;
        int la = a[0] | a[1] << 8, lb = b[0] | b[1] << 8;
        return la == lb && la <= a.Length && la <= b.Length && a.AsSpan(0, la).SequenceEqual(b.AsSpan(0, lb));
    }

    private void Revoke()
    {
        var sw = _registeredWith;
        _registeredWith = null;
        if (sw is not null)
        {
            foreach (var cookie in _cookies)
            {
                try { sw.Revoke(cookie); }
                catch { }
            }
        }
        _cookies.Clear();
    }
}
