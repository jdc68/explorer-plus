using System.IO;
using System.Text.Json;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Services;

public sealed class WindowPlacementSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 760;
    public bool Maximized { get; set; }
}

public enum PaneMode { None, Preview, Details }

public sealed class AppSettings
{
    public bool ShowHiddenFiles { get; set; } = true;
    public bool ShowFileExtensions { get; set; } = true;
    public bool ShowProtectedOsFiles { get; set; }
    public bool ShowFolderSizes { get; set; } = true;
    public bool ShowCheckboxes { get; set; }

    // Bloat review defaults (user can flip any of these).
    public bool ShowGallery { get; set; }
    public bool ShowLibraries { get; set; }
    public bool ShowNetwork { get; set; } = true;
    public bool ShowLinux { get; set; } = true;
    public bool ShowRecentFiles { get; set; } = true;

    // Context-menu entries hidden by default (bloat review). Matched against the entry's text or canonical verb,
    // case-insensitively. Shift+right-click always shows the full, unfiltered menu.
    public List<string> HiddenMenuItems { get; set; } =
    [
        "Copilot", "Designer", "Clipchamp", "Edit with Photos", "Edit with Paint", "Create a new video", "Visual search",
        "Give access to", "Cast to Device", "Include in library", "Nearby", "Quick Share",
        "OneDrive", "Always keep on this device", "Free up space", "View online", "Version history",
    ];

    public bool OpenFoldersInNewTab { get; set; } = true;
    public bool RestoreSession { get; set; } = true;
    public string StartLocation { get; set; } = ShellLocation.Known.Home;

    public List<string> LastSession { get; set; } = [];
    public List<string> PinnedFolders { get; set; } = [];

    /// <summary>Folders opened lately, newest first, for the taskbar jump list.</summary>
    public List<string> RecentFolders { get; set; } = [];

    /// <summary>Pick a view from what a folder holds (pictures as large icons…) unless one was set by hand.</summary>
    public bool ContentAwareViews { get; set; } = true;

    /// <summary>System, Light or Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Starred files and folders (Ctrl+D), in the order they were starred. Local only.</summary>
    public List<string> Starred { get; set; } = [];

    public double SidebarWidth { get; set; } = 250;
    public PaneMode Pane { get; set; }
    public double PaneWidth { get; set; } = 340;
    public WindowPlacementSettings Window { get; set; } = new();

    public static AppSettings Current { get; private set; } = new();

    public event Action? Changed;

    public void NotifyChanged()
    {
        ApplyToCore();
        Changed?.Invoke();
        Save();
    }

    private static string FilePath =>
        Path.Combine(AppPaths.DataFolder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
        Current.ApplyToCore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* settings are best-effort */ }
    }

    public bool IsMenuItemHidden(string? verb, string text) =>
        HiddenMenuItems.Any(p => p.Length > 0 && (text.Contains(p, StringComparison.OrdinalIgnoreCase) ||
                                                 verb is not null && verb.Contains(p.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)));

    private void ApplyToCore()
    {
        ShellFilter.Default.HideGallery = !ShowGallery;
        ShellFilter.Default.HideLibraries = !ShowLibraries;
    }
}
