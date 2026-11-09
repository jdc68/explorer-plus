using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Views;

public partial class MainWindow
{
    // ================= Tab drag: reorder, move to another window, tear off into a new one =================

    private const string TabDragFormat = "ExplorerPlus.Tab";

    /// <summary>The tab being dragged. Tab drags never leave the process, so the payload stays here, not in the data object.</summary>
    private static TabDrag? _tabDrag;

    private sealed class TabDrag(MainWindow source, TabViewModel tab)
    {
        public MainWindow Source { get; } = source;
        public TabViewModel Tab { get; } = tab;
        public bool Dropped { get; set; }
        public bool Cancelled { get; set; }
    }

    private Point? _tabPressPoint;
    private TabViewModel? _tabPressed;

    private void OnTabStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        _tabPressPoint = null;
        if (FindAncestor<Button>(source) is not null) return; // the close button
        if (FindAncestor<ListBoxItem>(source)?.DataContext is not TabViewModel tab) return;
        _tabPressPoint = e.GetPosition(TabStrip);
        _tabPressed = tab;
    }

    private void OnTabStripMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabPressPoint is not { } start || _tabPressed is not { } tab) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _tabPressPoint = null; return; }
        var d = e.GetPosition(TabStrip) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _tabPressPoint = null;
        DragTab(tab);
    }

    private void DragTab(TabViewModel tab)
    {
        var drag = _tabDrag = new TabDrag(this, tab);
        var data = new DataObject(TabDragFormat, TabDragFormat);
        QueryContinueDragEventHandler query = (_, e) => { if (e.EscapePressed) drag.Cancelled = true; };
        DragDrop.AddQueryContinueDragHandler(TabStrip, query);
        try { DragDrop.DoDragDrop(TabStrip, data, DragDropEffects.Move); }
        catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
        finally
        {
            DragDrop.RemoveQueryContinueDragHandler(TabStrip, query);
            _tabDrag = null;
        }

        // Let go outside every Explorer+ window: the tab becomes a window of its own, as in a browser.
        if (!drag.Dropped && !drag.Cancelled && ViewModel.Tabs.Count > 1 && ViewModel.Tabs.Contains(tab))
            TearOff(tab);
    }

    private void TearOff(TabViewModel tab)
    {
        User32.GetCursorPos(out var pt);
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var at = fromDevice.Transform(new Point(pt.X, pt.Y));
        ViewModel.DetachTab(tab);
        var window = new MainWindow(tab, new Point(at.X - 80, at.Y - 18));
        window.Show();
        window.BringToFront();
    }

    private static bool IsTabDrag(DragEventArgs e) => _tabDrag is not null && e.Data.GetDataPresent(TabDragFormat);

    private void OnWindowPreviewTabDrag(object sender, DragEventArgs e)
    {
        if (!IsTabDrag(e)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.Move;
        var drag = _tabDrag!;
        // In its own window the tab follows the pointer along the strip.
        if (drag.Source == this && TabInsertIndex(e, drag.Tab) is { } index) ViewModel.MoveTab(drag.Tab, index);
    }

    private void OnWindowPreviewTabDragLeave(object sender, DragEventArgs e)
    {
        if (IsTabDrag(e)) e.Handled = true;
    }

    private void OnWindowPreviewTabDrop(object sender, DragEventArgs e)
    {
        if (!IsTabDrag(e)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.Move;
        var drag = _tabDrag!;
        drag.Dropped = true;
        if (drag.Source == this) return; // already in place

        var index = TabInsertIndex(e, null) ?? ViewModel.Tabs.Count;
        var source = drag.Source;
        source.ViewModel.DetachTab(drag.Tab);
        ViewModel.AttachTab(drag.Tab, index);
        BringToFront();
        // A window whose last tab moved away has nothing left to show.
        if (source.ViewModel.Tabs.Count == 0) Dispatcher.BeginInvoke(source.Close);
    }

    /// <summary>
    /// Where a dragged tab goes when dropped at the pointer: before or after the tab under it, or null when the
    /// pointer isn't over a tab. For the tab's own strip it only moves once the pointer passes the other tab's middle,
    /// so tabs of different widths don't swap back and forth.
    /// </summary>
    private int? TabInsertIndex(DragEventArgs e, TabViewModel? moving)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { DataContext: TabViewModel over } row) return null;
        var target = ViewModel.Tabs.IndexOf(over);
        if (target < 0) return null;
        var afterMiddle = e.GetPosition(row).X > row.ActualWidth / 2;
        if (moving is null) return afterMiddle ? target + 1 : target;
        var from = ViewModel.Tabs.IndexOf(moving);
        if (from < 0 || over == moving) return null;
        if (target > from && afterMiddle) return target;
        if (target < from && !afterMiddle) return target;
        return null;
    }

    // ================= Title bar =================

    /// <summary>A maximized window hangs its resize border off the screen; pad the content so nothing is cut off.</summary>
    private void ApplyTitleBarLayout()
    {
        var t = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = WindowState == WindowState.Maximized && WindowStyle != WindowStyle.None
            ? new Thickness(t.Left, t.Top, t.Right, t.Bottom)
            : new Thickness(0);
        // Full screen (F11) has no caption buttons to keep clear of.
        CaptionButtonsColumn.Width = new GridLength(WindowStyle == WindowStyle.None ? 0 : 140);
    }
}
