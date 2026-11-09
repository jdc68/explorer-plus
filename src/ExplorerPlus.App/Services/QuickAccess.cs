using System.IO;
using System.Runtime.InteropServices;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Services;

/// <summary>What Quick access shows, in the sidebar and on Home: the standard user folders, then the user's pins.</summary>
public static class QuickAccess
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static IReadOnlyList<(string Name, string Path, bool IsPinned)> Entries()
    {
        var list = new List<(string, string, bool)>();
        void Known(string name, string? path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path) || ShellFilter.Default.IsBlocked(null, path)) return;
            list.Add((name, path, false));
        }
        Known("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Known("Downloads", KnownFolderPath(DownloadsId));
        Known("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Known("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Known("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Known("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        foreach (var pin in AppSettings.Current.PinnedFolders)
        {
            if (!Directory.Exists(pin) || list.Any(e => string.Equals(e.Item2, pin, StringComparison.OrdinalIgnoreCase))) continue;
            var name = Path.GetFileName(pin.TrimEnd('\\'));
            list.Add((string.IsNullOrEmpty(name) ? pin : name, pin, true));
        }
        return list;
    }

    /// <summary>For Home: the same folders, plus pins that are offline right now (Home skips what doesn't answer).</summary>
    public static IReadOnlyList<string> Paths() =>
        Entries().Select(e => e.Path).Concat(AppSettings.Current.PinnedFolders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static HomeFeedOptions HomeOptions() =>
        new(Paths(), Stars.All.ToList(), AppSettings.Current.ShowRecentFiles);

    private static string? KnownFolderPath(Guid id)
    {
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
