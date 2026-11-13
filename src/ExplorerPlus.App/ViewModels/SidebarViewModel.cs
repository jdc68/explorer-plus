using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using ExplorerPlus.App.Infrastructure;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.ViewModels;

public abstract class SidebarItem : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;
    private int _iconIndex = -1;

    public string Name { get; protected set; } = "";
    public ObservableCollection<SidebarItem> Children { get; } = [];
    public virtual bool IsHeader => false;

    public ImageSource? Icon => IconCache.Get(_iconIndex);

    protected void SetIcon(int index)
    {
        _iconIndex = index;
        OnPropertyChanged(nameof(Icon));
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value) && value) OnExpanded(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    protected virtual void OnExpanded() { }
}

public sealed class SidebarHeader : SidebarItem
{
    public SidebarHeader(string name)
    {
        Name = name;
        IsExpanded = true;
    }

    public override bool IsHeader => true;
}

public sealed class SidebarPlaceholder : SidebarItem
{
    public SidebarPlaceholder() => Name = "Loading…";
}

public class SidebarFolder : SidebarItem
{
    private bool _loaded;

    public SidebarFolder(string name, string target, string? fileSystemPath, bool expandable, bool isPinned = false, bool isStarred = false,
        string? glyph = null)
    {
        Name = name;
        Target = target;
        FileSystemPath = fileSystemPath;
        IsPinned = isPinned;
        IsStarredEntry = isStarred;
        Glyph = glyph;
        if (expandable) Children.Add(new SidebarPlaceholder());
        if (glyph is null) _ = LoadIconAsync();
    }

    // drawn instead of a shell icon (tags have none)
    public string? Glyph { get; }

    /// <summary>Parsing name or path to navigate to.</summary>
    public string Target { get; }
    public string? FileSystemPath { get; }
    public bool IsPinned { get; }

    /// <summary>An entry of the Starred section (may be a file).</summary>
    public bool IsStarredEntry { get; }

    private async Task LoadIconAsync()
    {
        try
        {
            var target = Target;
            var index = await StaWorkerPool.Icons.Run(() =>
            {
                if (FileSystemPath is not null) return ShellInfoCache.GetIconIndexForPath(FileSystemPath);
                var pidl = ShellNative.TryParse(target);
                if (pidl is null) return -1;
                using var n = pidl.ToNative();
                return ShellInfoCache.GetForPidl(n.Ptr).IconIndex;
            });
            SetIcon(index);
        }
        catch { }
    }

    protected override async void OnExpanded()
    {
        if (_loaded || FileSystemPath is null) return;
        _loaded = true;
        var path = FileSystemPath;
        var settings = AppSettings.Current;
        try
        {
            var folders = await Task.Run(() => FastFolderEnumerator.Enumerate(path)
                .Where(e => e.IsFolder
                            && (settings.ShowHiddenFiles || !e.IsHidden)
                            && (settings.ShowProtectedOsFiles || !e.IsProtectedSystem))
                .OrderBy(e => e.Name, NaturalComparer.Instance)
                .ToList());
            Children.Clear();
            foreach (var f in folders)
            {
                // Subfolders always offer to expand; the placeholder disappears if they turn out empty.
                Children.Add(new SidebarFolder(f.Name, f.Path!, f.Path, expandable: (f.FileAttributes & FileAttr.REPARSE_POINT) == 0));
            }
        }
        catch
        {
            Children.Clear();
        }
    }
}

public sealed class SidebarDrive : SidebarFolder
{
    public SidebarDrive(DriveEntry drive) : base(drive.DisplayName, drive.Root, drive.Root, expandable: drive.HasSpaceInfo)
    {
        Drive = drive;
    }

    public DriveEntry Drive { get; }
    public bool HasSpaceInfo => Drive.HasSpaceInfo;
    public double UsedPercent => Drive.UsedFraction * 100;
    public bool IsAlmostFull => Drive.UsedFraction >= 0.9;
    public string SpaceText => Drive.HasSpaceInfo
        ? $"{ItemViewModel.FormatSizeShort((long)Drive.FreeBytes)} free of {ItemViewModel.FormatSizeShort((long)Drive.TotalBytes)}"
        : "";
}

public sealed class SidebarViewModel : ObservableObject, IDisposable
{
    private readonly SidebarHeader _quick = new("Quick access");
    private readonly SidebarHeader _starred = new("Starred");
    private readonly SidebarHeader _tags = new("Tags");
    private readonly SidebarHeader _drives = new("Drives");
    private readonly SidebarHeader _locations = new("Locations");
    private readonly IDisposable _driveWatch;
    private CancellationTokenSource? _driveDebounce;

    public ObservableCollection<SidebarItem> Roots { get; } = [];

    public SidebarViewModel()
    {
        Roots.Add(_quick);
        Roots.Add(_drives);
        Roots.Add(_locations);
        BuildQuickAccess();
        BuildStarred();
        BuildTags();
        BuildLocations();
        _ = RefreshDrivesAsync();
        Stars.Changed += BuildStarred;
        Tags.Changed += BuildTags;

        _driveWatch = ShellChangeWatcher.Instance.Watch(Pidl.Desktop, true,
            SHCNE.DRIVEADD | SHCNE.DRIVEREMOVED | SHCNE.MEDIAINSERTED | SHCNE.MEDIAREMOVED | SHCNE.FREESPACE | SHCNE.NETSHARE,
            _ => ScheduleDriveRefresh());
        AppSettings.Current.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        BuildQuickAccess();
        BuildLocations();
    }

    public void BuildQuickAccess()
    {
        _quick.Children.Clear();
        _quick.Children.Add(new SidebarFolder("Home", ShellLocation.Known.Home, null, expandable: false));
        foreach (var (name, path, pinned) in QuickAccess.Entries())
            _quick.Children.Add(new SidebarFolder(name, path, path, expandable: true, isPinned: pinned));
    }

    /// <summary>The Starred section, shown only while something is starred.</summary>
    private void BuildStarred()
    {
        _starred.Children.Clear();
        foreach (var path in Stars.All)
        {
            var name = Path.GetFileName(path.TrimEnd('\\'));
            var isFolder = Directory.Exists(path);
            _starred.Children.Add(new SidebarFolder(string.IsNullOrEmpty(name) ? path : name, path, path, expandable: isFolder, isStarred: true));
        }
        var shown = Roots.Contains(_starred);
        if (_starred.Children.Count > 0 && !shown) Roots.Insert(Roots.IndexOf(_quick) + 1, _starred);
        else if (_starred.Children.Count == 0 && shown) Roots.Remove(_starred);
    }

    public const string TagGlyph = "\uE8EC";

    private void BuildTags()
    {
        var names = Tags.Names;
        if (names.SequenceEqual(_tags.Children.Select(c => c.Name))) return;
        _tags.Children.Clear();
        foreach (var tag in names)
            _tags.Children.Add(new SidebarFolder(tag, TagView.Url(tag), null, expandable: false, glyph: TagGlyph));
        var shown = Roots.Contains(_tags);
        if (_tags.Children.Count > 0 && !shown) Roots.Insert(Roots.IndexOf(_drives), _tags);
        else if (_tags.Children.Count == 0 && shown) Roots.Remove(_tags);
    }

    private void BuildLocations()
    {
        var s = AppSettings.Current;
        _locations.Children.Clear();
        _locations.Children.Add(new SidebarFolder("This PC", ShellLocation.Known.ThisPC, null, expandable: false));
        if (s.ShowNetwork) _locations.Children.Add(new SidebarFolder("Network", ShellLocation.Known.Network, null, expandable: false));
        if (s.ShowLinux && HasWslDistros())
            _locations.Children.Add(new SidebarFolder("Linux", ShellLocation.Known.Linux, null, expandable: false));
        _locations.Children.Add(new SidebarFolder("Recycle Bin", ShellLocation.Known.RecycleBin, null, expandable: false));
    }

    // wsl.exe ships with every Windows 11 install; only show Linux when a distro is actually registered.
    private static bool HasWslDistros()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            return key?.GetSubKeyNames().Length > 0;
        }
        catch { return false; }
    }

    private void ScheduleDriveRefresh()
    {
        _driveDebounce?.Cancel();
        var cts = _driveDebounce = new CancellationTokenSource();
        Task.Delay(800, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ = RefreshDrivesAsync();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public async Task RefreshDrivesAsync()
    {
        var drives = await DriveService.GetDrivesAsync();
        var expanded = _drives.Children.OfType<SidebarDrive>().Where(d => d.IsExpanded).Select(d => d.Drive.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _drives.Children.Clear();
        foreach (var d in drives)
        {
            var item = new SidebarDrive(d);
            _drives.Children.Add(item);
            if (expanded.Contains(d.Root)) item.IsExpanded = true;
        }
    }

    public void Pin(string path) => QuickAccess.Pin(path);

    public void Unpin(string path) => QuickAccess.Unpin(path);

    /// <summary>Highlights the entry matching the current location, if it is a top-level entry.</summary>
    public void SyncSelection(ShellLocation? location)
    {
        foreach (var header in Roots)
            foreach (var item in header.Children.OfType<SidebarFolder>())
            {
                var match = location is not null &&
                            (string.Equals(item.FileSystemPath?.TrimEnd('\\'), location.FileSystemPath?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && item.FileSystemPath is not null
                             || string.Equals(item.Target, location.ParsingName, StringComparison.OrdinalIgnoreCase));
                if (item.IsSelected != match) item.IsSelected = match;
            }
    }

    public void Dispose()
    {
        _driveWatch.Dispose();
        AppSettings.Current.Changed -= OnSettingsChanged;
        Stars.Changed -= BuildStarred;
        Tags.Changed -= BuildTags;
    }
}
