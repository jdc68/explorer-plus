using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Views;

public partial class MainWindow
{
    private Action? _onMenuIdle;

    internal TabViewModel? PerfTab => Tab;

    // null when the menu never showed
    internal async Task<double?> MeasureItemMenuAsync(ItemViewModel item)
    {
        FileList.UnselectAll();
        FileList.SelectedItem = item;
        FileList.ScrollIntoView(item);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        BringToFront();

        HwndSource.FromHwnd(_hwnd)?.AddHook(PerfHook);
        double? shown = null;
        var watch = Stopwatch.StartNew();
        _onMenuIdle = () =>
        {
            shown ??= watch.Elapsed.TotalMilliseconds;
            User32.EndMenu();
        };
        // a menu that never goes idle must not hang the run
        var guard = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Send, (_, _) => User32.EndMenu(), Dispatcher);
        guard.Start();
        try
        {
            var p = FileList.PointToScreen(new Point(FileList.ActualWidth / 2, FileList.ActualHeight / 3));
            ShowItemsMenu([item], (int)p.X, (int)p.Y);
        }
        finally
        {
            guard.Stop();
            _onMenuIdle = null;
            HwndSource.FromHwnd(_hwnd)?.RemoveHook(PerfHook);
        }
        return shown;
    }

    internal void SetFilterBoxText(string text) => FilterBox.Text = text;

    // each step timed until the UI thread is idle again
    internal async Task<object> MeasureScrollAsync()
    {
        if (FindDescendant<ScrollViewer>(FileList) is not { } sv) return "no scroll viewer";
        async Task<List<double>> Steps(Func<double> step, int count)
        {
            var times = new List<double>();
            for (var i = 0; i < count; i++)
            {
                var watch = Stopwatch.StartNew();
                sv.ScrollToVerticalOffset(sv.VerticalOffset + step());
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                times.Add(watch.Elapsed.TotalMilliseconds);
                await Task.Delay(30);
            }
            return times;
        }
        sv.ScrollToTop();
        await Task.Delay(500);
        var pages = await Steps(() => sv.ViewportHeight, 20);
        var wheel = await Steps(() => sv.ViewportHeight / 10, 60);
        return new { pages, wheel };
    }

    private IntPtr PerfHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM.ENTERIDLE && _onMenuIdle is { } idle) idle();
        return IntPtr.Zero;
    }
}
