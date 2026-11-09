using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;
using Microsoft.Win32;

namespace ExplorerPlus.App.Views;

public sealed class PreviewHost : HwndHost
{
    private IntPtr _hwnd;
    private int _generation;
    private PreviewSession? _session; // touched only on the preview thread

    /// <summary>Raised on the UI thread when a handler couldn't show its file.</summary>
    public event Action<PreviewTarget>? Failed;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = PreviewNative.CreateWindowExW(0, "Static", null,
            PreviewNative.WS_CHILD | PreviewNative.WS_VISIBLE | PreviewNative.WS_CLIPCHILDREN,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Clear();
        PreviewNative.DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    public void Show(PreviewTarget target)
    {
        if (_hwnd == IntPtr.Zero) return;
        var generation = Interlocked.Increment(ref _generation);
        var hwnd = _hwnd;
        var rect = PixelRect();
        var (background, foreground) = Colors();
        _ = StaWorkerPool.Preview.Run(() =>
        {
            _session?.Dispose();
            _session = null;
            if (Volatile.Read(ref _generation) != generation) return true;
            _session = PreviewSession.Open(target.Handler, target.Path, hwnd, rect, background, foreground);
            if (Volatile.Read(ref _generation) != generation) { _session?.Dispose(); _session = null; return true; }
            return _session is not null;
        }).ContinueWith(t =>
        {
            if ((t.IsFaulted || !t.Result) && Volatile.Read(ref _generation) == generation) Failed?.Invoke(target);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _ = StaWorkerPool.Preview.Run(() =>
        {
            _session?.Dispose();
            _session = null;
        });
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var rect = PixelRect();
        _ = StaWorkerPool.Preview.Run(() => _session?.Resize(rect));
    }

    private RECT PixelRect()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new RECT(0, 0, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
    }

    /// <summary>Handlers that support it get our colours, so a dark app doesn't show a white page.</summary>
    private static (uint Background, uint Foreground) Colors()
    {
        var light = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception) { }
        // COLORREF is 0x00BBGGRR.
        return light ? (0xFFFFFFu, 0x000000u) : (0x202020u, 0xFFFFFFu);
    }
}
