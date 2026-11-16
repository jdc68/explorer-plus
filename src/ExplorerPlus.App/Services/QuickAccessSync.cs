using System.IO;
using System.Windows;
using System.Windows.Threading;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Services;

public static class QuickAccessSync
{
    private static DispatcherTimer? _timer;
    private static bool _running;
    private static bool _again;

    public static void Start()
    {
        var home = ShellNative.TryParse(WindowsQuickAccess.HomeFolder);
        if (home is not null) ShellChangeWatcher.Instance.Watch(home, false, SHCNE.ALLEVENTS, _ => Schedule());
        AppSettings.Current.Changed += Schedule;
        Schedule();
    }

    private static void Schedule()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _timer!.Stop();
            _ = RunAsync();
        }, Application.Current.Dispatcher);
        _timer.Stop();
        _timer.Start();
    }

    private static async Task RunAsync()
    {
        if (_running)
        {
            _again = true;
            return;
        }
        _running = true;
        try
        {
            do
            {
                _again = false;
                await SyncOnceAsync();
            }
            while (_again);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(AppPaths.DataFolder, "errors.log"), $"{DateTime.Now:O} quick access sync: {ex}{Environment.NewLine}"); }
            catch (IOException) { }
        }
        finally { _running = false; }
    }

    private static async Task SyncOnceAsync()
    {
        var settings = AppSettings.Current;
        if (!settings.SyncQuickAccess) return;

        var pinned = await StaWorkerPool.General.Run(WindowsQuickAccess.ReadPinned);
        if (!settings.QuickAccessImported) ImportPins(pinned);

        var wanted = QuickAccess.FixedPaths();
        var (pin, unpin) = await StaWorkerPool.General.Run(() => WindowsQuickAccess.Plan(wanted, pinned, Directory.Exists));
        if (pin.Count == 0 && unpin.Count == 0) return;
        await StaWorkerPool.General.Run(() => WindowsQuickAccess.Apply(pin, unpin));
    }

    // the first run takes Windows' own pins over, so the first sync doesn't remove them
    private static void ImportPins(IReadOnlyCollection<string> pinned)
    {
        var settings = AppSettings.Current;
        foreach (var (_, path) in QuickAccess.StandardFolders())
        {
            if (string.IsNullOrEmpty(path)) continue;
            settings.HiddenQuickAccess.RemoveAll(h => FolderHistory.Same(h, path));
            if (!pinned.Any(p => FolderHistory.Same(p, path))) settings.HiddenQuickAccess.Add(path);
        }
        foreach (var path in pinned)
        {
            if (QuickAccess.IsStandard(path) || settings.PinnedFolders.Any(p => FolderHistory.Same(p, path))) continue;
            settings.PinnedFolders.Add(path);
        }
        settings.QuickAccessImported = true;
        settings.NotifyChanged();
    }
}
