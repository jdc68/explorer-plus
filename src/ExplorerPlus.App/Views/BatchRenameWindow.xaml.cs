using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Views;

public partial class BatchRenameWindow : Window
{
    private readonly IReadOnlyList<BatchRenameItem> _items;
    private readonly DispatcherTimer _timer;
    private int _version;
    private bool _ready;

    /// <summary>The plan the user confirmed; empty when the window was cancelled.</summary>
    public IReadOnlyList<BatchRenamePlan> Plan { get; private set; } = [];

    private BatchRenameWindow(IReadOnlyList<BatchRenameItem> items)
    {
        InitializeComponent();
        _items = items;
        Heading.Text = $"Rename {items.Count} item{(items.Count == 1 ? "" : "s")}";
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += async (_, _) => { _timer.Stop(); await RefreshAsync(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) =>
        {
            _ready = true;
            PatternBox.Focus();
            PatternBox.SelectAll();
            await RefreshAsync();
        };
    }

    /// <summary>Asks how to rename the items; returns the confirmed plan, or an empty list.</summary>
    public static IReadOnlyList<BatchRenamePlan> Ask(Window owner, IReadOnlyList<ItemViewModel> selection)
    {
        var items = selection.Where(i => i.Entry.Path is not null)
            .Select(i => new BatchRenameItem(i.Entry.Path!, i.IsFolder, i.Entry.Modified)).ToList();
        if (items.Count == 0) return [];
        var window = new BatchRenameWindow(items) { Owner = owner };
        return window.ShowDialog() == true ? window.Plan : [];
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _timer.Stop();
        _timer.Start();
    }

    private BatchRenameOptions Options() => new()
    {
        Find = FindBox.Text,
        Replace = ReplaceBox.Text,
        UseRegex = RegexBox.IsChecked == true,
        MatchCase = MatchCaseBox.IsChecked == true,
        Pattern = PatternBox.Text,
        Case = (NameCase)Math.Max(0, CaseBox.SelectedIndex),
        IncludeExtension = ExtensionBox.IsChecked == true,
        Start = int.TryParse(StartBox.Text, out var start) ? start : 1,
        Step = int.TryParse(StepBox.Text, out var step) ? step : 1,
    };

    /// <summary>Plans off the UI thread: checking whether names are taken touches the disk (maybe a network share).</summary>
    private async Task RefreshAsync()
    {
        var version = ++_version;
        var options = Options();
        var plan = await Task.Run(() => BatchRename.Plan(_items, options));
        if (version != _version) return;
        Plan = plan;
        PreviewList.ItemsSource = plan;
        var changed = plan.Count(p => p.IsChanged && p.Error is null);
        var problems = plan.Count(p => p.Error is not null);
        Summary.Text = (changed == 0 ? "No names change" : $"{changed} of {plan.Count} names change")
                       + (problems == 0 ? "." : $" · {problems} can't be renamed and will be skipped.");
        RenameButton.IsEnabled = changed > 0;
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        await RefreshAsync(); // the plan must match what's typed right now
        if (!Plan.Any(p => p.IsChanged && p.Error is null)) return;
        DialogResult = true;
    }
}
