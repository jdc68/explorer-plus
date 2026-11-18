using System.IO;
using System.Windows;
using System.Windows.Input;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Views;

public enum InUseChoice { None, TryAgain, ForceDelete }

public partial class InUseWindow : Window
{
    private readonly IReadOnlyList<string> _paths;
    private readonly InUseReport? _report;
    private CancellationTokenSource? _cts;

    public InUseChoice Choice { get; private set; }

    private InUseWindow(IReadOnlyList<string> paths, InUseReport? report)
    {
        InitializeComponent();
        _paths = paths;
        _report = report;
        Heading.Text = paths.Count == 1 ? $"What is using \u201c{Path.GetFileName(paths[0].TrimEnd('\\'))}\u201d" : $"What is using {paths.Count} items";
        TryAgainButton.Visibility = report?.Retry is not null ? Visibility.Visible : Visibility.Collapsed;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => _cts?.Cancel();
        Loaded += async (_, _) =>
        {
            if (report is not null) Show(report.Holders);
            else await RefreshAsync();
        };
    }

    // from a failed operation, the choice made here; from the item menu, InUseChoice.None
    public static InUseChoice Ask(Window owner, IReadOnlyList<string> paths, InUseReport? report)
    {
        var window = new InUseWindow(paths, report) { Owner = owner };
        window.ShowDialog();
        return window.Choice;
    }

    private void Show(IReadOnlyList<FileHolder> holders)
    {
        Holders.ItemsSource = holders;
        var closable = holders.Count(h => h.CanClose);
        Summary.Text = holders.Count == 0
            ? "Nothing has it open now."
            : holders.Count == closable
                ? "Close an app to free what it holds. It's asked to close first, so it can save."
                : closable == 0
                    ? "Explorer+ can't close these apps. Close them yourself, or sign in as their user."
                    : "Close an app to free what it holds. Explorer+ can't close the ones marked.";
        ForceDeleteButton.Visibility = _report is { IsDelete: true, Retry: not null } && closable > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Summary.Text = "Looking…";
        RefreshButton.IsEnabled = false;
        try
        {
            var holders = await Task.Run(() => FileLocks.FindHolders(_paths, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) Show(holders);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Summary.Text = ex.Message; }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnCloseAppClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileHolder holder) return;
        IsEnabled = false;
        Summary.Text = $"Asking {holder.Name} to close…";
        CloseOutcome outcome;
        try { outcome = await MainWindow.CloseAppsAsync(this, [holder], null); }
        finally { IsEnabled = true; }
        await RefreshAsync();
        if (outcome == CloseOutcome.Failed) Summary.Text = $"Couldn't end {holder.Name}.";
    }

    private void OnTryAgainClick(object sender, RoutedEventArgs e)
    {
        Choice = InUseChoice.TryAgain;
        Close();
    }

    private void OnForceDeleteClick(object sender, RoutedEventArgs e)
    {
        Choice = InUseChoice.ForceDelete;
        Close();
    }
}
