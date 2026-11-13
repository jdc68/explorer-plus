using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using ExplorerPlus.App.Services;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Views;

public partial class MainWindow : Window
{
    private readonly IReadOnlyList<string> _initialTargets;
    private readonly string? _initialSelect;
    private Workspace? _workspace;
    private TabViewModel? _adoptTab;
    private IntPtr _hwnd;
    private ItemViewModel? _renaming;
    private bool _renameCancelled;
    private bool _syncingSelection;
    private WindowState _stateBeforeFullScreen;
    private bool _prewarm;

    public MainViewModel ViewModel { get; }

    public MainWindow(IReadOnlyList<string> targets, string? select)
    {
        InitializeComponent();
        InitPanes();
        _initialTargets = targets;
        _initialSelect = select;
        ViewModel = new MainViewModel(() => _hwnd);
        DataContext = ViewModel;
        InitPane();

        RestorePlacement();
        FolderSizeWatcher.SetActive(true); // paused while Explorer+ had no window
        SidebarColumn.Width = new GridLength(Math.Clamp(AppSettings.Current.SidebarWidth, 140, 600));

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Loaded += (_, _) =>
        {
            TransferQueue.Changed += UpdateTransfers;
            UpdateTransfers();
        };
        Closed += (_, _) => TransferQueue.Changed -= UpdateTransfers;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            ViewModel.Dispose();
            if (!_prewarm) App.OnWindowClosed(this);
        };
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Address.EditFinished += FocusList;
        PreviewKeyDown += OnWindowKeyDown;
        PreviewMouseUp += OnWindowMouseUp;
        TaskbarItemInfo = new TaskbarItemInfo();
        StateChanged += (_, _) => ApplyTitleBarLayout();
        ApplyTitleBarLayout();
    }

    /// <summary>A window for a tab torn off another window, placed where the tab was let go.</summary>
    public MainWindow(Workspace workspace) : this([], null) => _workspace = workspace;

    public MainWindow(TabViewModel tab, Point topLeft) : this([], null)
    {
        _adoptTab = tab;
        WindowState = WindowState.Normal;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = topLeft.X;
        Top = topLeft.Y;
        ApplyTitleBarLayout();
    }

    /// <summary>
    /// Builds a window and lays out its templates without showing it, then drops it: after a background start, the
    /// first real window then opens as fast as later ones.
    /// </summary>
    public static void Prewarm()
    {
        var w = new MainWindow([], null) { _prewarm = true };
        if (w.Content is FrameworkElement root)
        {
            root.Measure(new Size(w.Width, w.Height));
            root.Arrange(new Rect(0, 0, w.Width, w.Height));
        }
        w.Close();
    }

    private TabViewModel? Tab => ViewModel.SelectedTab;

    private List<ItemViewModel> Selection => FileList.SelectedItems.Cast<ItemViewModel>().ToList();

    // ================= Window lifecycle =================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_adoptTab is { } adopted)
        {
            _adoptTab = null;
            ViewModel.AttachTab(adopted, 0);
            FocusList();
            return;
        }
        if (_workspace is { } workspace)
        {
            _workspace = null;
            await ViewModel.OpenWorkspaceAsync(workspace);
        }
        var first = true;
        foreach (var target in _initialTargets)
        {
            await ViewModel.OpenTabAsync(target, activate: first, selectPath: first ? _initialSelect : null);
            first = false;
        }
        if (ViewModel.Tabs.Count == 0) await ViewModel.OpenTabAsync(ShellLocation.Known.ThisPC);
        FocusList();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var p = AppSettings.Current.Window;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        p.Left = bounds.Left;
        p.Top = bounds.Top;
        p.Width = bounds.Width;
        p.Height = bounds.Height;
        p.Maximized = WindowState == WindowState.Maximized || _stateBeforeFullScreen == WindowState.Maximized && WindowStyle == WindowStyle.None;
    }

    private void RestorePlacement()
    {
        var p = AppSettings.Current.Window;
        Width = Math.Max(MinWidth, p.Width);
        Height = Math.Max(MinHeight, p.Height);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // Cascade extra windows a little from the last one, like Explorer.
        if (App.MainWindows.LastOrDefault(w => w != this && w.IsLoaded && w.WindowState == WindowState.Normal) is { } last &&
            screen.Contains(new Point(last.Left + 66, last.Top + 46)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = last.Left + 26;
            Top = last.Top + 26;
            return;
        }
        var offset = App.MainWindows.Count(w => w != this) * 26;
        if (!double.IsNaN(p.Left) && screen.Contains(new Point(p.Left + 40 + offset, p.Top + 20 + offset)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.Left + offset;
            Top = p.Top + offset;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_hwnd != IntPtr.Zero) User32.SetForegroundWindow(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // While a native context menu is open, its owner-drawn submenus ("Send to", "Open with", "New")
        // need these messages forwarded to the shell's IContextMenu2/3.
        if (ShellContextMenu.Current is { } menu && msg is WM.INITMENUPOPUP or WM.DRAWITEM or WM.MEASUREITEM or WM.MENUCHAR)
        {
            if (menu.HandleMenuMessage(msg, wParam, lParam, out var result))
            {
                handled = true;
                return result;
            }
        }
        return IntPtr.Zero;
    }

    // ================= Tabs =================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Progress):
                var progress = ViewModel.Progress;
                TaskbarItemInfo.ProgressState = progress is null ? TaskbarItemProgressState.None : TaskbarItemProgressState.Normal;
                TaskbarItemInfo.ProgressValue = progress ?? 0;
                break;
            case nameof(MainViewModel.CurrentTab):
                ShowTabInPane(_left, ViewModel.CurrentTab);
                break;
            case nameof(MainViewModel.RightTab):
                ShowTabInPane(_right, ViewModel.RightTab);
                ApplyDualLayout();
                break;
            case nameof(MainViewModel.IsRightActive):
                UpdateActivePane();
                break;
            case nameof(MainViewModel.SelectedTab):
                if (_quickLook is { } ql && ql.Tab != Tab) CloseQuickLook();
                UpdateActivePane();
                break;
        }
    }

    /// <summary>Swaps the list's layout: GridView with columns for Details, a wrap or stack panel with templates otherwise.</summary>
    private void ApplyViewMode(FilePane pane, ViewMode mode)
    {
        var list = pane.List;
        var grouped = pane.Tab?.GroupBy is not null;
        if (pane.AppliedMode == mode && pane.AppliedGrouped == grouped) return;
        var keep = list.SelectedItem;
        pane.AppliedMode = mode;
        pane.AppliedGrouped = grouped;
        ApplyGrouping(pane, mode);
        if (mode == ViewMode.Details)
        {
            list.ItemTemplate = null;
            list.ItemContainerStyle = (Style)FindResource("FileRowStyle");
            list.ItemsPanel = (ItemsPanelTemplate)FindResource("DetailsPanel");
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            list.Style = (Style)FindResource("FileListStyle");
            list.View = pane.DetailsView;
        }
        else
        {
            list.View = null;
            list.Style = (Style)FindResource("IconListStyle");
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Standard);
            list.ItemContainerStyle = (Style)FindResource("IconItemStyle");
            list.ItemsPanel = (grouped ? GroupedItemsPanel(mode) : null) ?? (ItemsPanelTemplate)FindResource(mode + "Panel");
            list.ItemTemplate = (DataTemplate)FindResource(mode switch
            {
                ViewMode.SmallIcons or ViewMode.List => "SmallIconTemplate",
                ViewMode.Tiles => "TileTemplate",
                ViewMode.Content => "ContentTemplate",
                _ => "IconTemplate",
            });
            ScrollViewer.SetHorizontalScrollBarVisibility(list, mode == ViewMode.List && !grouped ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        }
        if (ReferenceEquals(pane, _active))
        {
            ViewDetailsButton.IsChecked = mode == ViewMode.Details;
            ViewIconsButton.IsChecked = mode == ViewMode.LargeIcons;
        }
        if (keep is not null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => list.ScrollIntoView(keep));
    }

    private static readonly ViewMode[] ZoomOrder =
        [ViewMode.Details, ViewMode.List, ViewMode.SmallIcons, ViewMode.MediumIcons, ViewMode.LargeIcons, ViewMode.ExtraLargeIcons];

    /// <summary>Ctrl+wheel: step through the layouts from Details up to extra large icons.</summary>
    private void Zoom(int direction)
    {
        if (Tab is not { } tab) return;
        var i = Array.IndexOf(ZoomOrder, tab.ViewMode);
        if (i < 0) i = Array.IndexOf(ZoomOrder, ViewMode.MediumIcons);
        else i = Math.Clamp(i + direction, 0, ZoomOrder.Length - 1);
        tab.SetViewMode(ZoomOrder[i]);
    }

    private void OnFileListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        ViewModel.ActivatePane(PaneOf(sender).IsRight); // zoom the pane under the pointer
        Zoom(e.Delta > 0 ? 1 : -1);
    }

    private void OnViewDetailsClick(object sender, RoutedEventArgs e) => Tab?.SetViewMode(ViewMode.Details);
    private void OnViewIconsClick(object sender, RoutedEventArgs e) => Tab?.SetViewMode(ViewMode.LargeIcons);
    private async void OnNewTabClick(object sender, RoutedEventArgs e) => await ViewModel.OpenTabAsync(AppSettings.Current.StartLocation);

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) CloseTab(tab);
    }

    private void CloseTab(TabViewModel tab)
    {
        if (ViewModel.Tabs.Count <= 1) Close();
        else ViewModel.CloseTab(tab);
    }

    private void OnCloseOtherTabsClick(object sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) ViewModel.CloseOtherTabs(tab);
    }

    private async void OnDuplicateTabClick(object sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) await ViewModel.DuplicateTabAsync(tab);
    }

    private void OnTabNewWindowClick(object sender, RoutedEventArgs e)
    {
        if (TabFrom(sender)?.Location is { } l) App.OpenWindow(l.EditText);
    }

    private async void OnReopenTabClick(object sender, RoutedEventArgs e) => await ViewModel.ReopenClosedTabAsync();

    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: TabViewModel tab })
        {
            e.Handled = true;
            CloseTab(tab);
        }
    }

    private static TabViewModel? TabFrom(object sender) => (sender as FrameworkElement)?.DataContext as TabViewModel;

    // ================= Navigation bar =================

    private async void OnBackClick(object sender, RoutedEventArgs e) { if (Tab is { } t) await t.GoBackAsync(); }

    private async void OnForwardClick(object sender, RoutedEventArgs e) { if (Tab is { } t) await t.GoForwardAsync(); }

    private async void OnUpClick(object sender, RoutedEventArgs e) { if (Tab is { } t) await t.GoUpAsync(); }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (Tab is { } t) await t.RefreshAsync();
        await ViewModel.Sidebar.RefreshDrivesAsync();
    }

    private void OnHistoryButtonRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Tab is not { Location: { } current } tab) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var l in tab.ForwardHistory.Reverse<ShellLocation>().Take(12).Reverse()) menu.Items.Add(HistoryItem(tab, l));
        menu.Items.Add(new MenuItem { Header = current.DisplayName, FontWeight = FontWeights.SemiBold, IsChecked = true });
        foreach (var l in tab.BackHistory.Reverse<ShellLocation>().Take(12)) menu.Items.Add(HistoryItem(tab, l));
        menu.IsOpen = true;
    }

    private static MenuItem HistoryItem(TabViewModel tab, ShellLocation l)
    {
        var item = new MenuItem { Header = l.DisplayName, ToolTip = l.EditText };
        item.Click += async (_, _) => await tab.GoToHistoryAsync(l);
        return item;
    }

    private async void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (Tab is { } t) t.FilterText = "";
            FocusList();
        }
        else if (e.Key == Key.Enter && FilterBox.Text.Trim() is { Length: > 0 } query && Tab is { CanSearch: true })
        {
            // Typing filters what's listed; Enter searches the folder and everything below it.
            e.Handled = true;
            if (await ViewModel.SearchAsync(query)) FocusList();
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            e.Handled = true;
            FocusList();
        }
    }

    // ================= Command bar =================

    private async void OnNewMenuClick(object sender, RoutedEventArgs e)
    {
        var canCreate = Tab?.Location?.IsFileSystemFolder == true;
        IReadOnlyList<NewItemTemplate> templates;
        try { templates = await ShellNewTemplates.GetAsync(); }
        catch { templates = []; }

        var menu = new ContextMenu { PlacementTarget = NewButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuEntry("Folder", "Ctrl+Shift+N", async () => await ViewModel.NewFolderAsync(), ""));
        menu.Items.Add(new Separator());
        // Explorer's New ▸ list. Windows 11's new Notepad drops the .txt entry on some machines; we always offer it.
        var entries = templates.Select(t => (t.TypeName, Icon: IconCache.Get(t.IconIndex), Action: (Action)(async () => await ViewModel.NewFromTemplateAsync(t)))).ToList();
        if (!templates.Any(t => t.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)))
            entries.Add(("Text Document", IconCache.Get(ShellInfoCache.Get(".txt", false).IconIndex), async () => await ViewModel.NewTextFileAsync()));
        foreach (var (name, icon, action) in entries.OrderBy(t => t.TypeName, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = MenuEntry(name, null, action, null);
            if (icon is not null) item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("More items…", null, ShowBackgroundMenuAtButton, null));
        foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = canCreate;
        menu.IsOpen = true;
    }

    private void ShowBackgroundMenuAtButton()
    {
        var p = NewButton.PointToScreen(new Point(0, NewButton.ActualHeight));
        ShowBackgroundMenu((int)p.X, (int)p.Y);
    }

    private async void OnCutClick(object sender, RoutedEventArgs e) => await ViewModel.CopyToClipboardAsync(Selection, cut: true);
    private async void OnCopyClick(object sender, RoutedEventArgs e) => await ViewModel.CopyToClipboardAsync(Selection, cut: false);
    private async void OnPasteClick(object sender, RoutedEventArgs e) => await ViewModel.PasteAsync();
    private void OnRenameClick(object sender, RoutedEventArgs e) => BeginRenameSelected();
    private void OnCopyPathClick(object sender, RoutedEventArgs e) => ViewModel.CopyPaths(Selection);
    private async void OnDeleteClick(object sender, RoutedEventArgs e) => await ViewModel.DeleteAsync(Selection, permanent: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    private async void OnTerminalClick(object sender, RoutedEventArgs e) => await ViewModel.OpenTerminalAsync();
    private void OnPropertiesClick(object sender, RoutedEventArgs e) => ShowProperties();
    private void OnStarClick(object sender, RoutedEventArgs e) => ViewModel.ToggleStar(Selection);
    private void OnEmptyRecycleBinClick(object sender, RoutedEventArgs e) => ViewModel.EmptyRecycleBin();
    private void OnRestoreAllClick(object sender, RoutedEventArgs e) => ViewModel.Restore(FileList.Items.Cast<ItemViewModel>().ToList());

    private void OnRestoreSelectedClick(object sender, RoutedEventArgs e)
    {
        if (Selection is { Count: > 0 } items) ViewModel.Restore(items);
        else ViewModel.ShowNotice("Select the items to restore.");
    }

    private void OnMapDriveClick(object sender, RoutedEventArgs e) => ViewModel.MapNetworkDrive();
    private void OnDisconnectDriveClick(object sender, RoutedEventArgs e) => ViewModel.DisconnectNetworkDrive();

    private void OnSortMenuClick(object sender, RoutedEventArgs e)
    {
        if (Tab is not { } tab) return;
        var menu = new ContextMenu { PlacementTarget = SortButton, Placement = PlacementMode.Bottom };
        foreach (var (id, name) in SortChoices(tab))
            menu.Items.Add(MenuCheck(name, string.Equals(tab.SortKey, id, StringComparison.OrdinalIgnoreCase), () => tab.SortBy(id, tab.SortAscending)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCheck("Ascending", tab.SortAscending, () => tab.SortBy(tab.SortKey, true)));
        menu.Items.Add(MenuCheck("Descending", !tab.SortAscending, () => tab.SortBy(tab.SortKey, false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(GroupByMenu(tab));
        menu.IsOpen = true;
    }

    private void OnViewMenuClick(object sender, RoutedEventArgs e)
    {
        if (Tab is not { } tab) return;
        var menu = new ContextMenu { PlacementTarget = ViewButton, Placement = PlacementMode.Bottom };
        for (var i = 0; i < ViewModes.All.Length; i++)
        {
            var mode = ViewModes.All[i];
            var item = MenuEntry(ViewModes.Label(mode), $"Ctrl+Shift+{i + 1}", () => tab.SetViewMode(mode), ViewModes.Glyph(mode));
            if (tab.ViewMode == mode) item.FontWeight = FontWeights.SemiBold;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = MenuEntry(tab.ViewIsRemembered ? "Reset this folder's view" : $"Automatic view ({tab.Content})", null, tab.ResetView, null);
        reset.IsEnabled = tab.ViewIsRemembered;
        reset.ToolTip = "Folders you haven't customised get a view that fits their content: pictures and videos as large icons, the rest as details.";
        menu.Items.Add(reset);
        menu.Items.Add(new Separator());
        var pane = ViewModel.Pane;
        var preview = MenuCheck("Preview pane", pane.IsPreview, () => pane.Toggle(PaneMode.Preview));
        preview.InputGestureText = "Alt+P";
        menu.Items.Add(preview);
        var details = MenuCheck("Details pane", pane.IsDetails, () => pane.Toggle(PaneMode.Details));
        details.InputGestureText = "Alt+Shift+P";
        menu.Items.Add(details);
        var dual = MenuCheck("Dual pane", ViewModel.IsDualPane, async () => await ToggleDualPaneAsync());
        dual.InputGestureText = "Ctrl+\\";
        menu.Items.Add(dual);
        var show = new MenuItem { Header = "Show" };
        show.Items.Add(MenuCheck("Item check boxes", ViewModel.ShowCheckboxes, () => ViewModel.ShowCheckboxes = !ViewModel.ShowCheckboxes));
        show.Items.Add(MenuCheck("File name extensions", ViewModel.ShowFileExtensions, () => ViewModel.ShowFileExtensions = !ViewModel.ShowFileExtensions));
        show.Items.Add(MenuCheck("Hidden items", ViewModel.ShowHiddenFiles, () => ViewModel.ShowHiddenFiles = !ViewModel.ShowHiddenFiles));
        show.Items.Add(MenuCheck("Folder sizes", ViewModel.ShowFolderSizes, () => ViewModel.ShowFolderSizes = !ViewModel.ShowFolderSizes));
        show.Items.Add(MenuCheck("Protected operating system files", ViewModel.ShowProtectedOsFiles, () =>
        {
            if (!ViewModel.ShowProtectedOsFiles && MessageBox.Show(this,
                    "You have chosen to display protected operating system files. These files are required to start and run Windows. Deleting or editing them can make your computer inoperable.\n\nAre you sure you want to display them?",
                    "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            ViewModel.ShowProtectedOsFiles = !ViewModel.ShowProtectedOsFiles;
        }));
        menu.Items.Add(show);
        menu.IsOpen = true;
    }
    private void OnMoreMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };
        var undo = MenuEntry(UndoService.UndoLabel is { } u ? $"Undo {u.ToLowerInvariant()}" : "Undo", "Ctrl+Z", async () => await ViewModel.UndoAsync(), "");
        undo.IsEnabled = UndoService.UndoLabel is not null;
        menu.Items.Add(undo);
        var redo = MenuEntry(UndoService.RedoLabel is { } r ? $"Redo {r.ToLowerInvariant()}" : "Redo", "Ctrl+Y", async () => await ViewModel.RedoAsync(), "");
        redo.IsEnabled = UndoService.RedoLabel is not null;
        menu.Items.Add(redo);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Select all", "Ctrl+A", () => FileList.SelectAll(), ""));
        menu.Items.Add(MenuEntry("Select none", null, () => FileList.UnselectAll(), ""));
        menu.Items.Add(MenuEntry("Invert selection", null, InvertSelection, ""));
        menu.Items.Add(new Separator());
        var batch = MenuEntry("Rename selected items…", FileList.SelectedItems.Count > 1 ? "F2" : "Ctrl+Shift+F2", () => ShowBatchRename(Selection), "");
        batch.IsEnabled = FileList.SelectedItems.Count > 0;
        menu.Items.Add(batch);
        var look = MenuEntry("Quick Look", "Space", ToggleQuickLook, "");
        look.IsEnabled = FileList.SelectedItems.Count > 0;
        menu.Items.Add(look);
        menu.Items.Add(MenuEntry("Command palette", "Ctrl+K", ShowPalette, ""));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Open terminal as administrator", null, async () => await ViewModel.OpenTerminalAsync(elevated: true), ""));
        menu.Items.Add(MenuEntry("Open in new window", "Ctrl+N", NewWindowHere, ""));
        menu.Items.Add(WorkspacesMenu());
        menu.Items.Add(TagsMenu(Selection));
        menu.Items.Add(MenuEntry("Find duplicate files", null, FindDuplicates, "\uE8C8"));
        menu.Items.Add(MenuEntry("Compare and sync folders…", null, CompareFolders, "\uE8AB"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Map network drive…", null, ViewModel.MapNetworkDrive, "\uE8CE"));
        menu.Items.Add(MenuEntry("Disconnect network drive…", null, ViewModel.DisconnectNetworkDrive, "\uE8CD"));
        menu.Items.Add(MenuEntry("Empty Recycle Bin", null, ViewModel.EmptyRecycleBin, "\uE74D"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Settings", "Ctrl+,", ShowSettings, "\uE713"));
        menu.Items.Add(MenuEntry("Exit", null, App.ExitAll, ""));
        menu.IsOpen = true;
    }

    private static MenuItem MenuEntry(string header, string? gesture, Action action, string? glyph)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        // an empty icon too, or entries without one start further left than the rest
        item.Icon = new TextBlock { Text = glyph ?? "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"] };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem MenuCheck(string header, bool isChecked, Action action)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    private void InvertSelection()
    {
        var selected = FileList.SelectedItems.Cast<object>().ToHashSet();
        var invert = FileList.Items.Cast<object>().Where(i => !selected.Contains(i)).ToList();
        FileList.UnselectAll();
        foreach (var i in invert) FileList.SelectedItems.Add(i);
    }

    private void ShowSettings() => SettingsWindow.ShowFor(this);

    private void NewWindowHere()
    {
        App.OpenWindow(Tab?.Location?.EditText ?? AppSettings.Current.StartLocation);
    }

    // ================= File list =================

    private ScrollViewer? ListScrollViewer => FindDescendant<ScrollViewer>(FileList);

    private void FocusList()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Address.IsEditing || FilterBox.IsKeyboardFocusWithin && FilterBox.Text.Length > 0) return;
            var target = FileList.SelectedItem ?? (FileList.Items.Count > 0 ? FileList.Items[0] : null);
            if (target is not null && FileList.ItemContainerGenerator.ContainerFromItem(target) is ListViewItem c) c.Focus();
            else FileList.Focus();
        });
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || sender is not ListView list || PaneOf(list).Tab is not { } tab) return;
        tab.SelectedItems = list.SelectedItems.Cast<ItemViewModel>().ToList();
    }

    private void ScrollToTop(FilePane pane)
    {
        ResetScroll(pane.List);
        // A focused, recycled row would otherwise drag the viewport to wherever its new item landed.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (pane.List.SelectedItems.Count == 0) ResetScroll(pane.List);
        });
    }

    private static void ResetScroll(ListView list)
    {
        if (ScrollViewerOf(list) is not { } sv) return;
        // Once the old items are cleared the viewer reports 0 and skips its own ScrollToVerticalOffset(0), while
        // the panel still holds the old offset and goes back to it (clamped) when the new items arrive
        if (FindDescendant<VirtualizingPanel>(sv) is IScrollInfo panel)
        {
            panel.SetVerticalOffset(0);
            panel.SetHorizontalOffset(0);
        }
        sv.ScrollToVerticalOffset(0);
        sv.ScrollToHorizontalOffset(0);
    }

    private void SelectItems(IReadOnlyList<ItemViewModel> items, bool scroll) => SelectItems(_active, items, scroll);

    private void SelectItems(FilePane pane, IReadOnlyList<ItemViewModel> items, bool scroll)
    {
        var list = pane.List;
        var present = items.Count > 50 ? list.Items.Cast<ItemViewModel>().ToHashSet() : null;
        _syncingSelection = true;
        try
        {
            list.UnselectAll();
            foreach (var i in items)
                if (present?.Contains(i) ?? list.Items.Contains(i)) list.SelectedItems.Add(i);
        }
        finally { _syncingSelection = false; }
        if (pane.Tab is { } tab) tab.SelectedItems = list.SelectedItems.Cast<ItemViewModel>().ToList();

        if (list.SelectedItem is { } first)
        {
            if (scroll) list.ScrollIntoView(first);
            // Only the active pane takes focus; the other one just shows its selection.
            if (ReferenceEquals(pane, _active) && !Address.IsEditing && !FilterBox.IsKeyboardFocusWithin)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (ReferenceEquals(pane, _active) && list.ItemContainerGenerator.ContainerFromItem(first) is ListViewItem c) c.Focus();
                });
        }
    }

    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _renaming is not null) return;
        e.Handled = true;
        var target = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? OpenTarget.NewTab : OpenTarget.Here;
        await ViewModel.OpenItemsAsync(Selection, target);
    }

    private async void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not ListViewItem { DataContext: ItemViewModel item }) return;
        e.Handled = true;
        if (item.IsFolder || item.Entry.IsBrowsableFile) await ViewModel.OpenItemsAsync([item], OpenTarget.NewTab);
    }

    private void OnFileListMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        _pressPoint = e.GetPosition(FileList);
        _deferredSelect = null;
        if (FindAncestor<ListViewItem>(source) is { } row)
        {
            _pressOnItem = true;
            if (FindAncestor<CheckBox>(source) is not null || FindAncestor<TextBox>(source) is not null) return;
            // Pressing on one of several selected items must keep the selection, or it couldn't be dragged.
            if (row.IsSelected && FileList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1)
            {
                e.Handled = true;
                _deferredSelect = row.DataContext as ItemViewModel;
                row.Focus();
            }
            return;
        }
        _pressOnItem = false;
        if (FindAncestor<ScrollBar>(source) is not null || FindAncestor<GridViewColumnHeader>(source) is not null) return;
        if (FindAncestor<Thumb>(source) is not null) return;
        if (FindAncestor<ToggleButton>(source) is not null) return;
        // Empty space: clear the selection (as in Explorer) and start a rubber-band selection.
        BeginMarquee(e);
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string tag, Role: not GridViewColumnHeaderRole.Padding } || Tab is not { } tab) return;
        tab.SortBy(tag);
        UpdateSortGlyphs(_active, tab);
    }

    private void UpdateSortGlyphs(FilePane pane, TabViewModel tab)
    {
        if (pane.List.View is not GridView grid) return;
        foreach (var col in grid.Columns)
        {
            if (col.Header is not GridViewColumnHeader { Tag: string tag } header) continue;
            var baseName = ColumnCatalog.Get(tag)?.Header ?? tag;
            header.Content = string.Equals(tab.SortKey, tag, StringComparison.OrdinalIgnoreCase) ? $"{baseName}  {(tab.SortAscending ? "˄" : "˅")}" : baseName;
        }
    }

    private async void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || Tab is not { } tab) return;
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Enter when mods == ModifierKeys.None || mods == ModifierKeys.Control:
                e.Handled = true;
                await ViewModel.OpenItemsAsync(Selection, mods == ModifierKeys.Control ? OpenTarget.NewTab : OpenTarget.Here);
                break;
            case Key.Back:
                e.Handled = true;
                await tab.GoBackAsync();
                break;
            case Key.Space when mods == ModifierKeys.None && !IsTypingAhead:
                // Quick Look, as on macOS. Ctrl+Space still toggles the focused item's selection.
                e.Handled = true;
                ToggleQuickLook();
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when (mods & ModifierKeys.Alt) == 0 && FindDescendant<VirtualizingWrapPanel>(FileList) is { } panel:
                e.Handled = true;
                MoveInGrid(panel, e.Key, mods);
                break;
        }
    }

    /// <summary>Arrow keys in icon/list/tile layouts move by cell (rows and columns), with Shift and Ctrl as in Explorer.</summary>
    private void MoveInGrid(VirtualizingWrapPanel panel, Key key, ModifierKeys mods)
    {
        var count = FileList.Items.Count;
        if (count == 0) return;
        var current = Keyboard.FocusedElement is ListViewItem focused ? FileList.ItemContainerGenerator.IndexFromContainer(focused) : FileList.SelectedIndex;
        var per = panel.ItemsPerLine;
        var rows = panel.Orientation == Orientation.Horizontal;
        var delta = key switch
        {
            Key.Left => rows ? -1 : -per,
            Key.Right => rows ? 1 : per,
            Key.Up => rows ? -per : -1,
            _ => rows ? per : 1,
        };
        var target = current < 0 ? 0 : Math.Clamp(current + delta, 0, count - 1);
        var item = FileList.Items[target];
        if (mods.HasFlag(ModifierKeys.Shift))
        {
            if (_active.AnchorIndex < 0 || _active.AnchorIndex >= count) _active.AnchorIndex = Math.Max(0, current);
            if (!mods.HasFlag(ModifierKeys.Control)) FileList.UnselectAll();
            for (var i = Math.Min(_active.AnchorIndex, target); i <= Math.Max(_active.AnchorIndex, target); i++) FileList.SelectedItems.Add(FileList.Items[i]);
        }
        else if (!mods.HasFlag(ModifierKeys.Control))
        {
            FileList.SelectedItem = item;
            _active.AnchorIndex = target;
        }
        FileList.ScrollIntoView(item);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (FileList.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem c) c.Focus();
        });
    }

    // ================= Inline rename =================

    private void BeginRenameSelected()
    {
        if (FileList.SelectedItem is ItemViewModel item) BeginRename(item);
    }

    private void BeginRename(FilePane pane, ItemViewModel item)
    {
        // New folder / New ▸ in a pane renames there; that pane becomes the active one.
        if (!ReferenceEquals(pane, _active)) ViewModel.ActivatePane(pane.IsRight);
        BeginRename(item);
    }

    private void BeginRename(ItemViewModel item)
    {
        if (item.Entry.ShellAttributes != 0 && !item.Entry.ShellAttributes.HasFlag(SFGAO.CANRENAME))
        {
            ViewModel.ShowNotice("This item can't be renamed.");
            return;
        }
        if (item.Entry.Path is null)
        {
            // Virtual items (drives, special folders) rename through their shell verb.
            InvokeVerbOn([item], "rename");
            return;
        }
        if (_renaming is not null) CommitRename(_renaming);
        _renaming = item;
        _renameCancelled = false;
        item.EditName = item.DisplayName;
        item.IsRenaming = true;
        FileList.ScrollIntoView(item);
    }

    private void OnRenameBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox box || box.DataContext is not ItemViewModel item) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            var dot = item.IsFolder ? -1 : box.Text.LastIndexOf('.');
            if (dot > 0) box.Select(0, dot); else box.SelectAll();
        });
    }

    private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is not ItemViewModel item) return;
        if (e.Key == Key.Enter) { e.Handled = true; CommitRename(item); FocusList(); }
        else if (e.Key == Key.Escape) { e.Handled = true; _renameCancelled = true; item.IsRenaming = false; _renaming = null; FocusList(); }
        else if (e.Key == Key.F2 && sender is TextBox box)
        {
            // Explorer cycles: name → whole text → extension.
            e.Handled = true;
            var dot = box.Text.LastIndexOf('.');
            if (box.SelectionLength == box.Text.Length && dot > 0) box.Select(dot + 1, box.Text.Length - dot - 1);
            else if (dot > 0 && box.SelectionStart == 0 && box.SelectionLength == dot) box.SelectAll();
            else if (dot > 0) box.Select(0, dot);
        }
    }

    private void OnRenameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as TextBox)?.DataContext is ItemViewModel item && item.IsRenaming && !_renameCancelled) CommitRename(item);
    }

    private async void CommitRename(ItemViewModel item)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;
        _renaming = null;
        var newName = item.EditName.Trim();
        if (newName.Length == 0 || newName == item.DisplayName) return;

        if (!item.IsFolder)
        {
            var ext = item.Entry.Extension;
            var extensionHidden = item.DisplayName.Length < item.Name.Length;
            if (extensionHidden) newName += ext;
            else if (!string.Equals(Path.GetExtension(newName), ext, StringComparison.OrdinalIgnoreCase) && ext.Length > 0 &&
                     MessageBox.Show(this, "If you change a file name extension, the file might become unusable.\n\nAre you sure you want to change it?",
                         "Rename", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        var renamed0 = await ViewModel.RenameAsync(item, newName);
        // Search results don't get change notifications; reload them so the new name shows.
        if (renamed0 && Tab is { IsSearch: true } searchTab) await searchTab.RefreshAsync();
        if (Tab?.FindByName(newName) is { } renamed) SelectItems([renamed], scroll: true);
    }

    // ================= Native context menus =================

    private static readonly HashSet<string> FolderVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "explore", "opennewwindow", "opennewtab", "opennewprocess", "rename",
    };

    private static readonly HashSet<string> FileVerbs = new(StringComparer.OrdinalIgnoreCase) { "rename" };

    private const int CmdOpenNewTab = ShellContextMenu.FirstCustomId + 1;
    private const int CmdOpenNewWindow = ShellContextMenu.FirstCustomId + 2;
    private const int CmdPin = ShellContextMenu.FirstCustomId + 3;
    private const int CmdUnpin = ShellContextMenu.FirstCustomId + 4;
    private const int CmdRefresh = ShellContextMenu.FirstCustomId + 5;
    private const int CmdTerminal = ShellContextMenu.FirstCustomId + 6;
    private const int CmdStar = ShellContextMenu.FirstCustomId + 7;
    private const int CmdOpenLocation = ShellContextMenu.FirstCustomId + 8;
    private const int CmdRemoveRecent = ShellContextMenu.FirstCustomId + 9;
    private const int CmdExtract = ShellContextMenu.FirstCustomId + 10;
    private const int CmdBatchRename = ShellContextMenu.FirstCustomId + 11;
    private const int CmdCopyToOther = ShellContextMenu.FirstCustomId + 12;
    private const int CmdMoveToOther = ShellContextMenu.FirstCustomId + 13;
    private const int CmdOpenInOther = ShellContextMenu.FirstCustomId + 14;
    private const int CmdNewTag = ShellContextMenu.FirstCustomId + 15;
    private const int CmdCompressBase = ShellContextMenu.FirstCustomId + 20; // + (int)ArchiveFormat
    private const int CmdTagBase = ShellContextMenu.FirstCustomId + 200;
    private const int CmdChoiceLimit = 50;
    private const int CmdSortBase = ShellContextMenu.FirstCustomId + 100;
    private const int CmdGroupBase = ShellContextMenu.FirstCustomId + 150;
    private const int CmdGroupNone = ShellContextMenu.FirstCustomId + 32;
    private const int CmdSortAsc = ShellContextMenu.FirstCustomId + 30;
    private const int CmdSortDesc = ShellContextMenu.FirstCustomId + 31;

    private void OnFileListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (Tab is null) return;
        if (FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is { } header)
        {
            ShowColumnMenu(header);
            return;
        }
        var keyboard = e.CursorLeft < 0 || e.CursorTop < 0;
        var row = keyboard ? null : FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);

        if (row is { DataContext: ItemViewModel clicked } && !row.IsSelected)
        {
            FileList.UnselectAll();
            row.IsSelected = true;
        }
        if (!keyboard && row is null) FileList.UnselectAll();

        var items = Selection;
        Point screen;
        if (keyboard && items.Count > 0 && FileList.ItemContainerGenerator.ContainerFromItem(items[0]) is ListViewItem c)
            screen = c.PointToScreen(new Point(24, c.ActualHeight));
        else if (keyboard)
            screen = FileList.PointToScreen(new Point(24, 40));
        else
        {
            User32.GetCursorPos(out var pt);
            screen = new Point(pt.X, pt.Y);
        }

        if (items.Count > 0) ShowItemsMenu(items, (int)screen.X, (int)screen.Y);
        else ShowBackgroundMenu((int)screen.X, (int)screen.Y);
    }

    private void ShowItemsMenu(IReadOnlyList<ItemViewModel> items, int x, int y)
    {
        var location = Tab?.Location;
        List<Pidl> pidls;
        try { pidls = items.Select(i => i.Entry.GetPidl()).ToList(); }
        catch { return; }

        using var menu = ShellContextMenu.ForItems(pidls, _hwnd, location?.FileSystemPath);
        if (menu is null)
        {
            // Home and search results can span folders; the shell only builds menus for one folder at a time.
            ShowCollectionMenu(items);
            return;
        }

        var browsable = items.Where(i => i.IsFolder || i.Entry.IsBrowsableFile && Associations.IsBrowsedByShell(i.Entry.Extension)).ToList();
        var custom = new List<CustomMenuItem>();
        if (browsable.Count > 0)
        {
            custom.Add(new CustomMenuItem { Text = "Open in new tab", Id = CmdOpenNewTab });
            custom.Add(new CustomMenuItem { Text = "Open in new window", Id = CmdOpenNewWindow });
            if (browsable.Count == 1) custom.Add(new CustomMenuItem { Text = "Open in other pane", Id = CmdOpenInOther });
        }
        if (ViewModel.OtherPane?.Location is { IsFileSystemFolder: true } && items.All(i => i.Entry.Path is not null))
        {
            custom.Add(new CustomMenuItem { Text = "Copy to other pane\tAlt+F5", Id = CmdCopyToOther });
            custom.Add(new CustomMenuItem { Text = "Move to other pane\tAlt+F6", Id = CmdMoveToOther });
        }
        if (items.Count == 1 && items[0] is { IsFolder: true, Entry.Path: { } folderPath })
        {
            var pinned = QuickAccess.IsShown(folderPath);
            custom.Add(new CustomMenuItem { Text = pinned ? "Unpin from Quick access" : "Pin to Quick access", Id = pinned ? CmdUnpin : CmdPin });
        }
        AddM3Entries(custom, items);

        var intercept = browsable.Count == items.Count ? FolderVerbs : FileVerbs;
        var result = menu.Show(_hwnd, x, y, custom, intercept, extended: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), hide: MenuFilter);
        _ = HandleItemsMenuResult(result, items);
    }

    private async Task HandleItemsMenuResult(MenuResult result, IReadOnlyList<ItemViewModel> items)
    {
        switch (result.Kind)
        {
            case MenuResultKind.Custom when result.CustomId == CmdOpenNewTab:
                await ViewModel.OpenItemsAsync(items, OpenTarget.NewTab);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdOpenNewWindow:
                await ViewModel.OpenItemsAsync(items, OpenTarget.NewWindow);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdPin && items[0].Entry.Path is { } p:
                ViewModel.Sidebar.Pin(p);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdUnpin && items[0].Entry.Path is { } p:
                ViewModel.Sidebar.Unpin(p);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdStar:
                ViewModel.ToggleStar(items);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdOpenLocation:
                await ViewModel.OpenFileLocationAsync(items[0]);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdRemoveRecent:
                await ViewModel.RemoveFromRecentAsync(items);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdExtract:
                await ViewModel.ExtractAsync(items);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdBatchRename:
                ShowBatchRename(items);
                break;
            case MenuResultKind.Custom when result.CustomId is CmdCopyToOther or CmdMoveToOther:
                await ViewModel.TransferToOtherPaneAsync(items, move: result.CustomId == CmdMoveToOther);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdNewTag:
                NewTag(TaggablePaths(items));
                break;
            case MenuResultKind.Custom when result.CustomId >= CmdTagBase && result.CustomId < CmdTagBase + CmdChoiceLimit:
                if (Tags.Names.ElementAtOrDefault(result.CustomId - CmdTagBase) is { } tag) ToggleTag(TaggablePaths(items), tag);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdOpenInOther:
                await OpenInOtherPaneAsync(items[0]);
                break;
            case MenuResultKind.Custom when result.CustomId >= CmdCompressBase && result.CustomId <= CmdCompressBase + (int)ArchiveFormat.TarGz:
                await ViewModel.CompressAsync(items, (ArchiveFormat)(result.CustomId - CmdCompressBase));
                break;
            case MenuResultKind.Verb:
                switch (result.Verb!.ToLowerInvariant())
                {
                    case "rename": BeginRename(items[0]); break;
                    case "opennewwindow": await ViewModel.OpenItemsAsync(items, OpenTarget.NewWindow); break;
                    case "opennewtab": await ViewModel.OpenItemsAsync(items, OpenTarget.NewTab); break;
                    default: await ViewModel.OpenItemsAsync(items, OpenTarget.Here); break;
                }
                break;
        }
    }

    private async Task OpenInOtherPaneAsync(ItemViewModel item)
    {
        var e = item.Entry;
        var location = e.Path is not null ? await ShellLocation.ResolveAsync(e.Path) : await ShellLocation.FromPidlAsync(await e.GetPidlAsync());
        if (location is { IsFolder: true }) await ViewModel.OpenInOtherPaneAsync(location);
    }

    private void AddM3Entries(List<CustomMenuItem> custom, IReadOnlyList<ItemViewModel> items)
    {
        var paths = items.Select(i => i.Entry.Path).OfType<string>().ToList();
        if (paths.Count == items.Count)
        {
            var allStarred = items.All(i => i.IsStarred);
            custom.Add(new CustomMenuItem { Text = allStarred ? "Remove from Starred\tCtrl+D" : "Add to Starred\tCtrl+D", Id = CmdStar });
        }
        if (Tab?.Kind is LocationKind.Home or LocationKind.Search && items.Count == 1 && paths.Count == 1)
            custom.Add(new CustomMenuItem { Text = "Open file location", Id = CmdOpenLocation });
        if (items.Any(i => i.Entry.RecentLink is not null))
            custom.Add(new CustomMenuItem { Text = "Remove from Recent", Id = CmdRemoveRecent });
        if (paths.Count == items.Count && paths.Count > 0)
        {
            custom.Add(new CustomMenuItem
            {
                Text = "Compress to",
                Children = [.. new[] { ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.TarGz }
                    .Select(f => new CustomMenuItem { Text = Archives.Label(f), Id = CmdCompressBase + (int)f })],
            });
            if (paths.All(Archives.IsExtractable))
                custom.Add(new CustomMenuItem { Text = paths.Count == 1 ? "Extract here" : "Extract each here", Id = CmdExtract });
            if (paths.Count > 1) custom.Add(new CustomMenuItem { Text = $"Rename {paths.Count} items…\tF2", Id = CmdBatchRename });
        }
        if (TaggablePaths(items) is { Count: > 0 } taggable && taggable.Count == items.Count)
        {
            var tags = Tags.Names.Take(CmdChoiceLimit).ToList();
            var children = tags.Select((t, i) => new CustomMenuItem { Text = t, Id = CmdTagBase + i, IsChecked = taggable.All(p => Tags.Has(p, t)) }).ToList();
            if (children.Count > 0) children.Add(CustomMenuItem.Separator);
            children.Add(new CustomMenuItem { Text = "New tag…", Id = CmdNewTag });
            custom.Add(new CustomMenuItem { Text = "Tags", Children = children });
        }
    }

    /// <summary>Our own menu for selections that span several folders (Home, search results).</summary>
    private void ShowCollectionMenu(IReadOnlyList<ItemViewModel> items)
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(MenuEntry("Open", "Enter", async () => await ViewModel.OpenItemsAsync(items), null));
        if (items.Count == 1 && items[0].Entry.Path is not null)
            menu.Items.Add(MenuEntry("Open file location", null, async () => await ViewModel.OpenFileLocationAsync(items[0]), null));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Cut", "Ctrl+X", async () => await ViewModel.CopyToClipboardAsync(items, cut: true), null));
        menu.Items.Add(MenuEntry("Copy", "Ctrl+C", async () => await ViewModel.CopyToClipboardAsync(items, cut: false), null));
        menu.Items.Add(MenuEntry("Copy as path", "Ctrl+Shift+C", () => ViewModel.CopyPaths(items), null));
        menu.Items.Add(new Separator());
        if (items.All(i => i.Entry.Path is not null))
        {
            menu.Items.Add(MenuEntry(items.All(i => i.IsStarred) ? "Remove from Starred" : "Add to Starred", "Ctrl+D", () => ViewModel.ToggleStar(items), null));
            // 7z and tar need every item in one folder; ZIP doesn't.
            menu.Items.Add(MenuEntry("Compress to ZIP file", null, async () => await ViewModel.CompressAsync(items, ArchiveFormat.Zip), null));
            menu.Items.Add(TagsMenu(items));
        }
        if (items.Count == 1 && items[0] is { IsFolder: true, Entry.Path: { } folderPath })
        {
            var pinned = QuickAccess.IsShown(folderPath);
            menu.Items.Add(MenuEntry(pinned ? "Unpin from Quick access" : "Pin to Quick access", null,
                () => { if (pinned) QuickAccess.Unpin(folderPath); else QuickAccess.Pin(folderPath); }, null));
        }
        if (items.Any(i => i.Entry.RecentLink is not null))
            menu.Items.Add(MenuEntry("Remove from Recent", null, async () => await ViewModel.RemoveFromRecentAsync(items), null));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Delete", "Delete", async () => await ViewModel.DeleteAsync(items, permanent: false), null));
        menu.IsOpen = true;
    }

    private void ShowBackgroundMenu(int x, int y)
    {
        if (Tab is not { Location: { } location } tab) return;
        using var menu = ShellContextMenu.ForBackground(location.Pidl, _hwnd, location.FileSystemPath);
        if (menu is null) return;

        var choices = SortChoices(tab).Take(CmdChoiceLimit).ToList();
        var sortItems = new List<CustomMenuItem>();
        var groupItems = new List<CustomMenuItem>();
        for (var i = 0; i < choices.Count; i++)
        {
            sortItems.Add(new CustomMenuItem { Text = choices[i].Header, Id = CmdSortBase + i, IsChecked = string.Equals(tab.SortKey, choices[i].Id, StringComparison.OrdinalIgnoreCase) });
            groupItems.Add(new CustomMenuItem { Text = choices[i].Header, Id = CmdGroupBase + i, IsChecked = tab.GroupBy == choices[i].Id });
        }
        sortItems.Add(CustomMenuItem.Separator);
        sortItems.Add(new CustomMenuItem { Text = "Ascending", Id = CmdSortAsc, IsChecked = tab.SortAscending });
        sortItems.Add(new CustomMenuItem { Text = "Descending", Id = CmdSortDesc, IsChecked = !tab.SortAscending });
        groupItems.Add(CustomMenuItem.Separator);
        groupItems.Add(new CustomMenuItem { Text = "(None)", Id = CmdGroupNone, IsChecked = tab.GroupBy is null });

        var custom = new List<CustomMenuItem>
        {
            new() { Text = "Sort by", Children = sortItems },
            new() { Text = "Group by", Children = groupItems },
            new() { Text = "Refresh", Id = CmdRefresh },
        };
        if (location.FileSystemPath is not null) custom.Add(new CustomMenuItem { Text = "Open in Terminal", Id = CmdTerminal });

        var result = menu.Show(_hwnd, x, y, custom, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "paste" },
            extended: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), hide: MenuFilter);

        switch (result.Kind)
        {
            case MenuResultKind.Custom when result.CustomId >= CmdSortBase && result.CustomId < CmdSortBase + choices.Count:
                tab.SortBy(choices[result.CustomId - CmdSortBase].Id, tab.SortAscending);
                break;
            case MenuResultKind.Custom when result.CustomId >= CmdGroupBase && result.CustomId < CmdGroupBase + choices.Count:
                tab.SetGroupBy(choices[result.CustomId - CmdGroupBase].Id);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdGroupNone:
                tab.SetGroupBy(null);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdSortAsc:
                tab.SortBy(tab.SortKey, true);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdSortDesc:
                tab.SortBy(tab.SortKey, false);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdRefresh:
                _ = tab.RefreshAsync();
                break;
            case MenuResultKind.Custom when result.CustomId == CmdTerminal:
                _ = ViewModel.OpenTerminalAsync();
                break;
            case MenuResultKind.Verb when result.Verb == "paste":
                _ = ViewModel.PasteAsync();
                break;
            case MenuResultKind.Invoked when result.Verb is null || result.Verb.StartsWith("new", StringComparison.OrdinalIgnoreCase) || result.Verb.StartsWith('.'):
                // Something from the "New ▸" submenu: put the created item into rename mode, as Explorer does.
                tab.RenameNextCreated();
                break;
        }
    }

    /// <summary>Bloat filter for native menus; Shift+right-click shows everything.</summary>
    // File Explorer's own Quick access pins never show, even with Shift: ours replace them
    private static Func<string?, string, bool> MenuFilter =>
        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? (verb, _) => IsExplorerPinVerb(verb)
            : (verb, text) => IsExplorerPinVerb(verb) || AppSettings.Current.IsMenuItemHidden(verb, text);

    private static bool IsExplorerPinVerb(string? verb) =>
        verb is not null && (verb.Equals("pintohome", StringComparison.OrdinalIgnoreCase) || verb.Equals("unpinfromhome", StringComparison.OrdinalIgnoreCase));

    private void InvokeVerbOn(IReadOnlyList<ItemViewModel> items, string verb)
    {
        try
        {
            var pidls = items.Select(i => i.Entry.GetPidl()).ToList();
            using var menu = ShellContextMenu.ForItems(pidls, _hwnd, Tab?.Location?.FileSystemPath);
            menu?.InvokeVerb(verb, _hwnd);
        }
        catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
    }

    private void ShowProperties()
    {
        var items = Selection;
        if (items.Count > 0) { InvokeVerbOn(items, "properties"); return; }
        if (Tab?.Location is not { } location) return;
        using var menu = ShellContextMenu.ForItems([location.Pidl], _hwnd, null);
        menu?.InvokeVerb("properties", _hwnd);
    }

    // ================= Sidebar =================

    private async void OnSidebarItemClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDirectTreeItem(sender, e) || sender is not TreeViewItem { DataContext: SidebarFolder folder }) return;
        if (FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject) is not null) return; // the expander arrow
        var target = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? OpenTarget.NewTab : OpenTarget.Here;
        await ViewModel.NavigateAsync(folder.Target, target);
    }

    private async void OnSidebarItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !IsDirectTreeItem(sender, e) || sender is not TreeViewItem { DataContext: SidebarFolder folder }) return;
        e.Handled = true;
        await ViewModel.NavigateAsync(folder.Target, OpenTarget.NewTab);
    }

    private async void OnSidebarItemKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TreeViewItem { DataContext: SidebarFolder folder } item || !item.IsSelected) return;
        e.Handled = true;
        await ViewModel.NavigateAsync(folder.Target);
    }

    private void OnSidebarItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDirectTreeItem(sender, e) || sender is not TreeViewItem { DataContext: SidebarFolder folder }) return;
        e.Handled = true;
        if (TagView.IsUrl(folder.Target))
        {
            ShowSidebarTagMenu(folder);
            return;
        }
        var pidl = ShellNative.TryParse(folder.Target);
        if (pidl is null) return;
        using var menu = ShellContextMenu.ForItems([pidl], _hwnd, null);
        if (menu is null) return;

        var custom = new List<CustomMenuItem>
        {
            new() { Text = "Open in new tab", Id = CmdOpenNewTab },
            new() { Text = "Open in new window", Id = CmdOpenNewWindow },
        };
        if (folder.FileSystemPath is { } path && folder is not SidebarDrive)
        {
            var pinned = QuickAccess.IsShown(path);
            custom.Add(new CustomMenuItem { Text = pinned ? "Unpin from Quick access" : "Pin to Quick access", Id = pinned ? CmdUnpin : CmdPin });
        }
        if (folder.FileSystemPath is { } starPath)
            custom.Add(new CustomMenuItem { Text = Stars.IsStarred(starPath) ? "Remove from Starred" : "Add to Starred", Id = CmdStar });
        User32.GetCursorPos(out var pt);
        var result = menu.Show(_hwnd, pt.X, pt.Y, custom, FolderVerbs, extended: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), hide: MenuFilter);
        switch (result.Kind)
        {
            case MenuResultKind.Custom when result.CustomId == CmdOpenNewTab:
                _ = ViewModel.NavigateAsync(folder.Target, OpenTarget.NewTab);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdOpenNewWindow:
                App.OpenWindow(folder.Target);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdPin:
                ViewModel.Sidebar.Pin(folder.FileSystemPath!);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdUnpin:
                ViewModel.Sidebar.Unpin(folder.FileSystemPath!);
                break;
            case MenuResultKind.Custom when result.CustomId == CmdStar:
                Stars.Toggle([folder.FileSystemPath!]);
                break;
            case MenuResultKind.Verb when result.Verb is "opennewwindow":
                App.OpenWindow(folder.Target);
                break;
            case MenuResultKind.Verb when result.Verb is "opennewtab":
                _ = ViewModel.NavigateAsync(folder.Target, OpenTarget.NewTab);
                break;
            case MenuResultKind.Verb when result.Verb is not "rename":
                _ = ViewModel.NavigateAsync(folder.Target);
                break;
        }
    }

    /// <summary>Tree item events bubble through every ancestor item; only react on the item actually clicked.</summary>
    private static bool IsDirectTreeItem(object sender, RoutedEventArgs e) =>
        ReferenceEquals(FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject), sender);

    private void InitPane()
    {
        ApplyPaneLayout();
        ViewModel.Pane.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PaneViewModel.IsOpen): ApplyPaneLayout(); break;
                case nameof(PaneViewModel.HandlerTarget):
                    if (ViewModel.Pane.HandlerTarget is { } target) PreviewHost.Show(target);
                    else PreviewHost.Clear();
                    break;
            }
        };
        PreviewHost.Failed += ViewModel.Pane.HandlerFailed;
    }

    private void ApplyPaneLayout()
    {
        var open = ViewModel.Pane.IsOpen;
        PaneSplitterColumn.Width = new GridLength(open ? 5 : 0);
        PaneColumn.MinWidth = open ? 200 : 0;
        PaneColumn.Width = new GridLength(open ? Math.Clamp(AppSettings.Current.PaneWidth, 200, 900) : 0);
    }

    private void OnPaneResized(object sender, DragCompletedEventArgs e)
    {
        AppSettings.Current.PaneWidth = PaneColumn.ActualWidth;
        AppSettings.Current.Save();
    }

    private void OnSidebarResized(object sender, DragCompletedEventArgs e)
    {
        AppSettings.Current.SidebarWidth = SidebarColumn.ActualWidth;
        AppSettings.Current.Save();
    }

    // ================= Keyboard & mouse =================

    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var inText = Keyboard.FocusedElement is TextBox;
        var tab = Tab;

        // Shortcuts that work everywhere, including inside text boxes.
        switch (key)
        {
            case Key.Left when mods == ModifierKeys.Alt:
                e.Handled = true; if (tab is not null) await tab.GoBackAsync(); return;
            case Key.Right when mods == ModifierKeys.Alt:
                e.Handled = true; if (tab is not null) await tab.GoForwardAsync(); return;
            case Key.Up when mods == ModifierKeys.Alt:
                e.Handled = true; if (tab is not null) await tab.GoUpAsync(); return;
            case Key.L when mods == ModifierKeys.Control:
            case Key.D when mods == ModifierKeys.Alt:
            case Key.F4 when mods == ModifierKeys.None:
                e.Handled = true; Address.BeginEdit(); return;
            case Key.E when mods == ModifierKeys.Control:
            case Key.F when mods == ModifierKeys.Control:
            case Key.F3 when mods == ModifierKeys.None:
                e.Handled = true; FilterBox.Focus(); FilterBox.SelectAll(); return;
            case Key.T when mods == ModifierKeys.Control:
                e.Handled = true; await ViewModel.OpenTabAsync(AppSettings.Current.StartLocation); return;
            case Key.F when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true; await SearchEverywhereAsync(); return;
            case Key.T when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true; await ViewModel.ReopenClosedTabAsync(); return;
            case Key.W when mods == ModifierKeys.Control:
            case Key.F4 when mods == ModifierKeys.Control:
                e.Handled = true; if (ViewModel.CurrentTab is { } current) CloseTab(current); return;
            case Key.Tab when mods == ModifierKeys.Control:
                e.Handled = true; ViewModel.SelectTabRelative(1); return;
            case Key.Tab when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true; ViewModel.SelectTabRelative(-1); return;
            case >= Key.D1 and <= Key.D9 when mods == ModifierKeys.Control:
            {
                e.Handled = true;
                var n = key == Key.D9 ? ViewModel.Tabs.Count - 1 : key - Key.D1;
                if (n < ViewModel.Tabs.Count) ViewModel.SelectedTab = ViewModel.Tabs[n];
                return;
            }
            case Key.N when mods == ModifierKeys.Control:
                e.Handled = true; NewWindowHere(); return;
            case Key.F5 when mods == ModifierKeys.None:
            case Key.R when mods == ModifierKeys.Control:
                e.Handled = true; if (tab is not null) await tab.RefreshAsync(); return;
            case Key.F11 when mods == ModifierKeys.None:
                e.Handled = true; ToggleFullScreen(); return;
            case Key.Oem5 or Key.OemBackslash when mods == ModifierKeys.Control:
                e.Handled = true; await ToggleDualPaneAsync(); return;
            case Key.P when mods == ModifierKeys.Alt:
                e.Handled = true; ViewModel.Pane.Toggle(PaneMode.Preview); return;
            case Key.P when mods == (ModifierKeys.Alt | ModifierKeys.Shift):
                e.Handled = true; ViewModel.Pane.Toggle(PaneMode.Details); return;
            case Key.OemComma when mods == ModifierKeys.Control:
                e.Handled = true; ShowSettings(); return;
            case Key.F6 when mods is ModifierKeys.None or ModifierKeys.Shift:
                e.Handled = true; CycleFocus(mods == ModifierKeys.Shift ? -1 : 1); return;
            case Key.K when mods == ModifierKeys.Control:
            case Key.P when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                // Ctrl+K first: some tools swallow Ctrl+Shift+P system-wide (seen on the dev machine).
                e.Handled = true; ShowPalette(); return;
            case Key.Escape when mods == ModifierKeys.None && _quickLook is not null:
                e.Handled = true; CloseQuickLook(); return;
        }

        if (inText) return;

        // Shortcuts for the file list (not while typing).
        switch (key)
        {
            case Key.N when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true; await ViewModel.NewFolderAsync(); break;
            case Key.Z when mods == ModifierKeys.Control:
                e.Handled = true; await ViewModel.UndoAsync(); break;
            case Key.Y when mods == ModifierKeys.Control:
            case Key.Z when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true; await ViewModel.RedoAsync(); break;
            case Key.C when mods == ModifierKeys.Control:
                e.Handled = true; await ViewModel.CopyToClipboardAsync(Selection, cut: false); break;
            case Key.X when mods == ModifierKeys.Control:
                e.Handled = true; await ViewModel.CopyToClipboardAsync(Selection, cut: true); break;
            case Key.V when mods == ModifierKeys.Control:
                e.Handled = true; await ViewModel.PasteAsync(); break;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true; ViewModel.CopyPaths(Selection); break;
            case Key.Delete when mods is ModifierKeys.None or ModifierKeys.Shift:
                e.Handled = true; await ViewModel.DeleteAsync(Selection, permanent: mods == ModifierKeys.Shift); break;
            case Key.F2 when mods == ModifierKeys.None && FileList.SelectedItems.Count > 1:
            case Key.F2 when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                // Several items: the batch rename window instead of Explorer's "name (1), name (2) …".
                e.Handled = true; ShowBatchRename(Selection); break;
            case Key.F2 when mods == ModifierKeys.None:
                e.Handled = true; BeginRenameSelected(); break;
            case Key.Enter when mods == ModifierKeys.Alt:
                e.Handled = true; ShowProperties(); break;
            case >= Key.D1 and <= Key.D8 when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true; tab?.SetViewMode(ViewModes.All[key - Key.D1]); break;
            case Key.OemPlus or Key.Add when mods == ModifierKeys.Control:
                e.Handled = true; Zoom(1); break;
            case Key.OemMinus or Key.Subtract when mods == ModifierKeys.Control:
                e.Handled = true; Zoom(-1); break;
            case Key.Oem3 when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true; await ViewModel.OpenTerminalAsync(); break;
            case Key.D when mods == ModifierKeys.Control:
                // Explorer uses Ctrl+D for Delete; here it stars (PLAN.md), and Delete stays on the Delete key.
                e.Handled = true; ViewModel.ToggleStar(Selection); break;
            case Key.Tab when mods == ModifierKeys.None && ViewModel.IsDualPane && !Sidebar.IsKeyboardFocusWithin:
                // Two-pane file managers' Tab: also when focus fell off the list (its focused row was deleted).
                e.Handled = true; SwitchPane(); break;
            case Key.F5 when mods == ModifierKeys.Alt:
                e.Handled = true; await ViewModel.TransferToOtherPaneAsync(Selection, move: false); break;
            case Key.F6 when mods == ModifierKeys.Alt:
                e.Handled = true; await ViewModel.TransferToOtherPaneAsync(Selection, move: true); break;
            case Key.Escape when mods == ModifierKeys.None && ViewModel.IsArchiveBusy:
                e.Handled = true; ViewModel.CancelArchive(); break;
        }
    }

    private async void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Tab is not { } tab) return;
        if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; await tab.GoBackAsync(); }
        else if (e.ChangedButton == MouseButton.XButton2) { e.Handled = true; await tab.GoForwardAsync(); }
    }

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
        else
        {
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        ApplyTitleBarLayout();
    }

    /// <summary>F6 / Shift+F6 move between the address bar, the search box, the navigation pane and the file list, as in Explorer.</summary>
    private void CycleFocus(int direction)
    {
        var stops = new Action[]
        {
            () => Address.BeginEdit(),
            () => { FilterBox.Focus(); FilterBox.SelectAll(); },
            FocusSidebar,
            FocusList,
        };
        var current = Address.IsKeyboardFocusWithin ? 0 : FilterBox.IsKeyboardFocusWithin ? 1 : Sidebar.IsKeyboardFocusWithin ? 2 : 3;
        stops[(current + direction + stops.Length) % stops.Length]();
    }

    private void FocusSidebar()
    {
        if (Sidebar.ItemContainerGenerator.ContainerFromItem(Sidebar.SelectedItem) is TreeViewItem { IsVisible: true } selected && selected.Focus()) return;
        // Section headers can't take focus; the first folder under them can.
        var first = FindDescendants<TreeViewItem>(Sidebar).FirstOrDefault(i => i.Focusable && i.IsVisible);
        if (first?.Focus() != true) Sidebar.Focus();
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindDescendants<T>(child)) yield return d;
        }
    }

    // ================= Helpers =================

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }
}
