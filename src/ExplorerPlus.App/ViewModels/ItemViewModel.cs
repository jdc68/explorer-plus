using System.Windows;
using System.Windows.Media;
using ExplorerPlus.App.Infrastructure;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.App.ViewModels;

public sealed class ItemViewModel : ObservableObject
{
    private FolderEntry _entry;
    private int _iconIndex;
    private bool _realIconRequested;
    private bool _isRenaming;
    private string _editName = "";
    private long? _folderSize;
    private int _imageSize;
    private ImageSource? _thumb;
    private int _thumbFor;
    private bool _thumbRequested;
    private bool _sizePending;

    public ItemViewModel(FolderEntry entry)
    {
        _entry = entry;
        _iconIndex = entry.IconIndex;
    }

    public FolderEntry Entry => _entry;

    public string Name => _entry.Name;

    public bool IsFolder => _entry.IsFolder;

    public string DisplayName
    {
        get
        {
            var name = _entry.DisplayName;
            if (_entry.Path is null || _entry.IsFolder) return name;
            var ext = _entry.Extension;
            // .lnk/.url never show their extension in Explorer (NeverShowExt); others follow the setting.
            var hide = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
                       || !AppSettings.Current.ShowFileExtensions;
            return hide && ext.Length > 0 && name.Length > ext.Length ? name[..^ext.Length] : name;
        }
    }

    public string TypeName => _entry.TypeName;

    /// <summary>Starred (Ctrl+D); shown as a star next to the name.</summary>
    public bool IsStarred => Stars.IsStarred(_entry.Path);

    public void RefreshStar() => OnPropertyChanged(nameof(IsStarred));

    public string SizeText => _entry.IsFolder
        ? _folderSize is { } s ? FormatSize(s) : _sizePending ? "…" : ""
        : _entry.Size < 0 ? "" : FormatSize(_entry.Size);

    /// <summary>Recursive size of a folder once measured (null until then).</summary>
    public long? FolderSize => _folderSize;

    /// <summary>Size used for sorting and totals: the file size, or the measured folder size.</summary>
    public long SortSize => _entry.IsFolder ? _folderSize ?? -1 : _entry.Size;

    public void SetFolderSize(long? size, bool pending)
    {
        if (_folderSize == size && _sizePending == pending) return;
        _folderSize = size;
        _sizePending = pending;
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(FolderSize));
    }

    public string ModifiedText => _entry.Modified is { } d ? d.ToString("g") : "";

    public string CreatedText => _entry.Created is { } d ? d.ToString("g") : "";

    // ---------- Shell property columns ----------

    private Dictionary<string, PropertyValue>? _props;
    private HashSet<string>? _pendingProps;
    private bool _fetchScheduled;

    /// <summary>A shell property as text ("System.Music.Artist"); empty until loaded. Bindings use this indexer, so
    /// only items on screen ever read metadata, batched into one call per item.</summary>
    public string this[string key]
    {
        get
        {
            if (_props is not null && _props.TryGetValue(key, out var v)) return v.Display;
            RequestProperty(key);
            return "";
        }
    }

    public bool HasProperty(string key) => _props?.ContainsKey(key) == true;

    public IComparable? GetSortValue(string key) => _props is not null && _props.TryGetValue(key, out var v) ? v.Sort : null;

    public string GetPropertyText(string key) => _props is not null && _props.TryGetValue(key, out var v) ? v.Display : "";

    private void RequestProperty(string key)
    {
        _pendingProps ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_pendingProps.Add(key) || _fetchScheduled) return;
        _fetchScheduled = true;
        Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _fetchScheduled = false;
            if (_pendingProps.Count > 0) _ = LoadPropertiesAsync(_pendingProps.ToList());
        });
    }

    /// <summary>Reads the given properties (skipping ones already known) on the property workers.</summary>
    public async Task LoadPropertiesAsync(IReadOnlyCollection<string> keys)
    {
        var wanted = keys.Where(k => !HasProperty(k)).ToList();
        if (wanted.Count == 0) return;
        var path = _entry.Path;
        // Items listed by the shell (Recycle Bin, search results) carry properties only their own folder knows,
        // such as "Original location", so read them through that item rather than through the path.
        var pidl = path is null || _entry.ShellAttributes != 0 ? _entry.Pidl : null;
        Dictionary<string, PropertyValue> values;
        try { values = await StaWorkerPool.Properties.Run(() => PropertyReader.Read(path, pidl, wanted)); }
        catch { values = wanted.ToDictionary(k => k, _ => PropertyValue.Empty); }
        _props ??= new Dictionary<string, PropertyValue>(StringComparer.OrdinalIgnoreCase);
        var isDrive = path is { Length: <= 3 } && path.EndsWith(":\\", StringComparison.Ordinal);
        foreach (var k in wanted)
        {
            // Files report their volume's capacity too; Explorer shows these only for drives.
            var driveOnly = k is "System.Capacity" or "System.FreeSpace" or "System.PercentFull";
            _props[k] = driveOnly && !isDrive ? PropertyValue.Empty : values.GetValueOrDefault(k, PropertyValue.Empty);
            _pendingProps?.Remove(k);
        }
        OnPropertyChanged("Item[]");
    }

    // ---------- Grouping (set by the tab before the list is published) ----------

    public string GroupKey { get; set; } = "";
    public int GroupRank { get; set; }

    /// <summary>Hidden items are drawn faded, as in Explorer.</summary>
    public double ItemOpacity => _entry.IsHidden ? 0.55 : 1.0;

    public int IconIndex => _iconIndex;

    public ImageSource? Icon
    {
        get
        {
            if (_entry.NeedsRealIcon && !_realIconRequested) RequestRealIcon();
            return IconCache.Get(_iconIndex);
        }
    }

    /// <summary>Size of <see cref="LargeImage"/> for the current view; 0 when the view only uses small icons.</summary>
    public int ImageSize
    {
        get => _imageSize;
        set
        {
            if (_imageSize == value) return;
            _imageSize = value;
            _thumbRequested = false;
            OnPropertyChanged(nameof(LargeImage));
        }
    }

    /// <summary>Thumbnail (or big icon) for icon, tile and content views. Shows the icon at once and swaps in the
    /// shell thumbnail when it's ready; only requested for items that are actually on screen.</summary>
    public ImageSource? LargeImage
    {
        get
        {
            var size = _imageSize;
            if (size <= 0) return Icon;
            if (_thumb is not null && _thumbFor == size) return _thumb;
            var key = ThumbnailService.KeyOf(_entry.Path, _entry.Pidl);
            if (ThumbnailService.TryGet(key, size) is { } cached)
            {
                _thumb = cached;
                _thumbFor = size;
                return cached;
            }
            if (!_thumbRequested)
            {
                _thumbRequested = true;
                ThumbnailService.Request(_entry.Path, _entry.Path is null ? _entry.Pidl : null, size, image =>
                {
                    if (image is null || size != _imageSize) return;
                    _thumb = image;
                    _thumbFor = size;
                    OnPropertyChanged(nameof(LargeImage));
                }, () => _thumbRequested = false);
            }
            if (_entry.NeedsRealIcon && !_realIconRequested) RequestRealIcon();
            return IconCache.Get(_iconIndex, size);
        }
    }
    private bool _isDropTarget;

    /// <summary>A drag is hovering this item and would drop into it.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value);
    }

    public void Update(FolderEntry entry)
    {
        if (entry.Pidl is null && _entry.Pidl is not null && string.Equals(entry.Path, _entry.Path, StringComparison.OrdinalIgnoreCase))
            entry.Pidl = _entry.Pidl;
        _entry = entry;
        _iconIndex = entry.IconIndex;
        _realIconRequested = false;
        if (entry.Path is not null) ThumbnailService.Invalidate(entry.Path);
        _thumb = null;
        _thumbRequested = false;
        _props = null;
        OnPropertyChanged(string.Empty);
    }

    public void RefreshDisplay() => OnPropertyChanged(nameof(DisplayName));

    private void RequestRealIcon()
    {
        _realIconRequested = true;
        var path = _entry.Path;
        if (path is null) return;
        var dispatcher = Application.Current.Dispatcher;
        StaWorkerPool.Icons.Run(() => ShellInfoCache.GetIconIndexForPath(path)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result >= 0 && t.Result != _iconIndex)
                dispatcher.BeginInvoke(() =>
                {
                    _iconIndex = t.Result;
                    OnPropertyChanged(nameof(Icon));
                    if (_thumb is null) OnPropertyChanged(nameof(LargeImage));
                });
        }, TaskScheduler.Default);
    }

    public static string FormatSize(long bytes) => SizeFormat.Format(bytes);

    public static string FormatSizeShort(long bytes) => SizeFormat.FormatWithBytes(bytes);
}
