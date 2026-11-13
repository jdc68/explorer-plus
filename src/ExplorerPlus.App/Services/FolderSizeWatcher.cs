using System.IO;
using System.Windows.Threading;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Services;

public static class FolderSizeWatcher
{
    private static readonly List<FileSystemWatcher> Watchers = [];
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static DispatcherTimer? _timer;

    /// <summary>Folders whose cached size was dropped since the last notification, coalesced, on the UI thread.</summary>
    public static event Action<IReadOnlyCollection<string>>? Changed;

    public static void Start()
    {
        FolderSizeService.Instance.Invalidated += path => { lock (Pending) Pending.Add(path); };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher.CurrentDispatcher);
        _timer.Start();

        // One recursive watcher per local drive (ReadDirectoryChangesW on the thread pool, never the UI thread).
        Task.Run(() =>
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                    var w = new FileSystemWatcher(drive.RootDirectory.FullName)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    var root = drive.RootDirectory.FullName;
                    w.Created += (_, e) => FolderSizeService.Instance.Invalidate(e.FullPath);
                    w.Changed += (_, e) => FolderSizeService.Instance.Invalidate(e.FullPath);
                    w.Deleted += (_, e) => FolderSizeService.Instance.Invalidate(e.FullPath, includeDescendants: true);
                    w.Renamed += (_, e) =>
                    {
                        FolderSizeService.Instance.Invalidate(e.OldFullPath, includeDescendants: true);
                        FolderSizeService.Instance.Invalidate(e.FullPath);
                    };
                    // Buffer overflow: we lost track, so nothing cached on this drive can be trusted.
                    w.Error += (_, _) => FolderSizeService.Instance.Invalidate(root, includeDescendants: true);
                    w.EnableRaisingEvents = true;
                    lock (Watchers) Watchers.Add(w);
                }
                catch { /* a drive we can't watch just keeps the age limit */ }
            }
        });
    }

    /// <summary>Stops the flush timer while no window is open, so an idle background process doesn't wake up.</summary>
    public static void SetActive(bool active)
    {
        if (_timer is null) return;
        if (active) _timer.Start();
        else _timer.Stop();
    }

    private static void Flush()
    {
        string[] paths;
        lock (Pending)
        {
            if (Pending.Count == 0) return;
            paths = [.. Pending];
            Pending.Clear();
        }
        Changed?.Invoke(paths);
    }
}
