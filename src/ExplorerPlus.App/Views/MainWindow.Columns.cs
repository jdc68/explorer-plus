using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using ExplorerPlus.App.ViewModels;

namespace ExplorerPlus.App.Views;

public partial class MainWindow
{
    // Cell sizes for icon layouts when grouped (each group wraps its own items); must match the panel templates.
    private static readonly Dictionary<ViewMode, Size> GroupedCells = new()
    {
        [ViewMode.ExtraLargeIcons] = new(276, 312),
        [ViewMode.LargeIcons] = new(124, 156),
        [ViewMode.MediumIcons] = new(92, 106),
        [ViewMode.SmallIcons] = new(240, 30),
        [ViewMode.List] = new(260, 30),
        [ViewMode.Tiles] = new(300, 80),
    };

    private void InitColumns(FilePane pane)
    {
        foreach (var col in pane.DetailsView.Columns)
        {
            if (col.Header is not GridViewColumnHeader { Tag: string id }) continue;
            pane.ColumnCache[id] = col;
            ((INotifyPropertyChanged)col).PropertyChanged += (s, e) => OnColumnPropertyChanged(pane, s, e);
        }
        pane.DetailsView.Columns.CollectionChanged += (s, e) => OnColumnsCollectionChanged(pane, e);
    }

    private GridViewColumn ColumnFor(FilePane pane, string id)
    {
        if (pane.ColumnCache.TryGetValue(id, out var cached)) return cached;
        var def = ColumnCatalog.Get(id)!;
        var header = new GridViewColumnHeader
        {
            Tag = id, Content = def.Header, Padding = new Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        if (id == ColumnCatalog.SpaceUsed)
        {
            var bar = new GridViewColumn { Header = header, Width = def.Width, CellTemplate = (DataTemplate)FindResource("SpaceUsedCell") };
            ((INotifyPropertyChanged)bar).PropertyChanged += (s, e) => OnColumnPropertyChanged(pane, s, e);
            return pane.ColumnCache[id] = bar;
        }
        var text = new FrameworkElementFactory(typeof(TextBlock));
        var path = id == ColumnCatalog.Created ? nameof(ItemViewModel.CreatedText) : $"[{id}]";
        text.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        if (def.RightAlign)
        {
            text.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
            text.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        }
        var col = new GridViewColumn { Header = header, Width = def.Width, CellTemplate = new DataTemplate { VisualTree = text } };
        ((INotifyPropertyChanged)col).PropertyChanged += (s, e) => OnColumnPropertyChanged(pane, s, e);
        pane.ColumnCache[id] = col;
        return col;
    }

    private static string? IdOf(GridViewColumn col) => (col.Header as GridViewColumnHeader)?.Tag as string;

    private static IEnumerable<string> ShownColumnIds(FilePane pane) => pane.DetailsView.Columns.Select(IdOf).OfType<string>();

    private void RebuildColumns(FilePane pane, TabViewModel tab)
    {
        pane.BuildingColumns = true;
        try
        {
            var cols = pane.DetailsView.Columns;
            if (!ShownColumnIds(pane).SequenceEqual(tab.Columns, StringComparer.OrdinalIgnoreCase))
            {
                cols.Clear();
                foreach (var id in tab.Columns) cols.Add(ColumnFor(pane, id));
            }
            foreach (var col in cols)
                if (IdOf(col) is { } id) col.Width = tab.ColumnWidth(id);
        }
        finally { pane.BuildingColumns = false; }
        UpdateSortGlyphs(pane, tab);
    }

    private static void OnColumnPropertyChanged(FilePane pane, object? sender, PropertyChangedEventArgs e)
    {
        if (pane.BuildingColumns || e.PropertyName != nameof(GridViewColumn.Width) || pane.Tab is not { } tab) return;
        if (sender is not GridViewColumn col || IdOf(col) is not { } id || double.IsNaN(col.Width)) return;
        if (!pane.DetailsView.Columns.Contains(col)) return;
        tab.SetColumnWidth(id, col.Width);
    }

    private void OnColumnsCollectionChanged(FilePane pane, NotifyCollectionChangedEventArgs e)
    {
        // The user dragged a header to a new place.
        if (pane.BuildingColumns || e.Action != NotifyCollectionChangedAction.Move || pane.Tab is not { } tab) return;
        var order = ShownColumnIds(pane).ToList();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (pane.Tab == tab) tab.SetColumns(order); });
    }

    private void SizeColumnToFit(GridViewColumn col)
    {
        col.Width = col.ActualWidth;
        col.Width = double.NaN; // measure to the realized cells
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Tab is { } tab && IdOf(col) is { } id && col.ActualWidth > 0)
            {
                col.Width = col.ActualWidth;
                tab.SetColumnWidth(id, col.ActualWidth);
            }
        });
    }

    /// <summary>Right-click on the column headers: pick columns, as in Explorer.</summary>
    private void ShowColumnMenu(GridViewColumnHeader header)
    {
        if (Tab is not { } tab) return;
        var menu = new ContextMenu { PlacementTarget = header, Placement = PlacementMode.MousePoint };
        var shown = tab.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (header.Column is { } clicked)
            menu.Items.Add(MenuEntry("Size column to fit", null, () => SizeColumnToFit(clicked), null));
        menu.Items.Add(MenuEntry("Size all columns to fit", null, () => { foreach (var c in _active.DetailsView.Columns) SizeColumnToFit(c); }, null));
        menu.Items.Add(new Separator());

        MenuItem Toggle(ColumnDef def)
        {
            var on = shown.Contains(def.Id);
            var item = MenuCheck(def.Header, on, () =>
            {
                var next = tab.Columns.ToList();
                if (on) next.RemoveAll(c => string.Equals(c, def.Id, StringComparison.OrdinalIgnoreCase));
                else next.Add(def.Id);
                tab.SetColumns(next);
            });
            if (def.Id == ColumnCatalog.Name) item.IsEnabled = false;
            return item;
        }

        foreach (var def in ColumnCatalog.All.Where(d => d.Category == "Common")) menu.Items.Add(Toggle(def));
        menu.Items.Add(new Separator());
        foreach (var category in ColumnCatalog.All.Select(d => d.Category).Where(c => c != "Common").Distinct())
        {
            var sub = new MenuItem { Header = category };
            foreach (var def in ColumnCatalog.All.Where(d => d.Category == category)) sub.Items.Add(Toggle(def));
            menu.Items.Add(sub);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(GroupByMenu(tab));
        menu.Items.Add(MenuEntry("Reset columns", null, tab.ResetColumns, null));
        menu.IsOpen = true;
    }

    /// <summary>Columns offered for sorting and grouping: the basic four plus whatever else is shown.</summary>
    private static List<(string Id, string Header)> SortChoices(TabViewModel tab)
    {
        var ids = new List<string> { ColumnCatalog.Name, ColumnCatalog.Modified, ColumnCatalog.Type, ColumnCatalog.Size };
        ids.AddRange(tab.Columns.Where(c => !ids.Contains(c, StringComparer.OrdinalIgnoreCase)));
        return ids.Select(id => (id, ColumnCatalog.Get(id)?.Header ?? id)).ToList();
    }

    private static MenuItem GroupByMenu(TabViewModel tab)
    {
        var group = new MenuItem { Header = "Group by" };
        foreach (var (id, header) in SortChoices(tab))
            group.Items.Add(MenuCheck(header, tab.GroupBy == id, () => tab.SetGroupBy(id)));
        if (SectionGroupLabel(tab) is { } sections)
            group.Items.Add(MenuCheck(sections, tab.GroupBy == ColumnCatalog.Section, () => tab.SetGroupBy(ColumnCatalog.Section)));
        group.Items.Add(new Separator());
        group.Items.Add(MenuCheck("(None)", tab.GroupBy is null, () => tab.SetGroupBy(null)));
        return group;
    }

    // Home's sections and This PC's categories aren't a column, so they're offered on their own
    private static string? SectionGroupLabel(TabViewModel tab) => tab.Kind switch
    {
        LocationKind.Home => "Section",
        LocationKind.ThisPC => "Category",
        _ => null,
    };

    /// <summary>Turns grouping on the list on or off to match the tab, with a group panel suited to the layout.</summary>
    private void ApplyGrouping(FilePane pane, ViewMode mode)
    {
        var list = pane.List;
        var grouped = pane.Tab?.GroupBy is not null;
        var groups = list.Items.GroupDescriptions;
        groups.Clear();
        list.GroupStyle.Clear();
        ApplyScrollUnit(pane);
        if (!grouped) return;

        // When grouping, GroupStyle.Panel stacks the groups and ItemsPanel lays out the items inside each group.
        list.GroupStyle.Add(new GroupStyle { ContainerStyle = (Style)FindResource("GroupContainerStyle"), Panel = GroupsPanel });
        groups.Add(new PropertyGroupDescription(nameof(ItemViewModel.GroupKey)));
    }

    private static ItemsPanelTemplate? _groupsPanel;
    private static readonly Dictionary<ViewMode, ItemsPanelTemplate> GroupedItemPanels = [];

    /// <summary>Stacks the groups (virtualized, so big grouped folders stay fast).</summary>
    private static ItemsPanelTemplate GroupsPanel => _groupsPanel ??= new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));

    /// <summary>Items inside one group of an icon layout: a plain wrap panel with the layout's cell size.</summary>
    private static ItemsPanelTemplate? GroupedItemsPanel(ViewMode mode)
    {
        if (!GroupedCells.TryGetValue(mode, out var cell)) return null;
        if (GroupedItemPanels.TryGetValue(mode, out var cached)) return cached;
        var wrap = new FrameworkElementFactory(typeof(WrapPanel));
        wrap.SetValue(WrapPanel.ItemWidthProperty, cell.Width);
        wrap.SetValue(WrapPanel.ItemHeightProperty, cell.Height);
        wrap.SetValue(MarginProperty, new Thickness(6, 0, 0, 0));
        return GroupedItemPanels[mode] = new ItemsPanelTemplate(wrap);
    }
}
