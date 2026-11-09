using System.IO;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Services;

/// <summary>The taskbar jump list: New window, the sidebar pins and recently opened folders.</summary>
public static class JumpLists
{
    private const int RecentLimit = 10;
    private static DispatcherTimer? _timer;

    public static string AppId => "ExplorerPlus" + (AppPaths.Profile is { } p ? "." + p : "");

    /// <summary>Remembers a folder the user opened, for the Recent category.</summary>
    public static void NoteVisited(ShellLocation? location)
    {
        if (location is not { IsFileSystemFolder: true, IsSearch: false, FileSystemPath: { } path }) return;
        var recent = AppSettings.Current.RecentFolders;
        if (recent.Count > 0 && string.Equals(recent[0], path, StringComparison.OrdinalIgnoreCase)) return;
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);
        if (recent.Count > RecentLimit * 2) recent.RemoveRange(RecentLimit * 2, recent.Count - RecentLimit * 2);
        Schedule();
    }

    /// <summary>Rebuilds the list a moment later, so a burst of navigations writes it once.</summary>
    public static void Schedule()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _timer!.Stop();
            Update();
        }, Application.Current.Dispatcher);
        _timer.Stop();
        _timer.Start();
    }

    public static async void Update()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var settings = AppSettings.Current;
        var shell32 = Path.Combine(Environment.SystemDirectory, "shell32.dll");
        var pinnedCandidates = settings.PinnedFolders.ToList();
        var recentCandidates = settings.RecentFolders.ToList();
        // Pins can be on network shares; checking them must not hold up the UI thread.
        var (pinned, recent) = await Task.Run(() =>
        {
            var p = pinnedCandidates.Where(Directory.Exists).ToList();
            var r = recentCandidates.Where(x => !p.Contains(x, StringComparer.OrdinalIgnoreCase) && Directory.Exists(x)).Take(RecentLimit).ToList();
            return (p, r);
        });
        var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
        list.JumpItems.Add(new JumpTask
        {
            Title = "New window",
            Description = "Open a new Explorer+ window",
            ApplicationPath = exe,
            Arguments = "/n",
            IconResourcePath = exe,
        });

        JumpTask Folder(string path, string category) => new()
        {
            Title = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path,
            Description = path,
            ApplicationPath = exe,
            Arguments = Quote(path),
            IconResourcePath = shell32,
            IconResourceIndex = Path.GetPathRoot(path) == path ? 8 : 3, // drive / folder
            CustomCategory = category,
        };

        foreach (var p in pinned) list.JumpItems.Add(Folder(p, "Pinned"));
        foreach (var p in recent) list.JumpItems.Add(Folder(p, "Recent"));

        try
        {
            JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
        catch (Exception) { /* the jump list is a convenience */ }
    }

    /// <summary>Clears the Recent category (Settings ▸ Clear).</summary>
    public static void ClearRecent()
    {
        AppSettings.Current.RecentFolders.Clear();
        AppSettings.Current.Save();
        Update();
    }

    // A trailing backslash would escape the closing quote ("C:\" → C:").
    private static string Quote(string path) => "\"" + (path.EndsWith('\\') ? path + "\\" : path) + "\"";
}
