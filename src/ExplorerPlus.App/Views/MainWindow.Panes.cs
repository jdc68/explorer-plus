using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ExplorerPlus.App.Services;
using ExplorerPlus.App.ViewModels;

namespace ExplorerPlus.App.Views;

public partial class MainWindow
{
    // ================= Dual pane =================
    //
    // The window always has a left pane (the tab picked in the strip) and, when that tab is split, a right pane (its
    // Partner). Each pane has its own list, details columns and layout state. Keyboard and mouse commands act on the
    // active pane (FileList, Tab); events a tab raises by itself (selection after a load, rename after New) go to the
    // pane that shows it.

    private sealed class FilePane(ListView list, Rectangle marquee, Border host, bool isRight)
    {
        public ListView List { get; } = list;
        public Rectangle Marquee { get; } = marquee;
        public Border Host { get; } = host;
        public bool IsRight { get; } = isRight;
        public required GridView DetailsView { get; init; }
        public Dictionary<string, GridViewColumn> ColumnCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TabViewModel? Tab { get; set; }
        public ViewMode? AppliedMode { get; set; }
        public bool? AppliedGrouped { get; set; }
        public int AnchorIndex { get; set; } = -1;
        public bool BuildingColumns { get; set; }

        public Action<IReadOnlyList<ItemViewModel>> SelectionRequested { get; set; } = _ => { };
        public Action ScrollToTopRequested { get; set; } = () => { };
        public Action<double> ScrollToOffsetRequested { get; set; } = _ => { };
        public Action<ItemViewModel> RenameRequested { get; set; } = _ => { };
        public PropertyChangedEventHandler TabChanged { get; set; } = (_, _) => { };
    }

    private FilePane _left = null!;
    private FilePane _right = null!;
    private FilePane _active = null!;

    /// <summary>The active pane's list.</summary>
    private ListView FileList => _active.List;

    private Rectangle MarqueeRect => _active.Marquee;

    private void InitPanes()
    {
        _left = CreatePane(LeftList, LeftMarquee, LeftPaneHost, false);
        _right = CreatePane(RightList, RightMarquee, RightPaneHost, true);
        _active = _left;
    }

    private FilePane CreatePane(ListView list, Rectangle marquee, Border host, bool right)
    {
        // x:Shared="False": every pane gets its own GridView (a view can only belong to one list).
        var pane = new FilePane(list, marquee, host, right) { DetailsView = (GridView)FindResource("DetailsGridView") };
        pane.SelectionRequested = items => SelectItems(pane, items, scroll: true);
        pane.ScrollToTopRequested = () => ScrollToTop(pane);
        pane.ScrollToOffsetRequested = offset => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollViewerOf(pane.List)?.ScrollToVerticalOffset(offset));
        pane.RenameRequested = item => BeginRename(pane, item);
        pane.TabChanged = (s, e) => OnPaneTabPropertyChanged(pane, s, e);
        list.View = pane.DetailsView;
        InitColumns(pane);
        return pane;
    }

    private FilePane PaneOf(object? element) =>
        ReferenceEquals(element, RightList) || element is Visual v && RightPaneHost.IsAncestorOf(v) ? _right : _left;

    private static ScrollViewer? ScrollViewerOf(ListView list) => FindDescendant<ScrollViewer>(list);

    /// <summary>Puts a tab into a pane: remembers where the old one was scrolled and what it had selected, then shows the new one.</summary>
    private void ShowTabInPane(FilePane pane, TabViewModel? tab)
    {
        if (ReferenceEquals(pane.Tab, tab)) return;
        if (pane.Tab is { } old)
        {
            old.SelectionRequested -= pane.SelectionRequested;
            old.ScrollToTopRequested -= pane.ScrollToTopRequested;
            old.ScrollToOffsetRequested -= pane.ScrollToOffsetRequested;
            old.ReadScrollOffset = null;
            old.RenameRequested -= pane.RenameRequested;
            old.PropertyChanged -= pane.TabChanged;
            old.SavedScrollOffset = ScrollViewerOf(pane.List)?.VerticalOffset ?? 0;
            old.SavedSelection = pane.List.SelectedItems.Cast<ItemViewModel>().ToList();
        }
        if (ReferenceEquals(pane, _active)) EndMarquee();
        pane.Tab = tab;
        _syncingSelection = true;
        try
        {
            pane.Host.DataContext = tab;
            pane.List.ItemsSource = tab?.Items;
        }
        finally { _syncingSelection = false; }
        if (tab is null) return;

        tab.SelectionRequested += pane.SelectionRequested;
        tab.ScrollToTopRequested += pane.ScrollToTopRequested;
        tab.ScrollToOffsetRequested += pane.ScrollToOffsetRequested;
        tab.ReadScrollOffset = () => ScrollViewerOf(pane.List)?.VerticalOffset ?? 0;
        tab.RenameRequested += pane.RenameRequested;
        tab.PropertyChanged += pane.TabChanged;
        RebuildColumns(pane, tab);
        ApplyViewMode(pane, tab.ViewMode);

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (pane.Tab != tab) return;
            var sv = ScrollViewerOf(pane.List);
            if (tab.SavedSelection.Count > 0)
            {
                SelectItems(pane, tab.SavedSelection, scroll: false);
                sv?.ScrollToVerticalOffset(tab.SavedScrollOffset);
            }
            else if (tab.SelectedItems.Count > 0)
            {
                // A fresh tab opened with /select may already have its selection; don't scroll it back to the top.
                SelectItems(pane, tab.SelectedItems, scroll: true);
            }
            else sv?.ScrollToVerticalOffset(tab.SavedScrollOffset);
        });
    }

    private void OnPaneTabPropertyChanged(FilePane pane, object? sender, PropertyChangedEventArgs e)
    {
        if (sender != pane.Tab) return;
        var tab = pane.Tab!;
        switch (e.PropertyName)
        {
            case nameof(TabViewModel.ViewMode) or nameof(TabViewModel.GroupBy):
                ApplyViewMode(pane, tab.ViewMode);
                break;
            case nameof(TabViewModel.SortColumn) or nameof(TabViewModel.SortAscending) or nameof(TabViewModel.SortKey):
                UpdateSortGlyphs(pane, tab);
                break;
            case nameof(TabViewModel.Columns):
                // May come from a header drag, while the column collection is still raising its own events.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (pane.Tab == tab) RebuildColumns(pane, tab); });
                break;
        }
    }

    /// <summary>Shows or hides the right pane to match the current tab.</summary>
    private void ApplyDualLayout()
    {
        var dual = ViewModel.IsDualPane;
        var wasDual = RightPaneHost.Visibility == Visibility.Visible;
        if (dual && !wasDual) PanesGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star); // start at half and half
        DualSplitterColumn.Width = new GridLength(dual ? 5 : 0);
        RightPaneColumn.MinWidth = dual ? 220 : 0;
        RightPaneColumn.Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DualSplitter.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        RightPaneHost.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        DualPaneButton.IsChecked = dual;
        UpdateActivePane();
    }

    /// <summary>Follows the view model's active pane: commands go there, and an accent line marks it.</summary>
    private void UpdateActivePane()
    {
        var pane = ViewModel.IsRightActive ? _right : _left;
        if (!ReferenceEquals(pane, _active))
        {
            EndMarquee();
            _active = pane;
        }
        var dual = ViewModel.IsDualPane;
        foreach (var p in new[] { _left, _right })
        {
            p.Host.BorderThickness = new Thickness(0, dual ? 2 : 0, 0, 0);
            if (dual && ReferenceEquals(p, _active)) p.Host.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
            else p.Host.BorderBrush = Brushes.Transparent;
        }
        if (_active.AppliedMode is { } mode)
        {
            ViewDetailsButton.IsChecked = mode == ViewMode.Details;
            ViewIconsButton.IsChecked = mode == ViewMode.LargeIcons;
        }
    }

    private void OnPanePreviewMouseDown(object sender, MouseButtonEventArgs e) => ViewModel.ActivatePane(ReferenceEquals(sender, RightPaneHost));

    private void OnPaneGotFocus(object sender, KeyboardFocusChangedEventArgs e) => ViewModel.ActivatePane(ReferenceEquals(sender, RightPaneHost));

    /// <summary>Tab in the file list: move to the other pane, as in two-pane file managers.</summary>
    private void SwitchPane()
    {
        if (!ViewModel.IsDualPane) return;
        ViewModel.ActivatePane(!_active.IsRight);
        FocusList();
    }

    private async void OnDualPaneClick(object sender, RoutedEventArgs e) => await ToggleDualPaneAsync();

    private async Task ToggleDualPaneAsync()
    {
        await ViewModel.ToggleDualPaneAsync();
        DualPaneButton.IsChecked = ViewModel.IsDualPane;
        FocusList();
    }

    // ================= Transfer queue =================

    private void UpdateTransfers()
    {
        TransfersButton.Visibility = TransferQueue.Jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TransfersText.Text = TransferQueue.Summary;
    }

    private void OnTransfersClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TransfersButton, Placement = PlacementMode.Top };
        foreach (var job in TransferQueue.Jobs.ToList())
        {
            var item = new MenuItem { Header = $"{job.Title} {job.Detail}", InputGestureText = job.IsQueued ? "Queued" : "Running" };
            if (job.IsQueued)
            {
                item.Items.Add(MenuEntry("Start now", null, () => TransferQueue.RunNow(job), ""));
                item.Items.Add(MenuEntry("Cancel", null, () => TransferQueue.Cancel(job), ""));
            }
            else
            {
                item.Items.Add(new MenuItem { Header = "Pause or cancel it in Windows' progress window", IsEnabled = false });
            }
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No copies or moves are running", IsEnabled = false });
        menu.IsOpen = true;
    }
}
