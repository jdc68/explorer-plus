using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace ExplorerPlus.App.Views;

public partial class MainWindow
{
    // ================= Drag source =================

    private Point? _pressPoint;
    private bool _pressOnItem;
    private ItemViewModel? _deferredSelect;
    private List<ItemViewModel>? _dragging;

    /// <summary>
    /// Paths of the files an Explorer+ window is dragging right now (any window: drags never leave the UI thread).
    /// Drops of these into a folder go through the transfer queue; drops from other apps stay with the shell, because
    /// some sources (archivers, browsers) delete what they dropped as soon as the drop returns.
    /// </summary>
    private static List<string>? _internalDragPaths;

    private void OnFileListPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(FileList);
        _pressOnItem = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not null;
    }

    private void OnFileListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeStart is not null) { UpdateMarquee(e.GetPosition(FileList)); return; }
        if (_pressPoint is not { } start || !_pressOnItem) return;
        if (e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed) { _pressPoint = null; return; }
        var d = e.GetPosition(FileList) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressPoint = null;
        StartDrag();
    }

    private void StartDrag()
    {
        var items = Selection;
        if (items.Count == 0 || _renaming is not null) return;
        List<Pidl> pidls;
        try { pidls = items.Select(i => i.Entry.GetPidl()).ToList(); }
        catch { return; }
        _deferredSelect = null;
        _dragging = items;
        var paths = items.Select(i => i.Entry.Path).ToList();
        _internalDragPaths = paths.All(p => p is not null) ? paths.OfType<string>().ToList() : null;
        Mouse.Capture(null);
        try { ShellDragDrop.DoDrag(pidls, _hwnd); }
        catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
        finally
        {
            _dragging = null;
            _internalDragPaths = null;
            // the drag is over, so no window may still hold a drag image or a hovered target
            foreach (var window in App.MainWindows) window.EndDragHere(false);
        }
    }

    // ================= Drop targets (file list, sidebar, tabs) =================

    /// <summary>A file-system folder a drop can be queued into.</summary>
    private sealed record DropFolder(Pidl Pidl, string Path, string Name);

    private sealed class DropSession(object key, IDropTarget target, IntPtr data) : IDisposable
    {
        public object Key { get; } = key;

        public DropFolder? Folder { get; init; }

        /// <summary>What the shell said the drop would do, last time the pointer moved (copy, move, link).</summary>
        public uint LastEffect { get; private set; }

        public uint Enter(uint keys, POINT pt, uint allowed)
        {
            var effect = allowed;
            return LastEffect = target.DragEnter(data, keys, pt, ref effect) == 0 ? effect : DropEffect.None;
        }

        public uint Over(uint keys, POINT pt, uint allowed)
        {
            var effect = allowed;
            return LastEffect = target.DragOver(keys, pt, ref effect) == 0 ? effect : DropEffect.None;
        }

        public void Leave() => target.DragLeave();

        public uint Drop(uint keys, POINT pt, uint allowed)
        {
            var effect = allowed;
            return target.Drop(data, keys, pt, ref effect) == 0 ? effect : DropEffect.None;
        }

        public void Dispose() => ShellNative.Release(target);
    }

    private DropSession? _drop;
    private IntPtr _dropData;
    private IDropTargetHelper? _dropHelper;
    private bool _helperEntered;
    private ItemViewModel? _dropHighlight;
    private DispatcherTimer? _tabSwitchTimer;
    private TabViewModel? _tabSwitchCandidate;
    private bool _dropRightButton;

    private static POINT CursorPoint()
    {
        User32.GetCursorPos(out var p);
        return p;
    }

    private IntPtr DataPointer(DragEventArgs e)
    {
        // WPF's DataObject forwards to the original OLE data object of the drag, so the shell sees the real thing.
        if (_dropData == IntPtr.Zero) _dropData = Marshal.GetComInterfaceForObject(e.Data, typeof(ComDataObject));
        return _dropData;
    }

    private IDropTargetHelper? DropHelper
    {
        get
        {
            if (_dropHelper is not null) return _dropHelper;
            try { _dropHelper = (IDropTargetHelper)Activator.CreateInstance(Type.GetTypeFromCLSID(OleDragDrop.CLSID_DragDropHelper)!)!; }
            catch { }
            return _dropHelper;
        }
    }

    /// <summary>Routes the drag to the shell drop target for <paramref name="key"/> (null: nothing droppable here).</summary>
    private void DragOverTarget(DragEventArgs e, object? key, Func<(Pidl Pidl, bool Background)?> resolve, Func<Pidl, DropFolder?>? folder = null)
    {
        e.Handled = true;
        var pt = CursorPoint();
        var keys = (uint)e.KeyStates;
        // At drop time the button is already up, so remember whether this is a right-drag (the shell shows a menu then).
        _dropRightButton = e.KeyStates.HasFlag(DragDropKeyStates.RightMouseButton);
        var allowed = (uint)e.AllowedEffects;
        var data = DataPointer(e);
        if (!_helperEntered && DropHelper is { } h)
        {
            h.DragEnter(_hwnd, data, ref pt, allowed);
            _helperEntered = true;
        }

        uint effect = DropEffect.None;
        if (key is null) EndDropSession();
        else if (_drop is null || !Equals(_drop.Key, key))
        {
            EndDropSession();
            if (resolve() is { } r && ShellDragDrop.GetDropTarget(r.Pidl, r.Background, _hwnd) is { } target)
            {
                _drop = new DropSession(key, target, data) { Folder = folder?.Invoke(r.Pidl) };
                effect = _drop.Enter(keys, pt, allowed);
            }
        }
        else effect = _drop.Over(keys, pt, allowed);

        SetDropHighlight(key as ItemViewModel);
        e.Effects = (DragDropEffects)effect;
        _dropHelper?.DragOver(ref pt, effect);
    }

    private void DropOnTarget(DragEventArgs e)
    {
        e.Handled = true;
        if (TryQueueDrop(e)) return;
        var pt = CursorPoint();
        var effect = DropEffect.None;
        try
        {
            if (_drop is not null) effect = _drop.Drop((uint)e.KeyStates, pt, (uint)e.AllowedEffects);
            if (_dropData != IntPtr.Zero) _dropHelper?.Drop(_dropData, ref pt, effect);
        }
        catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
        finally
        {
            _drop?.Dispose();
            _drop = null;
            _helperEntered = false;
            ReleaseDragData();
        }
        e.Effects = (DragDropEffects)effect;
    }

    /// <summary>
    /// A left-drag of Explorer+'s own files onto a folder, as a copy or a move: tell the shell target the drag left,
    /// and run the transfer through the queue instead. The source is told nothing happened (DROPEFFECT_NONE), so it
    /// never deletes anything itself; the queue does the move.
    /// </summary>
    private bool TryQueueDrop(DragEventArgs e)
    {
        if (_drop is not { Folder: { } folder } session || _internalDragPaths is not { Count: > 0 } paths || _dropRightButton) return false;
        var effect = session.LastEffect;
        bool move;
        if ((effect & DropEffect.Move) != 0) move = true;
        else if ((effect & DropEffect.Copy) != 0) move = false;
        else return false; // links and refusals stay with the shell

        try { session.Leave(); } catch { }
        if (_helperEntered) { _dropHelper?.DragLeave(); _helperEntered = false; }
        session.Dispose();
        _drop = null;
        ReleaseDragData();
        e.Effects = DragDropEffects.None;
        var files = paths.ToList();
        // After the drag's modal loop has returned, so the source window isn't left waiting on the queue.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
            await ViewModel.TransferAsync(files, folder.Pidl, folder.Path, folder.Name, move));
        return true;
    }

    private void EndDropSession()
    {
        if (_drop is null) return;
        try { _drop.Leave(); } catch { }
        _drop.Dispose();
        _drop = null;
        SetDropHighlight(null);
    }

    private void ReleaseDragData()
    {
        SetDropHighlight(null);
        _tabSwitchTimer?.Stop();
        if (_dropData == IntPtr.Zero) return;
        Marshal.Release(_dropData);
        _dropData = IntPtr.Zero;
    }

    private void SetDropHighlight(ItemViewModel? item)
    {
        if (ReferenceEquals(_dropHighlight, item)) return;
        if (_dropHighlight is not null) _dropHighlight.IsDropTarget = false;
        _dropHighlight = item;
        if (item is not null) item.IsDropTarget = true;
    }

    private const DragDropKeyStates MouseButtons =
        DragDropKeyStates.LeftMouseButton | DragDropKeyStates.RightMouseButton | DragDropKeyStates.MiddleMouseButton;

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        // WPF also raises it between child elements (button down, pointer still here), and the real leave reports a point inside the window too
        if ((e.KeyStates & MouseButtons) != 0 && IsPointerOverWindow()) return;
        EndDragHere(true);
    }

    private bool IsPointerOverWindow()
    {
        var under = User32.WindowFromPoint(CursorPoint());
        return under != IntPtr.Zero && User32.GetAncestor(under, User32.GA_ROOT) == _hwnd;
    }

    private void EndDragHere(bool clearDescription)
    {
        EndDropSession();
        if (_helperEntered)
        {
            try { _dropHelper?.DragLeave(); } catch { }
            _helperEntered = false;
        }
        if (clearDescription) ShellDragDrop.ClearDropDescription(_dropData);
        ReleaseDragData();
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        // Anywhere that isn't a drop target (toolbars, status bar…) refuses the drop.
        if (e.Handled) return;
        DragOverTarget(e, null, () => null);
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Handled) DropOnTarget(e);
    }

    private static bool IsDropTargetItem(ItemViewModel item) =>
        item.IsFolder || item.Entry.IsBrowsableFile ||
        item.Entry.Extension.ToLowerInvariant() is ".exe" or ".lnk" or ".bat" or ".cmd";

    private void OnFileListDragOver(object sender, DragEventArgs e)
    {
        // The pane under the pointer, which isn't necessarily the active one (dragging from one pane to the other).
        if (PaneOf(sender).Tab is not { Location: { } location } tab) return;
        var row = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is ItemViewModel item && IsDropTargetItem(item))
        {
            // Hovering the items being dragged: nowhere to drop.
            if (_dragging?.Contains(item) == true) { DragOverTarget(e, null, () => null); return; }
            DragOverTarget(e, item, () => item.Entry.GetPidl() is { } p ? (p, false) : null,
                p => item is { IsFolder: true, Entry.Path: { } path } ? new DropFolder(p, path, item.Name) : null);
            return;
        }
        DragOverTarget(e, tab, () => (location.Pidl, true), FolderOf(location));
    }

    private static Func<Pidl, DropFolder?> FolderOf(ShellLocation location) =>
        p => location is { IsFileSystemFolder: true, FileSystemPath: { } path } ? new DropFolder(p, path, location.DisplayName) : null;

    private void OnFileListDrop(object sender, DragEventArgs e) => DropOnTarget(e);

    private void OnSidebarDragOver(object sender, DragEventArgs e)
    {
        var row = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is not SidebarFolder folder) { DragOverTarget(e, null, () => null); return; }
        DragOverTarget(e, folder, () => ShellNative.TryParse(folder.Target) is { } p ? (p, false) : null,
            p => folder.FileSystemPath is { } path && folder is not SidebarDrive { HasSpaceInfo: false } ? new DropFolder(p, path, folder.Name) : null);
    }

    private void OnSidebarDrop(object sender, DragEventArgs e) => DropOnTarget(e);

    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is not TabViewModel { Location: { } location } tab) { DragOverTarget(e, null, () => null); return; }
        DragOverTarget(e, tab, () => (location.Pidl, true), FolderOf(location));

        // Hovering a tab for a moment switches to it, so you can drop into a specific folder there (as in Explorer).
        if (ReferenceEquals(tab, ViewModel.SelectedTab) || ReferenceEquals(tab, _tabSwitchCandidate)) return;
        _tabSwitchCandidate = tab;
        _tabSwitchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Input, (_, _) =>
        {
            _tabSwitchTimer!.Stop();
            if (_tabSwitchCandidate is { } t && ViewModel.Tabs.Contains(t)) ViewModel.SelectedTab = t;
            _tabSwitchCandidate = null;
        }, Dispatcher);
        _tabSwitchTimer.Stop();
        _tabSwitchTimer.Start();
    }

    private void OnTabStripDrop(object sender, DragEventArgs e) => DropOnTarget(e);

    // ================= Marquee (rubber-band) selection =================

    private Point? _marqueeStart;            // in content coordinates
    private HashSet<ItemViewModel> _marqueeBase = [];
    private DispatcherTimer? _marqueeScroll;
    private Point _marqueeLast;

    private void BeginMarquee(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(FileList);
        _marqueeStart = ToContent(p);
        _marqueeLast = p;
        _marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Selection.ToHashSet() : [];
        if (_marqueeBase.Count == 0) FileList.UnselectAll();
        FileList.Focus();
        FileList.CaptureMouse();
        _marqueeScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => AutoScrollMarquee(), Dispatcher);
        _marqueeScroll.Start();
        e.Handled = true;
    }

    private void EndMarquee()
    {
        if (_marqueeStart is null) return;
        _marqueeStart = null;
        _marqueeScroll?.Stop();
        MarqueeRect.Visibility = Visibility.Collapsed;
        FileList.ReleaseMouseCapture();
    }

    private void OnFileListPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = null;
        if (_marqueeStart is not null) { EndMarquee(); e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Left && _deferredSelect is { } item)
        {
            // Plain click on one of several selected items: select just it, but only now that it wasn't a drag.
            _deferredSelect = null;
            FileList.SelectedItem = item;
        }
    }

    private void AutoScrollMarquee()
    {
        if (_marqueeStart is null || ListScrollViewer is not { } sv) return;
        var p = Mouse.GetPosition(FileList);
        var horizontal = _active.AppliedMode == ViewMode.List;
        if (horizontal)
        {
            if (p.X < 0) sv.LineLeft();
            else if (p.X > FileList.ActualWidth) sv.LineRight();
            else return;
        }
        else
        {
            if (p.Y < 24) sv.LineUp();
            else if (p.Y > FileList.ActualHeight) sv.LineDown();
            else return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UpdateMarquee(Mouse.GetPosition(FileList)));
    }

    private void UpdateMarquee(Point current)
    {
        if (_marqueeStart is not { } start) return;
        _marqueeLast = current;
        var end = ToContent(current);
        var content = new Rect(start, end);

        // Draw the band in viewport coordinates, clipped to the list.
        var a = FromContent(start);
        var band = new Rect(a, current);
        band.Intersect(new Rect(0, 0, FileList.ActualWidth, FileList.ActualHeight));
        if (band.IsEmpty) MarqueeRect.Visibility = Visibility.Collapsed;
        else
        {
            Canvas.SetLeft(MarqueeRect, band.X);
            Canvas.SetTop(MarqueeRect, band.Y);
            MarqueeRect.Width = band.Width;
            MarqueeRect.Height = band.Height;
            MarqueeRect.Visibility = Visibility.Visible;
        }

        var wanted = new HashSet<ItemViewModel>(_marqueeBase);
        foreach (var vm in ItemsIn(content)) wanted.Add(vm);

        _syncingSelection = true;
        try
        {
            foreach (var s in Selection.Where(s => !wanted.Contains(s))) FileList.SelectedItems.Remove(s);
            var selected = FileList.SelectedItems.Cast<ItemViewModel>().ToHashSet();
            foreach (var w in wanted.Where(w => !selected.Contains(w))) FileList.SelectedItems.Add(w);
        }
        finally { _syncingSelection = false; }
        if (Tab is { } tab) tab.SelectedItems = Selection;
    }

    // Content coordinates don't move when the list scrolls, so a band can grow while auto-scrolling.
    private Point ToContent(Point viewport)
    {
        // Grouped lists scroll by pixel, so the scroll offsets are content coordinates.
        if (FileList.IsGrouping && ListScrollViewer is { } sv) return new Point(viewport.X + sv.HorizontalOffset, viewport.Y + sv.VerticalOffset);
        if (FindDescendant<VirtualizingWrapPanel>(FileList) is { } wrap)
        {
            var o = wrap.TranslatePoint(new Point(0, 0), FileList);
            return new Point(viewport.X - o.X + wrap.Offset.X, viewport.Y - o.Y + wrap.Offset.Y);
        }
        var (origin, rowHeight, first) = StackGeometry();
        return new Point(viewport.X, viewport.Y - origin.Y + first * rowHeight);
    }

    private Point FromContent(Point content)
    {
        if (FileList.IsGrouping && ListScrollViewer is { } sv) return new Point(content.X - sv.HorizontalOffset, content.Y - sv.VerticalOffset);
        if (FindDescendant<VirtualizingWrapPanel>(FileList) is { } wrap)
        {
            var o = wrap.TranslatePoint(new Point(0, 0), FileList);
            return new Point(content.X + o.X - wrap.Offset.X, content.Y + o.Y - wrap.Offset.Y);
        }
        var (origin, rowHeight, first) = StackGeometry();
        return new Point(content.X, content.Y + origin.Y - first * rowHeight);
    }

    private IEnumerable<ItemViewModel> ItemsIn(Rect content)
    {
        if (FileList.IsGrouping)
        {
            // Groups have no fixed geometry; test the realized rows (the list keeps a page or two around the viewport).
            var viewport = new Rect(FromContent(content.TopLeft), FromContent(content.BottomRight));
            foreach (var row in Descendants<ListViewItem>(FileList))
            {
                if (row.DataContext is not ItemViewModel vm || !row.IsVisible) continue;
                var bounds = new Rect(row.TranslatePoint(new Point(0, 0), FileList), row.RenderSize);
                if (bounds.IntersectsWith(viewport)) yield return vm;
            }
            yield break;
        }
        IEnumerable<int> indexes;
        if (FindDescendant<VirtualizingWrapPanel>(FileList) is { } wrap) indexes = wrap.IndexesIn(content);
        else
        {
            var (_, rowHeight, _) = StackGeometry();
            if (rowHeight <= 0) yield break;
            var top = Math.Max(0, (int)(content.Top / rowHeight));
            var bottom = Math.Min(FileList.Items.Count - 1, (int)(content.Bottom / rowHeight));
            indexes = bottom < top ? [] : Enumerable.Range(top, bottom - top + 1);
        }
        foreach (var i in indexes)
            if (i < FileList.Items.Count && FileList.Items[i] is ItemViewModel vm) yield return vm;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var n = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T match) yield return match;
                else stack.Push(child);
            }
        }
    }

    /// <summary>Rows in details/content view are uniform and scroll by item, so positions follow from the first visible index.</summary>
    private (Point Origin, double RowHeight, double FirstIndex) StackGeometry()
    {
        var panel = FindDescendant<VirtualizingStackPanel>(FileList);
        if (panel is null || panel.Children.Count == 0) return (new Point(), 30, 0);
        var origin = panel.TranslatePoint(new Point(0, 0), FileList);
        var row = (FrameworkElement)panel.Children[0];
        var height = row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
        return (origin, height > 0 ? height : 30, ListScrollViewer?.VerticalOffset ?? 0);
    }
}
