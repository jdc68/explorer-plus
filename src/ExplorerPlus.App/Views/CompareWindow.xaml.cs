using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Core.Shell;
using Microsoft.Win32;

namespace ExplorerPlus.App.Views;

public sealed class CompareRow(CompareItem item)
{
    public CompareItem Item => item;

    public string Name => item.IsFolder ? item.Relative + "\\" : item.Relative;

    public string State => item.State switch
    {
        CompareState.LeftOnly => "Only on the left",
        CompareState.RightOnly => "Only on the right",
        CompareState.LeftNewer => "Newer on the left",
        CompareState.RightNewer => "Newer on the right",
        CompareState.Different => "Different",
        _ => "Same",
    };

    public Brush? StateBrush => Application.Current.TryFindResource(item.State switch
    {
        CompareState.Same => "TextFillColorTertiaryBrush",
        CompareState.Different => "SystemFillColorCautionBrush",
        _ => "AccentTextFillColorPrimaryBrush",
    }) as Brush;

    public string LeftSize => Size(item.LeftSize, item.IsFolder);
    public string RightSize => Size(item.RightSize, item.IsFolder);
    public string LeftModified => item.LeftModified?.ToString("g") ?? "";
    public string RightModified => item.RightModified?.ToString("g") ?? "";

    private static string Size(long size, bool folder) => size < 0 || folder ? "" : ItemViewModel.FormatSize(size);
}

public partial class CompareWindow : Window
{
    private List<CompareItem> _items = [];
    private string _left = "", _right = "";
    private CancellationTokenSource? _cts;

    public SyncPlan? Plan { get; private set; }

    private CompareWindow(string? left, string? right)
    {
        InitializeComponent();
        LeftBox.Text = left ?? "";
        RightBox.Text = right ?? "";
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) =>
        {
            if (LeftBox.Text.Length > 0 && RightBox.Text.Length > 0) await CompareAsync();
            else (LeftBox.Text.Length == 0 ? LeftBox : RightBox).Focus();
        };
        Closed += (_, _) => _cts?.Cancel();
    }

    // what to sync, or null when the window was closed without syncing
    public static SyncPlan? Ask(Window owner, string? left, string? right)
    {
        var window = new CompareWindow(left, right) { Owner = owner };
        return window.ShowDialog() == true ? window.Plan : null;
    }

    private async Task CompareAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        string left = Normalize(LeftBox.Text), right = Normalize(RightBox.Text);
        var byContent = ContentBox.IsChecked == true;
        Summary.Text = "Comparing…";
        SyncButton.IsEnabled = false;
        try
        {
            var items = await Task.Run(() =>
            {
                foreach (var folder in new[] { left, right })
                    if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder.Length == 0 ? "Pick two folders to compare." : $"Can't find the folder {folder}.");
                if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) throw new IOException("Pick two different folders.");
                return FolderCompare.Compare(left, right, byContent, cts.Token);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            (_items, _left, _right) = (items, left, right);
            Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (_items, _left, _right) = ([], "", "");
            Results.ItemsSource = null;
            Summary.Text = ex.Message;
        }
    }

    private SyncDirection Direction => (SyncDirection)Math.Max(0, DirectionBox.SelectedIndex);

    private SyncPlan CurrentPlan() => FolderCompare.Plan(_left, _right, _items, Direction, ExtrasBox.IsChecked == true);

    private void Refresh()
    {
        if (!IsLoaded) return;
        ExtrasBox.IsEnabled = Direction != SyncDirection.BothWays;
        if (_left.Length == 0) return;
        var showSame = SameBox.IsChecked == true;
        Results.ItemsSource = _items.Where(i => showSame || i.State != CompareState.Same).Select(i => new CompareRow(i)).ToList();
        var differences = _items.Count(i => i.State != CompareState.Same);
        var plan = CurrentPlan();
        SyncButton.IsEnabled = !plan.IsEmpty;
        if (differences == 0)
        {
            Summary.Text = $"Both folders have the same {_items.Count:N0} files.";
            return;
        }
        var text = $"{differences:N0} difference{(differences == 1 ? "" : "s")}. ";
        text += plan.IsEmpty ? "Nothing to sync this way."
            : $"Sync copies {plan.Copies.Count:N0} item{(plan.Copies.Count == 1 ? "" : "s")}"
              + (plan.Deletes.Count > 0 ? $" and sends {plan.Deletes.Count:N0} replaced or extra item{(plan.Deletes.Count == 1 ? "" : "s")} to the Recycle Bin." : ".");
        var clashes = Direction == SyncDirection.BothWays ? _items.Count(i => i.State == CompareState.Different) : 0;
        if (clashes > 0) text += $" {clashes:N0} with the same date but different content stay as they are.";
        Summary.Text = text;
    }

    private static string Normalize(string text)
    {
        var t = text.Trim().Trim('"');
        if (t.Length == 0) return "";
        try { t = Path.GetFullPath(t); } catch { }
        return t.Length > 3 ? t.TrimEnd('\\') : t;
    }

    private async void OnCompareClick(object sender, RoutedEventArgs e) => await CompareAsync();

    private void OnFilterChanged(object sender, RoutedEventArgs e) => Refresh();

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await CompareAsync();
    }

    private async void OnSwap(object sender, RoutedEventArgs e)
    {
        (LeftBox.Text, RightBox.Text) = (RightBox.Text, LeftBox.Text);
        await CompareAsync();
    }

    private async void OnBrowseLeft(object sender, RoutedEventArgs e)
    {
        if (Browse(LeftBox.Text) is { } folder) { LeftBox.Text = folder; await CompareAsync(); }
    }

    private async void OnBrowseRight(object sender, RoutedEventArgs e)
    {
        if (Browse(RightBox.Text) is { } folder) { RightBox.Text = folder; await CompareAsync(); }
    }

    private string? Browse(string current)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Normalize(current) };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void OnSyncClick(object sender, RoutedEventArgs e)
    {
        var plan = CurrentPlan();
        if (plan.IsEmpty) return;
        Plan = plan;
        DialogResult = true;
    }
}
