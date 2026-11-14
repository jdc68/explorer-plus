using System.IO;
using System.Windows;
using System.Windows.Threading;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.App.Services;

public static class FrequentFolders
{
    public const int ShownCount = 5;

    // a folder that doesn't answer in this time (offline share, unplugged drive) is left out for now
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMilliseconds(1500);

    private static IReadOnlyList<string> _available = [];
    private static DispatcherTimer? _timer;
    private static int _refreshVersion;

    /// <summary>The folders to show, most frequent first (empty while the setting is off).</summary>
    public static IReadOnlyList<string> Shown => AppSettings.Current.ShowFrequentFolders ? _available : [];

    public static event Action? Changed;

    public static bool IsShown(string path) => Shown.Any(p => FolderHistory.Same(p, path));

    public static void Start()
    {
        AppSettings.Current.Changed += Refresh;
        if (AppSettings.Current.FolderHistorySeeded) Refresh();
        else _ = SeedAsync();
    }

    public static void NoteVisited(ShellLocation? location)
    {
        if (location is not { IsFileSystemFolder: true, IsSearch: false, IsIndexSearch: false, IsTagView: false, IsDuplicates: false, FileSystemPath: { } path })
            return;
        if (!FolderHistory.Record(AppSettings.Current.FolderHistory, path, DateTime.Now)) return;
        Schedule();
    }

    public static void Remove(string path)
    {
        if (!FolderHistory.Remove(AppSettings.Current.FolderHistory, path)) return;
        AppSettings.Current.Save();
        Refresh();
    }

    public static void Clear()
    {
        AppSettings.Current.FolderHistory.Clear();
        AppSettings.Current.Save();
        Refresh();
    }

    // saves and re-ranks a moment later, so a burst of navigations does it once
    private static void Schedule()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _timer!.Stop();
            AppSettings.Current.Save();
            Refresh();
        }, Application.Current.Dispatcher);
        _timer.Stop();
        _timer.Start();
    }

    public static async void Refresh()
    {
        var version = ++_refreshVersion;
        var quick = QuickAccess.FixedPaths();
        var candidates = FolderHistory.Ranked(AppSettings.Current.FolderHistory, DateTime.Now)
            .Where(p => !quick.Any(q => FolderHistory.Same(q, p)))
            .Take(ShownCount * 3)
            .ToList();
        var available = await Task.Run(() => Available(candidates));
        if (version != _refreshVersion) return;
        var shown = available.Take(ShownCount).ToList();
        if (shown.SequenceEqual(_available, StringComparer.OrdinalIgnoreCase)) return;
        _available = shown;
        Changed?.Invoke();
    }

    // checked in parallel, keeping the ranked order
    private static List<string> Available(List<string> paths)
    {
        var tasks = paths.Select(p => Task.Run(() => Directory.Exists(p))).ToArray();
        try { Task.WaitAll(tasks, CheckTimeout); }
        catch (AggregateException) { }
        return paths.Where((_, i) => tasks[i].IsCompletedSuccessfully && tasks[i].Result).ToList();
    }

    private static async Task SeedAsync()
    {
        List<string> explorer;
        try { explorer = await StaWorkerPool.General.Run(FolderHistory.ReadExplorerFrequent); }
        catch { explorer = []; }
        var settings = AppSettings.Current;
        var now = DateTime.Now;
        foreach (var seed in FolderHistory.Seed(explorer, now))
        {
            var known = settings.FolderHistory.Find(v => FolderHistory.Same(v.Path, seed.Path));
            if (known is null) settings.FolderHistory.Add(seed);
            else known.Count += seed.Count;
        }
        settings.FolderHistorySeeded = true;
        settings.Save();
        Refresh();
    }
}
