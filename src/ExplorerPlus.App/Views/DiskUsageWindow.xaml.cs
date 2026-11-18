using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Views;

public sealed class DiskRow(DiskNode node, long total)
{
    public DiskNode Node { get; } = node;
    public string Name => Node.Name;
    public Brush Brush => FileKinds.BrushOf(Node);
    public string SizeText => Node.Size < 0 ? "…" : SizeFormat.Format(Node.Size);
    private double Share => total > 0 && Node.Size > 0 ? (double)Node.Size / total : 0;
    public string ShareText => Node.Size < 0 ? "" : $"{Share:P1}";
    public double BarWidth => 76 * Share;
}

public partial class DiskUsageWindow : Window
{
    private const double ExpandShare = 0.005;

    private readonly DiskUsage _usage;
    private readonly MainWindow? _owner;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<DiskNode> _requested = [];
    private readonly HashSet<FileKind> _kinds = [];
    private readonly DispatcherTimer _refresh;
    private DiskNode _current;
    private volatile bool _dirty;
    private string? _driveSpace;

    public DiskUsageWindow(string folder, MainWindow? owner)
    {
        InitializeComponent();
        _owner = owner;
        _usage = new DiskUsage(folder);
        _usage.Changed += _ => _dirty = true;
        _current = _usage.Root;
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            Refresh();
        }, Dispatcher);
        Title = $"Disk usage of {folder} - Explorer+";

        var background = TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.Black;
        foreach (DiskChart chart in new DiskChart[] { Treemap, Sunburst })
        {
            chart.Background = background;
            chart.NodeClicked += OnNodeClicked;
            chart.NodeDoubleClicked += Open;
            chart.NodeRightClicked += (node, at) => ShowMenu(node, chart);
        }
        Sunburst.CenterClicked += GoUp;
        BuildLegend();
        ShowChart(AppSettings.Current.DiskUsageChart == nameof(Sunburst));
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _refresh.Stop();
        };
        if (Path.GetPathRoot(folder) is { } root && string.Equals(root.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            _ = ReadDriveSpaceAsync(root);
        ShowFolder(_usage.Root);
    }

    private async Task ReadDriveSpaceAsync(string root)
    {
        try
        {
            var drive = await Task.Run(() => new DriveInfo(root));
            var (free, total) = await Task.Run(() => (drive.TotalFreeSpace, drive.TotalSize));
            _driveSpace = $"{SizeFormat.Format(free)} free of {SizeFormat.Format(total)}";
            UpdateStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private MainWindow? Main => _owner is { IsLoaded: true } ? _owner : App.MainWindows.LastOrDefault(w => w.IsLoaded);

    // ---------- Navigation ----------

    private void ShowFolder(DiskNode folder)
    {
        _current = folder;
        Treemap.Root = Sunburst.Root = folder;
        Treemap.Selected = Sunburst.Selected = null;
        var chain = new List<DiskNode>();
        for (var n = folder; n is not null; n = n.Parent) chain.Insert(0, n);
        Crumbs.ItemsSource = chain;
        UpButton.IsEnabled = folder.Parent is not null;
        Refresh();
    }

    private void GoUp()
    {
        if (_current.Parent is { } parent) ShowFolder(parent);
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => GoUp();

    private void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DiskNode node }) ShowFolder(node);
    }

    private void OnNodeClicked(DiskNode node)
    {
        if (node.IsOther) return;
        if (node.IsFolder)
        {
            ShowFolder(node);
            return;
        }
        // a file drawn inside a subfolder: go to that folder, where it is listed
        if (node.Parent is { } parent && parent != _current) ShowFolder(parent);
        Select(node);
    }

    private void Select(DiskNode node)
    {
        var row = Rows.Items.OfType<DiskRow>().FirstOrDefault(r => r.Node == node);
        Rows.SelectedItem = row;
        if (row is not null) Rows.ScrollIntoView(row);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back && e.OriginalSource is not TextBox)
        {
            GoUp();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // ---------- Charts and list ----------

    private void ShowChart(bool sunburst)
    {
        Treemap.Visibility = sunburst ? Visibility.Collapsed : Visibility.Visible;
        Sunburst.Visibility = sunburst ? Visibility.Visible : Visibility.Collapsed;
        TreemapButton.IsChecked = !sunburst;
        SunburstButton.IsChecked = sunburst;
    }

    private void OnChartClick(object sender, RoutedEventArgs e)
    {
        var sunburst = sender == SunburstButton;
        ShowChart(sunburst);
        AppSettings.Current.DiskUsageChart = sunburst ? nameof(Sunburst) : nameof(Treemap);
        AppSettings.Current.Save();
    }

    private void BuildLegend()
    {
        foreach (var kind in FileKinds.All)
        {
            var toggle = new ToggleButton { Style = (Style)FindResource("RecentToggle"), Content = FileKinds.Label(kind), Tag = kind };
            toggle.Click += (_, _) =>
            {
                if (toggle.IsChecked == true) _kinds.Add(kind); else _kinds.Remove(kind);
                Treemap.Kinds = Sunburst.Kinds = _kinds.ToHashSet();
                Refresh();
            };
            Legend.Children.Add(Swatch(FileKinds.BrushOf(kind), toggle));
        }
        Legend.Children.Add(Swatch(FileKinds.FolderBrush, new TextBlock { Text = "Folders", Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }));
        Legend.Children.Add(Swatch(FileKinds.OtherItemsBrush, new TextBlock { Text = "Small items together", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }));
    }

    private static StackPanel Swatch(Brush brush, UIElement label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
        panel.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        panel.Children.Add(label);
        return panel;
    }

    private void Refresh()
    {
        Treemap.InvalidateVisual();
        Sunburst.InvalidateVisual();
        var children = _current.Children ?? [];
        var total = children.Sum(c => Math.Max(0, c.Size));
        var selected = (Rows.SelectedItem as DiskRow)?.Node;
        var rows = children.Where(c => _kinds.Count == 0 || c.IsFolder || _kinds.Contains(FileKinds.Of(c)))
            .Select(c => new DiskRow(c, total)).ToList();
        Rows.ItemsSource = rows;
        if (selected is not null) Rows.SelectedItem = rows.FirstOrDefault(r => r.Node == selected);
        UpdateStatus();
        LoadAround(_current);
    }

    private void UpdateStatus()
    {
        var parts = new List<string>();
        if (_current.Children is null) parts.Add($"Reading {_current.Path}…");
        else
        {
            var files = _current.Children.Count(c => !c.IsFolder);
            var folders = _current.Children.Count - files;
            parts.Add($"{_current.Path}: {(_current.Size < 0 ? "measuring" : SizeFormat.Format(_current.Size))} in {folders:N0} folder{(folders == 1 ? "" : "s")} and {files:N0} file{(files == 1 ? "" : "s")}");
        }
        if (_current.Parent is null && _driveSpace is not null) parts.Add(_driveSpace);
        if (_usage.Measuring > 0) parts.Add($"measuring {_usage.Measuring:N0} folder{(_usage.Measuring == 1 ? "" : "s")}…");
        else if (_usage.FromIndex) parts.Add("folder totals from the index");
        Status.Text = string.Join("  ·  ", parts);
    }

    // the folder on screen, and the folders big enough to show in the charts' next two levels
    private void LoadAround(DiskNode folder)
    {
        Load(folder);
        var share = Math.Max(1, folder.Size > 0 ? folder.Size : (folder.Children ?? []).Sum(c => Math.Max(0, c.Size))) * ExpandShare;
        foreach (var child in BigFolders(folder, share))
        {
            Load(child);
            foreach (var grandchild in BigFolders(child, share)) Load(grandchild);
        }
    }

    private static IEnumerable<DiskNode> BigFolders(DiskNode folder, double share) =>
        (folder.Children ?? []).Where(c => c.IsFolder && c.Size >= share);

    private void Load(DiskNode folder)
    {
        if (folder.Children is not null || !_requested.Add(folder)) return;
        _ = LoadSafeAsync(folder);
    }

    private async Task LoadSafeAsync(DiskNode folder)
    {
        try { await _usage.LoadAsync(folder, _cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void OnRowSelected(object sender, SelectionChangedEventArgs e) =>
        Treemap.Selected = Sunburst.Selected = (Rows.SelectedItem as DiskRow)?.Node;

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Rows, source) is ListViewItem { Content: DiskRow row })
            OpenOrEnter(row.Node);
    }

    private void OnRowKeyDown(object sender, KeyEventArgs e)
    {
        if (Rows.SelectedItem is not DiskRow row) return;
        if (e.Key == Key.Enter) OpenOrEnter(row.Node);
        else if (e.Key == Key.Delete) _ = DeleteAsync(row.Node);
        else return;
        e.Handled = true;
    }

    private void OnRowMenu(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (Rows.SelectedItem is DiskRow row) ShowMenu(row.Node, Rows);
    }

    private void OpenOrEnter(DiskNode node)
    {
        if (node.IsFolder) ShowFolder(node);
        else Open(node);
    }

    // ---------- Actions ----------

    private void ShowMenu(DiskNode node, UIElement target)
    {
        if (node.IsOther) return;
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item(node.IsFolder ? "Open in Explorer+" : "Open", () => Open(node)));
        menu.Items.Add(Item("Show in folder", () => _ = ShowInFolderAsync(node)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", () => _ = DeleteAsync(node)));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string text, Action run)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => run();
        return item;
    }

    private async void Open(DiskNode node)
    {
        try
        {
            if (node.IsOther) return;
            if (node.IsFolder)
            {
                if (Main is { } main)
                {
                    main.BringToFront();
                    await main.ViewModel.OpenTabAsync(node.Path);
                }
                else App.OpenWindow(node.Path);
            }
            else await ShellLauncher.RunAsync(node.Path, null, Path.GetDirectoryName(node.Path), new WindowInteropHelper(this).Handle);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async Task ShowInFolderAsync(DiskNode node)
    {
        if (Path.GetDirectoryName(node.Path.TrimEnd('\\')) is not { } parent) return;
        try
        {
            if (Main is { } main)
            {
                main.BringToFront();
                await main.ViewModel.OpenTabAsync(parent, selectPath: node.Path);
            }
            else
            {
                var window = new MainWindow([parent], node.Path);
                window.Show();
                window.BringToFront();
            }
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async Task DeleteAsync(DiskNode node)
    {
        if (node.IsOther || node.Parent is null) return;
        if (Main is not { } main)
        {
            Status.Text = "Open an Explorer+ window to delete from here.";
            return;
        }
        try
        {
            await main.ViewModel.DeletePathsAsync([node.Path], permanent: false, confirm: true);
            if (await Task.Run(() => File.Exists(node.Path) || Directory.Exists(node.Path))) return;
            _usage.Remove(node);
            Refresh();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}
