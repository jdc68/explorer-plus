using System.IO;
using System.Runtime.InteropServices;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Services;

/// <summary>What Quick access shows, in the sidebar and on Home: the standard user folders, then the user's pins (frequent folders follow them, see <see cref="FrequentFolders"/>).</summary>
public static class QuickAccess
{
    public const string PinGlyph = "";
    public const string FrequentGlyph = "";

    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static IReadOnlyList<(string Name, string Path, bool IsPinned)> Entries()
    {
        var list = new List<(string, string, bool)>();
        void Known(string name, string? path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path) || ShellFilter.Default.IsBlocked(null, path)) return;
            if (AppSettings.Current.HiddenQuickAccess.Any(h => SamePath(h, path))) return;
            list.Add((name, path, false));
        }
        foreach (var (name, path) in StandardFolders()) Known(name, path);
        foreach (var pin in AppSettings.Current.PinnedFolders)
        {
            if (!Directory.Exists(pin) || list.Any(e => SamePath(e.Item2, pin))) continue;
            var name = Path.GetFileName(pin.TrimEnd('\\'));
            list.Add((string.IsNullOrEmpty(name) ? pin : name, pin, true));
        }
        return list;
    }

    public static bool IsShown(string path) => Entries().Any(e => SamePath(e.Path, path));

    /// <summary>The pins and the standard folders that show, without checking the disk.</summary>
    public static List<string> FixedPaths()
    {
        var settings = AppSettings.Current;
        return StandardFolders()
            .Where(f => !string.IsNullOrEmpty(f.Path) && !settings.HiddenQuickAccess.Any(h => SamePath(h, f.Path!)))
            .Select(f => f.Path!)
            .Concat(settings.PinnedFolders)
            .ToList();
    }

    // Explorer+'s own pins, separate from File Explorer's Quick access
    public static void Pin(string path)
    {
        var settings = AppSettings.Current;
        settings.HiddenQuickAccess.RemoveAll(h => SamePath(h, path));
        if (!IsStandard(path) && !settings.PinnedFolders.Any(p => SamePath(p, path))) settings.PinnedFolders.Add(path);
        settings.NotifyChanged();
    }

    public static void Unpin(string path)
    {
        var settings = AppSettings.Current;
        settings.PinnedFolders.RemoveAll(p => SamePath(p, path));
        if (IsStandard(path) && !settings.HiddenQuickAccess.Any(h => SamePath(h, path))) settings.HiddenQuickAccess.Add(path);
        settings.NotifyChanged();
    }

    private static bool IsStandard(string path) => StandardFolders().Any(f => f.Path is not null && SamePath(f.Path, path));

    private static IEnumerable<(string Name, string? Path)> StandardFolders()
    {
        yield return ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        yield return ("Downloads", KnownFolderPath(DownloadsId));
        yield return ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        yield return ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        yield return ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        yield return ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>For Home: the same folders, plus pins that are offline right now (Home skips what doesn't answer), then the frequent ones.</summary>
    public static IReadOnlyList<string> Paths() =>
        Entries().Select(e => e.Path).Concat(AppSettings.Current.PinnedFolders).Concat(FrequentFolders.Shown)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
