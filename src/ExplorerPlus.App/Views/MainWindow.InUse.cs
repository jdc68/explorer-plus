using System.Windows;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Views;

public enum CloseOutcome { Closed, Declined, Failed }

public partial class MainWindow
{
    private static readonly TimeSpan InUseNoticeTime = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);

    private async void OnInUseFound(InUseReport report)
    {
        try
        {
            // the preview pane or Quick Look may be what holds it
            if (await ReleaseOwnHoldsAsync(report.Paths))
            {
                report = report with { Holders = await Task.Run(() => FileLocks.FindHolders(report.Paths)) };
                if (report.Holders.Count == 0)
                {
                    ViewModel.ShowNotice($"{report.Subject} was open in the preview, which is closed now.",
                        report.Retry is null ? [] : [new NoticeAction("Try again", () => RetryInUse(report))], InUseNoticeTime);
                    return;
                }
            }
            var actions = new List<NoticeAction> { new("Show who", () => ShowWho(report), "Which apps have it open, with a way to close them") };
            if (report.Retry is not null) actions.Add(new NoticeAction("Try again", () => RetryInUse(report)));
            if (report is { IsDelete: true, Retry: not null } && report.Holders.Any(h => h.CanClose))
                actions.Add(new NoticeAction("Force delete", () => ForceDelete(report),
                    $"Asks {InUseReport.Names(report.Holders.Where(h => h.CanClose).ToList())} to close, then deletes"));
            ViewModel.ShowNotice(report.Message, actions, InUseNoticeTime);
        }
        catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
    }

    private void OnNoticeActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NoticeAction action) action.Run();
    }

    private async Task<bool> ReleaseOwnHoldsAsync(IReadOnlyList<string> paths)
    {
        var released = false;
        if (_quickLook is not null)
        {
            CloseQuickLook();
            released = true;
        }
        if (ViewModel.Pane.HandlerTarget is { } target && paths.Any(p => FileLocks.IsSameOrUnder(target.Path, p)))
        {
            await PreviewHost.Clear();
            released = true;
        }
        return released;
    }

    private async void RetryInUse(InUseReport report)
    {
        if (report.Retry is null) return;
        ViewModel.ClearNotice();
        try
        {
            await ReleaseOwnHoldsAsync(report.Paths);
            await report.Retry();
        }
        catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
    }

    private void ShowWho(InUseReport report)
    {
        switch (InUseWindow.Ask(this, report.Paths, report))
        {
            case InUseChoice.TryAgain: RetryInUse(report); break;
            case InUseChoice.ForceDelete: ForceDelete(report); break;
        }
    }

    private void ShowWhatIsUsing(IReadOnlyList<ItemViewModel> items)
    {
        var paths = items.Select(i => i.Entry.Path).OfType<string>().ToList();
        if (paths.Count == 0) { ViewModel.ShowNotice("Only files and folders can be checked."); return; }
        InUseWindow.Ask(this, paths, null);
    }

    private async void ForceDelete(InUseReport report)
    {
        if (report.Retry is null) return;
        ViewModel.ClearNotice();
        try
        {
            await ReleaseOwnHoldsAsync(report.Paths);
            var holders = (await Task.Run(() => FileLocks.FindHolders(report.Paths))).Where(h => h.CanClose).ToList();
            if (holders.Count > 0)
            {
                ViewModel.ShowNotice($"Asking {InUseReport.Names(holders)} to close…", [], CloseWait + TimeSpan.FromSeconds(5));
                var outcome = await CloseAppsAsync(this, holders, $"delete {report.Subject}");
                ViewModel.ClearNotice();
                if (outcome == CloseOutcome.Declined) return;
                if (outcome == CloseOutcome.Failed)
                {
                    ViewModel.ShowNotice($"Couldn't end {InUseReport.Names(holders)}; {report.Subject} wasn't deleted.");
                    return;
                }
            }
            await report.Retry();
        }
        catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
    }

    // asks each app to close, then offers to end the ones still running
    internal static async Task<CloseOutcome> CloseAppsAsync(Window owner, IReadOnlyList<FileHolder> holders, string? then)
    {
        var closed = await Task.WhenAll(holders.Select(h => FileLocks.AskToCloseAsync(h, CloseWait)));
        var still = holders.Where((_, i) => !closed[i]).ToList();
        if (still.Count == 0) return CloseOutcome.Closed;

        var names = InUseReport.Names(still);
        var them = still.Count == 1 ? "it" : "them";
        var question = $"{names} didn't close.\n\nEnd {them}{(then is null ? "" : " and " + then)}? Anything not saved in {names} will be lost.";
        if (MessageBox.Show(owner, question, "End app", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return CloseOutcome.Declined;
        var ended = await Task.Run(() => still.Select(FileLocks.End).ToList());
        return ended.All(e => e) ? CloseOutcome.Closed : CloseOutcome.Failed;
    }
}
