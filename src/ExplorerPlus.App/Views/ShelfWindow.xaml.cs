using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ExplorerPlus.App.Infrastructure;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace ExplorerPlus.App.Views;

public sealed class ShelfItem(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;
    public Pidl? Pidl { get; set; }

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
}

public partial class ShelfWindow : Window
{
    private const double Gap = 8;

    private static ShelfWindow? _instance;

    /// <summary>The Explorer+ window used last: where "Move here" and "Copy here" put things, and what the shelf sits beside.</summary>
    public static MainWindow? LastMain { get; set; }

    private readonly ObservableCollection<ShelfItem> _items = [];
    private bool _shownByDrag;
    private bool _draggingOut;
    private Point? _press;
    private ShelfItem? _pressItem;
    private bool _pressAll;
    private IntPtr _dropData;
    private IDropTargetHelper? _helper;
    private bool _helperEntered;
    private bool _hooked;

    private ShelfWindow()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateCount();
        foreach (var path in AppSettings.Current.Shelf) AddItem(path);
        UpdateCount();
        _ = DropMissingAsync(TimeSpan.Zero);
        SourceInitialized += (_, _) => ApplyBackdrop();
        Loaded += (_, _) => ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
        var dark = FindResource("TextFillColorPrimary") is Color text && text.R > 128 ? 1 : 0;
        var backdrop = Dwmapi.DWMSBT_TRANSIENTWINDOW;
        var corner = Dwmapi.DWMWCP_ROUND;
        Dwmapi.DwmSetWindowAttribute(hwnd, Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        Dwmapi.DwmSetWindowAttribute(hwnd, Dwmapi.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        Dwmapi.DwmSetWindowAttribute(hwnd, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        if (!_hooked && HwndSource.FromHwnd(hwnd) is { } source) { source.AddHook(KeepActiveLook); _hooked = true; }
        User32.DefWindowProc(hwnd, User32.WM_NCACTIVATE, 1, IntPtr.Zero);
    }

    // the system drops the acrylic on inactive windows, and the shelf is hardly ever active
    private static IntPtr KeepActiveLook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != User32.WM_NCACTIVATE) return IntPtr.Zero;
        handled = true;
        return User32.DefWindowProc(hwnd, msg, 1, lParam);
    }

    private static ShelfWindow Instance => _instance ??= new ShelfWindow();

    public static bool HasItems => _instance is { _items.Count: > 0 } || AppSettings.Current.Shelf.Count > 0;

    // ---------- Showing ----------

    /// <summary>A drag of files started in an Explorer+ window: show the shelf beside it, to drop them on.</summary>
    public static void ShowForDrag(MainWindow near)
    {
        var shelf = Instance;
        if (shelf.IsVisible) return;
        shelf._shownByDrag = true;
        shelf.ShowNear(near);
    }

    /// <summary>The drag is over: a shelf that came up for it and got nothing goes away again.</summary>
    public static void DragEnded()
    {
        if (_instance is not { } shelf) return;
        if (shelf._shownByDrag && shelf._items.Count == 0) shelf.Hide();
        shelf._shownByDrag = false;
    }

    public static void ShowShelf(MainWindow near)
    {
        var shelf = Instance;
        shelf._shownByDrag = false;
        if (!shelf.IsVisible) shelf.ShowNear(near);
    }

    public static void Add(IEnumerable<string> paths, MainWindow near)
    {
        var shelf = Instance;
        foreach (var path in paths) shelf.AddItem(path);
        shelf.Save();
        ShowShelf(near);
    }

    private void ShowNear(MainWindow near)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var owner = new WindowInteropHelper(near).Handle;
        if (owner != IntPtr.Zero && User32.GetWindowRect(owner, out var r))
        {
            var monitor = User32.MonitorFromPoint(new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 }, User32.MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            User32.GetMonitorInfoW(monitor, ref info);
            var scale = User32.GetDpiForMonitor(monitor, User32.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
            var work = info.rcWork;
            var (w, h, gap) = ((int)(Width * scale), (int)(Height * scale), (int)(Gap * scale));
            // beside the window's right edge, or just inside it when there's no room (maximized)
            var x = r.Right + gap + w <= work.Right ? r.Right + gap : Math.Max(work.Left, r.Right - w - 3 * gap);
            var y = Math.Clamp(r.Top + (r.Bottom - r.Top - h) / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
            User32.SetWindowPos(hwnd, User32.HWND_TOPMOST, x, y, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
        Show();
    }

    // ---------- Items ----------

    private void AddItem(string path)
    {
        path = path.Length > 3 ? path.TrimEnd('\\') : path;
        if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        var item = new ShelfItem(path);
        _items.Add(item);
        _ = ResolveAsync(item);
    }

    private static async Task ResolveAsync(ShelfItem item)
    {
        try
        {
            var (pidl, icon) = await StaWorkerPool.General.Run(() => (ShellNative.TryParse(item.Path), ShellInfoCache.GetIconIndexForPath(item.Path)));
            item.Pidl = pidl;
            item.Icon = IconCache.Get(icon, 32);
        }
        catch { }
    }

    private void Remove(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items.ToList()) _items.Remove(item);
        Save();
        if (_items.Count == 0) Hide();
    }

    private void Clear()
    {
        _items.Clear();
        Save();
        Hide();
    }

    private void Save()
    {
        AppSettings.Current.Shelf = _items.Select(i => i.Path).ToList();
        AppSettings.Current.Save();
    }

    private void UpdateCount()
    {
        CountText.Text = _items.Count switch { 0 => "Shelf", 1 => "1 item", var n => $"{n:N0} items" };
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AllHandle.Cursor = _items.Count == 0 ? null : Cursors.Hand;
    }

    // items moved or deleted elsewhere leave the shelf
    private async Task DropMissingAsync(TimeSpan delay, IReadOnlyList<ShelfItem>? only = null)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        var check = (only ?? _items).ToList();
        var gone = await Task.Run(() => check.Where(i => !File.Exists(i.Path) && !Directory.Exists(i.Path)).ToList());
        if (gone.Count > 0) Remove(gone);
    }

    // ---------- Dragging out ----------

    private void OnAllHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_items.Count == 0) return;
        _press = e.GetPosition(this);
        _pressAll = true;
        e.Handled = true;
    }

    private void OnAllHandleMouseMove(object sender, MouseEventArgs e) => MaybeDrag(e);

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressAll = false;
        _pressItem = (ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) as ListBoxItem)?.Content as ShelfItem;
        if (_pressItem is null) return;
        _press = e.GetPosition(this);
        // keep a multi-selection for the drag; a plain click picks the one item on release
        if (List.SelectedItems.Contains(_pressItem) && List.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None) e.Handled = true;
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_press is not null && _pressItem is not null && List.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
            List.SelectedItem = _pressItem;
        _press = null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e) => MaybeDrag(e);

    private void MaybeDrag(MouseEventArgs e)
    {
        if (_press is not { } start) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _press = null; return; }
        var d = e.GetPosition(this) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _press = null;
        List<ShelfItem> items = _pressAll ? [.. _items]
            : _pressItem is not null && !List.SelectedItems.Contains(_pressItem) ? [_pressItem]
            : List.SelectedItems.Cast<ShelfItem>().ToList();
        DragOut(items);
    }

    private void DragOut(List<ShelfItem> items)
    {
        if (items.Count == 0) return;
        if (items.Any(i => i.Pidl is null))
        {
            _ = DropMissingAsync(TimeSpan.Zero);
            return;
        }
        Mouse.Capture(null);
        _draggingOut = true;
        uint effect;
        bool queued;
        try { (effect, queued) = MainWindow.DragFromShelf(items.Select(i => i.Pidl!).ToList(), items.Select(i => i.Path).ToList(), new WindowInteropHelper(this).Handle); }
        catch { return; }
        finally { _draggingOut = false; }
        // dropped somewhere: they leave the shelf, as on macOS; a move the target did itself shows up as missing files
        if (effect != DropEffect.None || queued) Remove(items);
        else _ = DropMissingAsync(TimeSpan.FromSeconds(2), items);
    }

    // ---------- Dropping in ----------

    private DragDropEffects EffectFor(DragEventArgs e)
    {
        if (_draggingOut || !e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
        // the shelf only remembers where things are, so the source never moves or deletes anything
        return e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
            : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = EffectFor(e);
        e.Handled = true;
        var pt = CursorPoint();
        try
        {
            if (_dropData == IntPtr.Zero) _dropData = Marshal.GetComInterfaceForObject(e.Data, typeof(ComDataObject));
            _helper ??= (IDropTargetHelper)Activator.CreateInstance(Type.GetTypeFromCLSID(OleDragDrop.CLSID_DragDropHelper)!)!;
            if (!_helperEntered)
            {
                _helper.DragEnter(new WindowInteropHelper(this).Handle, _dropData, ref pt, (uint)e.Effects);
                _helperEntered = true;
            }
            else _helper.DragOver(ref pt, (uint)e.Effects);
        }
        catch { }
        if (e.Effects == DragDropEffects.None) Card.BorderBrush = Brushes.Transparent;
        else Card.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (_helperEntered) try { _helper?.DragLeave(); } catch { }
        EndDrop();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Effects = EffectFor(e);
        e.Handled = true;
        var pt = CursorPoint();
        if (_helperEntered) try { _helper?.Drop(_dropData, ref pt, (uint)e.Effects); } catch { }
        EndDrop();
        if (e.Effects == DragDropEffects.None || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths) AddItem(path);
        _shownByDrag = false;
        Save();
    }

    private void EndDrop()
    {
        _helperEntered = false;
        Card.BorderBrush = Brushes.Transparent;
        if (_dropData == IntPtr.Zero) return;
        Marshal.Release(_dropData);
        _dropData = IntPtr.Zero;
    }

    private static POINT CursorPoint()
    {
        User32.GetCursorPos(out var p);
        return p;
    }

    // ---------- Commands ----------

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Clear();

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton, Placement = PlacementMode.Bottom };
        var target = Target();
        var where = target is null ? "" : $" ({target.Value.Location.DisplayName})";
        menu.Items.Add(Item($"Move here{where}", target is not null && _items.Count > 0, () => _ = PutHereAsync(move: true)));
        menu.Items.Add(Item($"Copy here{where}", target is not null && _items.Count > 0, () => _ = PutHereAsync(move: false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Clear the shelf", _items.Count > 0, Clear));
        menu.Items.Add(Item("Hide", true, Hide));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string text, bool enabled, Action run)
    {
        var item = new MenuItem { Header = text, IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }

    private static (MainWindow Window, ShellLocation Location)? Target()
    {
        var main = LastMain is { IsLoaded: true } ? LastMain : App.MainWindows.LastOrDefault(w => w.IsLoaded);
        return main?.ViewModel.SelectedTab?.Location is { IsSearch: false } location ? (main, location) : null;
    }

    private async Task PutHereAsync(bool move)
    {
        if (Target() is not { } target) return;
        var items = _items.ToList();
        var location = target.Location;
        var result = await target.Window.ViewModel.TransferAsync(items.Select(i => i.Path).ToList(), location.Pidl, location.FileSystemPath, location.DisplayName, move);
        if (result == FileOpResult.Succeeded) Remove(items);
        else await DropMissingAsync(TimeSpan.Zero, items);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is ShelfItem item) _ = ShowInFolderAsync(item);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && List.SelectedItems.Count > 0)
        {
            Remove(List.SelectedItems.Cast<ShelfItem>());
            e.Handled = true;
        }
    }

    private void OnListMenu(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        var selected = List.SelectedItems.Cast<ShelfItem>().ToList();
        if (selected.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = List, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("Show in folder", selected.Count == 1, () => _ = ShowInFolderAsync(selected[0])));
        menu.Items.Add(Item("Remove from the shelf", true, () => Remove(selected)));
        menu.IsOpen = true;
    }

    private static async Task ShowInFolderAsync(ShelfItem item)
    {
        if (Path.GetDirectoryName(item.Path) is not { } parent) return;
        var main = LastMain is { IsLoaded: true } ? LastMain : App.MainWindows.LastOrDefault(w => w.IsLoaded);
        if (main is null)
        {
            var window = new MainWindow([parent], item.Path);
            window.Show();
            return;
        }
        main.BringToFront();
        await main.ViewModel.OpenTabAsync(parent, selectPath: item.Path);
    }
}
