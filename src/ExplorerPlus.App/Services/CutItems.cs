using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Services;

public static class CutItems
{
    private static HwndSource? _window;
    private static HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private static int _readVersion;

    // every path that was cut before or is cut now
    public static event Action<IReadOnlySet<string>>? Changed;

    public static bool IsCut(string? path) => path is not null && _paths.Count > 0 && _paths.Contains(Normalize(path));

    // cuts made in other apps (File Explorer) show too
    public static void Start()
    {
        if (_window is not null) return;
        var p = new HwndSourceParameters("ExplorerPlusClipboard") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);
        User32.AddClipboardFormatListener(_window.Handle);
        ScheduleRead(0);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_CLIPBOARDUPDATE) ScheduleRead(0);
        return IntPtr.Zero;
    }

    // the clipboard is often still open by the app that just wrote it
    private static async void ScheduleRead(int attempt)
    {
        var version = ++_readVersion;
        await Task.Delay(attempt == 0 ? 30 : 150);
        if (version != _readVersion) return;
        HashSet<string> now;
        try { now = ReadCut(); }
        catch (COMException) when (attempt < 5) { ScheduleRead(attempt + 1); return; }
        catch { now = new(StringComparer.OrdinalIgnoreCase); }
        if (now.SetEquals(_paths)) return;
        var touched = new HashSet<string>(_paths, StringComparer.OrdinalIgnoreCase);
        touched.UnionWith(now);
        _paths = now;
        Changed?.Invoke(touched);
    }

    private static HashSet<string> ReadCut()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Clipboard.ContainsFileDropList()) return set;
        if (Clipboard.GetData("Preferred DropEffect") is not MemoryStream ms || ms.Length < 4) return set;
        var effect = new BinaryReader(ms).ReadInt32();
        if ((effect & 2) == 0 || (effect & 1) != 0) return set;
        foreach (var f in Clipboard.GetFileDropList()) if (f is { Length: > 0 }) set.Add(Normalize(f));
        return set;
    }

    private static string Normalize(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;
}
