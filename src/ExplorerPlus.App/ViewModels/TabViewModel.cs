using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using ExplorerPlus.App.Infrastructure;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.ViewModels;

public sealed class BreadcrumbSegment(ShellLocation location)
{
    public ShellLocation Location { get; } = location;
    public string Name => Location.DisplayName;
}

public sealed class TabViewModel : ObservableObject, IDisposable
{
    private readonly List<ShellLocation> _back = [];
    private readonly List<ShellLocation> _forward = [];
    private List<ItemViewModel> _all = [];
    private readonly Dictionary<string, ItemViewModel> _byName = new(StringComparer.OrdinalIgnoreCase);

    private ShellLocation? _location;
    private CancellationTokenSource? _loadCts;
    private IDisposable? _watch;
    private int _viewVersion;
    private bool _isLoading;
    private bool _dirtyDuringLoad;
    private string? _error;
    private string _filterText = "";
    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortAscending = true;
    private IReadOnlyList<BreadcrumbSegment> _breadcrumb = [];
    private IReadOnlyList<ItemViewModel> _selectedItems = [];
    private int _iconIndex = -1;
    private string? _renameWhenCreated;
    private DateTime _renameNextCreatedUntil;
    private CancellationTokenSource? _refreshDebounce;
    private CancellationTokenSource? _sizesCts;
    private int _pendingSizes;
    private ViewMode _viewMode = ViewMode.Details;
    private bool _viewIsRemembered;
    private FolderContent _content;
    private List<string> _columns = [ColumnCatalog.Name, ColumnCatalog.Modified, ColumnCatalog.Type, ColumnCatalog.Size];
    private readonly Dictionary<string, double> _columnWidths = new(StringComparer.OrdinalIgnoreCase);
    private bool _columnsRemembered;
    private string? _groupBy;
    private string? _sortProperty;
    private TabViewModel? _partner;

    public TabViewModel()
    {
        FolderSizeWatcher.Changed += OnFolderSizesInvalidated;
        Stars.Changed += OnStarsChanged;
        Tags.Changed += OnTagsChanged;
        CutItems.Changed += OnCutChanged;
    }

    public RangeObservableCollection<ItemViewModel> Items { get; } = new();

    /// <summary>
    /// The right-hand pane when this tab is split (dual pane). It is a view of its own, with its own history,
    /// layout and selection, and it goes wherever the tab goes (another window, closing).
    /// </summary>
    public TabViewModel? Partner
    {
        get => _partner;
        set
        {
            if (Set(ref _partner, value)) OnPropertyChanged(nameof(IsSplit));
        }
    }

    public bool IsSplit => _partner is not null;

    public ShellLocation? Location
    {
        get => _location;
        private set
        {
            if (!Set(ref _location, value)) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ToolTip));
            OnPropertyChanged(nameof(CanGoUp));
            OnPropertyChanged(nameof(SearchPlaceholder));
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(IsHome));
            OnPropertyChanged(nameof(IsRecycleBin));
            OnPropertyChanged(nameof(IsSearch));
            OnPropertyChanged(nameof(IsLiveSearch));
            OnPropertyChanged(nameof(ShowNetworkActions));
            OnPropertyChanged(nameof(CanSearch));
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    public string Title => _location?.DisplayName ?? "Loading…";

    /// <summary>The tab's tooltip: the folder's path, or its name for virtual folders (not a CLSID).</summary>
    public string? ToolTip => _location is null ? null : _location.FileSystemPath ?? _location.DisplayName;
    public string SearchPlaceholder => IsLiveSearch
        ? $"Search {_location!.SearchScope?.DisplayName}"
        : _location?.IsTagView == true
        ? $"Filter items tagged “{_location.Tag}”"
        : _location?.IsDuplicates == true
        ? $"Filter {_location.DisplayName}"
        : Kind == LocationKind.Search
        ? $"Filter results for “{_location!.SearchQuery}”"
        : CanSearch ? $"Search {_location!.DisplayName}" : $"Filter {_location?.DisplayName ?? ""}";

    public LocationKind Kind => _location switch
    {
        null => LocationKind.Folder,
        { IsSearch: true } or { IsTagView: true } or { IsDuplicates: true } => LocationKind.Search,
        { IsHome: true } => LocationKind.Home,
        { IsThisPC: true } => LocationKind.ThisPC,
        { IsNetwork: true } => LocationKind.Network,
        { IsRecycleBin: true } => LocationKind.RecycleBin,
        _ => LocationKind.Folder,
    };

    public bool IsHome => Kind == LocationKind.Home;
    public bool IsRecycleBin => Kind == LocationKind.RecycleBin;
    public bool IsSearch => Kind == LocationKind.Search;
    public bool ShowNetworkActions => Kind is LocationKind.ThisPC or LocationKind.Network;

    /// <summary>What Enter in the search box searches: this folder, or for search results the folder they came from.</summary>
    public ShellLocation? SearchScope => _location?.SearchScope ?? _location;

    public bool CanSearch => SearchLocation.CanSearch(SearchScope);

    /// <summary>Lists that aren't one folder's own content: no content-based layout.</summary>
    private bool IsCollection => Kind is LocationKind.Home or LocationKind.Search or LocationKind.RecycleBin;
    public ImageSource? Icon => IconCache.Get(_iconIndex);

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => _location is { Pidl.IsDesktop: false };

    public IReadOnlyList<ShellLocation> BackHistory => _back;
    public IReadOnlyList<ShellLocation> ForwardHistory => _forward;

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (Set(ref _isLoading, value)) UpdateStatus(); }
    }

    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(ShowEmptyMessage)); }
    }

    public bool ShowEmptyMessage => !_isLoading && _error is null && Items.Count == 0;

    /// <summary>Instant-search results: the search box holds the query rather than a filter.</summary>
    public bool IsLiveSearch => _location?.IsIndexSearch == true;

    /// <summary>What the list is filtered by (nothing on instant-search results, whose box holds the query).</summary>
    private string ListFilter => IsLiveSearch ? "" : _filterText;

    public string EmptyMessage => ListFilter.Length > 0 ? "No items match your filter." : Kind switch
    {
        LocationKind.Search when IsLiveSearch && string.IsNullOrWhiteSpace(_location!.SearchQuery) =>
            $"Type a name to search {_location.SearchScope?.DisplayName}. Words: all must match · *.pdf: whole name · folder\\name: path · !word: exclude.",
        LocationKind.Search when _location!.IsDuplicates => $"No two files in {_location.DuplicatesIn} have the same content.",
        LocationKind.Search when _location!.IsTagView => $"Nothing is tagged “{_location.Tag}” any more.",
        LocationKind.Search => "No items match your search.",
        LocationKind.Home => "Pin folders and star files (Ctrl+D) to see them here.",
        LocationKind.RecycleBin => "The Recycle Bin is empty.",
        _ => "This folder is empty.",
    };

    public IReadOnlyList<BreadcrumbSegment> Breadcrumb
    {
        get => _breadcrumb;
        private set => Set(ref _breadcrumb, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!Set(ref _filterText, value ?? "")) return;
            OnPropertyChanged(nameof(EmptyMessage));
            if (IsLiveSearch) ScheduleRequery();
            else _ = ApplyViewAsync(keepSelection: true);
        }
    }

    public ViewMode ViewMode => _viewMode;

    /// <summary>True when the user picked this folder's view; false when it was chosen from the folder's content.</summary>
    public bool ViewIsRemembered => _viewIsRemembered;

    public FolderContent Content => _content;

    public SortColumn SortColumn => _sortColumn;
    public bool SortAscending => _sortAscending;

    public IReadOnlyList<ItemViewModel> SelectedItems
    {
        get => _selectedItems;
        set
        {
            _selectedItems = value;
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    public string ItemCountText { get; private set; } = "";
    public string SelectionText { get; private set; } = "";

    /// <summary>Raised after Items was replaced; the view selects these items (if any) and scrolls to the first.</summary>
    public event Action<IReadOnlyList<ItemViewModel>>? SelectionRequested;
    public event Action? ScrollToTopRequested;

    /// <summary>Raised when an item should enter inline rename (new folder, F2).</summary>
    public event Action<ItemViewModel>? RenameRequested;

    /// <summary>Raised when the current folder disappeared or could not be opened and the tab moved elsewhere.</summary>
    public event Action<string>? Notice;

    // Per-tab view state kept while another tab is active.
    public double SavedScrollOffset { get; set; }
    public List<ItemViewModel> SavedSelection { get; set; } = [];

    // ---------- Navigation ----------

    /// <summary>Replace: show another location in place of this one, without a history step (instant search while typing).</summary>
    public enum NavMode { New, Back, Forward, Refresh, Replace }

    public async Task NavigateAsync(ShellLocation location, NavMode mode = NavMode.New, string? selectPath = null)
    {
        var previous = _location;
        if (mode == NavMode.New && previous is not null && !previous.IsSameAs(location))
        {
            _back.Add(previous);
            _forward.Clear();
        }
        if (mode == NavMode.Refresh && previous is not null && previous.IsSameAs(location))
        {
            await RefreshAsync();
            return;
        }

        Location = location;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        // On instant-search results the box holds the query, and typing searches again; while typing (Replace) the
        // box keeps exactly what was typed.
        if (mode != NavMode.Replace)
        {
            _filterText = location.IsIndexSearch ? location.SearchQuery ?? "" : "";
            OnPropertyChanged(nameof(FilterText));
        }

        _watch?.Dispose();
        _watch = WatchFor(location);

        ApplyRememberedView();
        _ = LoadBreadcrumbAsync(location);
        _ = LoadIconAsync(location);
        await LoadAsync(clearFirst: true, selectPath: selectPath);
    }

    private IDisposable? WatchFor(ShellLocation location)
    {
        // A search would restart on every change anywhere below it; F5 runs it again instead.
        if (location.IsTagView || location.IsDuplicates)
            return ShellChangeWatcher.Instance.Watch(Pidl.Desktop, true, SHCNE.DELETE | SHCNE.RMDIR | SHCNE.RENAMEITEM | SHCNE.RENAMEFOLDER,
                c => RemoveGone(c.Path1));
        if (location.IsSearch) return null;
        if (location.IsHome)
        {
            // Stars and settings raise their own events; opening a file anywhere updates the Recent folder.
            var recent = ShellNative.TryParse(Environment.GetFolderPath(Environment.SpecialFolder.Recent));
            return recent is null ? null : ShellChangeWatcher.Instance.Watch(recent, false, SHCNE.ALLEVENTS, _ => ScheduleRefresh(1500));
        }
        return ShellChangeWatcher.Instance.Watch(location.Pidl, false, SHCNE.ALLEVENTS, OnShellChange);
    }

    /// <summary>
    /// Searches for <paramref name="query"/> in this tab's folder (or the folder of the current results): instantly
    /// through the index service when it runs and covers the folder, otherwise (and for Windows Search's own
    /// <c>kind:</c>, <c>date:</c> syntax) with Windows Search.
    /// </summary>
    public async Task<bool> SearchAsync(string query)
    {
        var scope = SearchScope;
        if (string.IsNullOrWhiteSpace(query) || !SearchLocation.CanSearch(scope)) return false;
        if (IsLiveSearch && string.Equals(query.Trim(), _location!.SearchQuery, StringComparison.Ordinal)) return true;
        if (!IndexSearch.IsWindowsSearchQuery(query) && await IndexSearch.CanSearchAsync(scope))
        {
            await NavigateAsync(IndexSearch.Create(query, scope!), IsLiveSearch ? NavMode.Replace : NavMode.New);
            return true;
        }
        var search = await StaWorkerPool.Enumeration.Run(() => SearchLocation.Create(query, scope!));
        if (search is null) return false;
        await NavigateAsync(search);
        return true;
    }

    private CancellationTokenSource? _requeryCts;

    /// <summary>Typing on instant-search results: search again once typing pauses.</summary>
    private async void ScheduleRequery()
    {
        _requeryCts?.Cancel();
        var cts = _requeryCts = new CancellationTokenSource();
        try { await Task.Delay(150, cts.Token); }
        catch (OperationCanceledException) { return; }
        var query = _filterText.Trim();
        if (_location is not { IsIndexSearch: true, SearchScope: { } scope } || query.Length == 0 || query == _location.SearchQuery) return;
        if (IndexSearch.IsWindowsSearchQuery(query)) return; // Enter hands it to Windows Search
        await NavigateAsync(IndexSearch.Create(query, scope), NavMode.Replace);
    }

    public async Task GoBackAsync()
    {
        if (_back.Count == 0 || _location is null) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(_location);
        await NavigateAsync(target, NavMode.Back, selectPath: ChildPathToSelect(_location, target));
    }

    public async Task GoForwardAsync()
    {
        if (_forward.Count == 0 || _location is null) return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(_location);
        await NavigateAsync(target, NavMode.Forward);
    }

    /// <summary>Jumps to an entry in the back (negative) or forward (positive) history list.</summary>
    public async Task GoToHistoryAsync(ShellLocation target)
    {
        if (_location is null) return;
        var bi = _back.IndexOf(target);
        if (bi >= 0)
        {
            _forward.Add(_location);
            for (var i = _back.Count - 1; i > bi; i--) { _forward.Add(_back[i]); _back.RemoveAt(i); }
            _back.RemoveAt(bi);
            await NavigateAsync(target, NavMode.Back);
            return;
        }
        var fi = _forward.IndexOf(target);
        if (fi >= 0)
        {
            _back.Add(_location);
            for (var i = _forward.Count - 1; i > fi; i--) { _back.Add(_forward[i]); _forward.RemoveAt(i); }
            _forward.RemoveAt(fi);
            await NavigateAsync(target, NavMode.Forward);
        }
    }

    public async Task GoUpAsync()
    {
        if (_location is null) return;
        var parent = await _location.GetParentAsync();
        if (parent is null) return;
        await NavigateAsync(parent, NavMode.New, selectPath: _location.FileSystemPath);
    }

    public Task RefreshAsync() => _location is null ? Task.CompletedTask : LoadAsync(clearFirst: false, keepSelection: true);

    private static string? ChildPathToSelect(ShellLocation from, ShellLocation to)
    {
        if (from.FileSystemPath is null) return null;
        var parent = Path.GetDirectoryName(from.FileSystemPath.TrimEnd('\\'));
        if (to.FileSystemPath is not null && string.Equals(parent?.TrimEnd('\\'), to.FileSystemPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return from.FileSystemPath;
        // Drive root → This PC: select the drive.
        return from.FileSystemPath.Length <= 3 ? from.FileSystemPath : null;
    }

    private async Task LoadBreadcrumbAsync(ShellLocation location)
    {
        try
        {
            var chain = await location.GetAncestryAsync();
            if (ReferenceEquals(location, _location)) Breadcrumb = chain.Select(l => new BreadcrumbSegment(l)).ToList();
        }
        catch { Breadcrumb = [new BreadcrumbSegment(location)]; }
    }

    private async Task LoadIconAsync(ShellLocation location)
    {
        try
        {
            var index = await StaWorkerPool.Icons.Run(() =>
            {
                using var n = location.Pidl.ToNative();
                return ShellInfoCache.GetForPidl(n.Ptr).IconIndex;
            });
            if (!ReferenceEquals(location, _location)) return;
            _iconIndex = index;
            OnPropertyChanged(nameof(Icon));
        }
        catch { }
    }

    // ---------- Loading ----------

    private async Task LoadAsync(bool clearFirst, string? selectPath = null, bool keepSelection = false)
    {
        var location = _location!;
        _loadCts?.Cancel();
        if (clearFirst) _sizesCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var keep = keepSelection ? _selectedItems.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        IsLoading = true;
        Error = null;
        _dirtyDuringLoad = false;
        if (clearFirst)
        {
            ScrollToTopRequested?.Invoke();
            SetAll([]);
            Items.ReplaceAll([]);
            SelectedItems = [];
        }

        var fresh = new List<ItemViewModel>();
        var shownFirst = !clearFirst; // a refresh keeps showing the old list until the new one is complete
        var sinceApply = Stopwatch.StartNew();
        try
        {
            await foreach (var batch in FolderLoader.LoadAsync(location, cts.Token))
            {
                foreach (var e in batch) fresh.Add(NewItem(e));
                if (!shownFirst || (clearFirst && sinceApply.ElapsedMilliseconds > 400))
                {
                    shownFirst = true;
                    SetAll(fresh);
                    if (clearFirst) ChooseViewFromContent();
                    await ApplyViewAsync(keepSelection: false);
                    if (clearFirst) ScrollToTopRequested?.Invoke();
                    sinceApply.Restart();
                }
            }
            if (cts.IsCancellationRequested) return;

            SetAll(fresh);
            if (clearFirst) ChooseViewFromContent();
            var select = ResolveSelection(selectPath, keep);
            await ApplyViewAsync(keepSelection: false, select: select);
            if (clearFirst && select is not { Count: > 0 }) ScrollToTopRequested?.Invoke();
            RequestFolderSizes(_all);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            SetAll([]);
            Items.ReplaceAll([]);
            Error = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                OnPropertyChanged(nameof(ShowEmptyMessage));
                if (_dirtyDuringLoad) ScheduleRefresh();
            }
        }
    }

    private List<ItemViewModel>? ResolveSelection(string? selectPath, HashSet<string>? keepNames)
    {
        if (selectPath is not null)
        {
            var target = selectPath.TrimEnd('\\');
            var match = _all.FirstOrDefault(i => i.Entry.Path is { } p && string.Equals(p.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase));
            return match is null ? null : [match];
        }
        if (keepNames is { Count: > 0 }) return _all.Where(i => keepNames.Contains(i.Name)).ToList();
        return null;
    }

    private void SetAll(List<ItemViewModel> items)
    {
        _all = items;
        _byName.Clear();
        foreach (var i in items) _byName.TryAdd(i.Name, i);
    }

    /// <summary>Filters, groups and sorts the full list (off the UI thread for big folders) and publishes it.</summary>
    public async Task ApplyViewAsync(bool keepSelection, IReadOnlyList<ItemViewModel>? select = null)
    {
        var version = ++_viewVersion;
        var snapshot = _all.ToArray();
        var filter = ListFilter;
        var settings = AppSettings.Current;
        var showHidden = settings.ShowHiddenFiles;
        var showProtected = settings.ShowProtectedOsFiles;
        var groupBy = _groupBy;
        var comparer = new ItemComparer(_sortColumn, _sortAscending, _sortProperty, groupBy is not null);
        var toSelect = select ?? (keepSelection ? _selectedItems.ToList() : null);

        // Sorting or grouping by a shell property needs that property for every item, not just the visible ones.
        var needed = new List<string>();
        if (_sortColumn == SortColumn.Property && _sortProperty is not null) needed.Add(_sortProperty);
        if (groupBy is not null && !ColumnCatalog.BuiltIn.Contains(groupBy)) needed.Add(groupBy);
        if (needed.Count > 0)
        {
            var missing = snapshot.Where(i => needed.Any(k => !i.HasProperty(k))).ToList();
            if (missing.Count > 0)
            {
                await Task.WhenAll(missing.Select(i => i.LoadPropertiesAsync(needed)));
                if (version != _viewVersion) return;
            }
        }

        ItemViewModel[] Compute()
        {
            var list = new List<ItemViewModel>(snapshot.Length);
            foreach (var i in snapshot)
            {
                if (!PassesFilter(i, filter, showHidden, showProtected)) continue;
                list.Add(i);
            }
            if (groupBy is not null) Grouping.Assign(list, groupBy);
            var arr = list.ToArray();
            Array.Sort(arr, comparer);
            return arr;
        }

        var result = snapshot.Length < 3000 ? Compute() : await Task.Run(Compute);
        if (version != _viewVersion) return;

        Items.ReplaceAll(result);
        OnPropertyChanged(nameof(ShowEmptyMessage));
        UpdateStatus();
        if (toSelect is { Count: > 0 }) SelectionRequested?.Invoke(toSelect);
    }

    private static bool PassesFilter(ItemViewModel i, string filter, bool showHidden, bool showProtected)
    {
        if (!showHidden && i.Entry.IsHidden) return false;
        if (!showProtected && i.Entry.IsProtectedSystem) return false;
        return filter.Length == 0 || i.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
    }

    public void SetSort(SortColumn column, bool? ascending = null) =>
        SortBy(column == SortColumn.Property ? _sortProperty ?? ColumnCatalog.Name : column.ToString(), ascending);

    /// <summary>Sorts by a column id: a built-in column ("Name", "Size"…) or a shell property ("System.Music.Artist").</summary>
    public void SortBy(string columnId, bool? ascending = null)
    {
        var column = Enum.TryParse<SortColumn>(columnId, out var builtIn) && builtIn != SortColumn.Property ? builtIn : SortColumn.Property;
        var property = column == SortColumn.Property ? columnId : null;
        var same = column == _sortColumn && property == _sortProperty;
        // Dates, sizes and properties start newest/biggest first, like Explorer; names and types A→Z.
        _sortAscending = ascending ?? (same ? !_sortAscending : column is SortColumn.Name or SortColumn.Type);
        _sortColumn = column;
        _sortProperty = property;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAscending));
        OnPropertyChanged(nameof(SortKey));
        RememberView();
        _ = ApplyViewAsync(keepSelection: true);
    }

    /// <summary>Id of the column the list is sorted by.</summary>
    public string SortKey => _sortColumn == SortColumn.Property ? _sortProperty ?? ColumnCatalog.Name : _sortColumn.ToString();

    // ---------- Columns and groups ----------

    public IReadOnlyList<string> Columns => _columns;

    public string? GroupBy => _groupBy;

    public double ColumnWidth(string id) =>
        _columnWidths.TryGetValue(id, out var w) ? w : ColumnCatalog.Get(id)?.Width ?? 120;

    public void SetColumnWidth(string id, double width)
    {
        if (_columnWidths.TryGetValue(id, out var old) && Math.Abs(old - width) < 1) return;
        _columnWidths[id] = width;
        _columnsRemembered = true;
        RememberView();
    }

    public void SetColumns(IEnumerable<string> ids)
    {
        var list = ids.Where(id => ColumnCatalog.Get(id) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        list.Remove(ColumnCatalog.Name);
        list.Insert(0, ColumnCatalog.Name); // Name always stays, first
        _columns = list;
        _columnsRemembered = true;
        OnPropertyChanged(nameof(Columns));
        RememberView();
    }

    public void ResetColumns()
    {
        _columnsRemembered = false;
        _columnWidths.Clear();
        _columns = ColumnCatalog.DefaultFor(_content, Kind);
        OnPropertyChanged(nameof(Columns));
        RememberView();
    }

    public void SetGroupBy(string? columnId)
    {
        if (_groupBy == columnId) return;
        _groupBy = columnId;
        OnPropertyChanged(nameof(GroupBy));
        RememberView();
        _ = ApplyViewAsync(keepSelection: true);
    }


    // ---------- View mode ----------

    // Every search shares one remembered view (each query has its own URL).
    private string? FolderKey => _location?.IsSearch == true ? SearchLocation.Scheme
        : _location?.IsTagView == true ? TagView.Scheme
        : _location?.IsDuplicates == true ? DuplicateFinder.Scheme
        : _location?.FileSystemPath ?? _location?.ParsingName;

    private SortColumn DefaultSort => IsHome ? SortColumn.Default : SortColumn.Name;
    private string? DefaultGroup => IsHome || _location?.IsDuplicates == true ? ColumnCatalog.Section : null;

    private ItemViewModel NewItem(FolderEntry entry) => new(entry) { ImageSize = ViewModes.ImageSize(_viewMode) };

    /// <summary>The user picked a layout: use it and remember it for this folder.</summary>
    public void SetViewMode(ViewMode mode)
    {
        ApplyViewMode(mode);
        RememberView();
    }

    /// <summary>Forget the user's choices for this folder and go back to the automatic, content-based view.</summary>
    public void ResetView()
    {
        if (FolderKey is { } key) FolderViewStore.Remove(key);
        _viewIsRemembered = false;
        _columnsRemembered = false;
        _columnWidths.Clear();
        OnPropertyChanged(nameof(ViewIsRemembered));
        _sortColumn = DefaultSort;
        _sortProperty = null;
        _sortAscending = true;
        _groupBy = DefaultGroup;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAscending));
        OnPropertyChanged(nameof(SortKey));
        OnPropertyChanged(nameof(GroupBy));
        ChooseViewFromContent();
        _ = ApplyViewAsync(keepSelection: true);
    }

    private void ApplyViewMode(ViewMode mode)
    {
        if (_viewMode == mode) return;
        _viewMode = mode;
        var size = ViewModes.ImageSize(mode);
        foreach (var i in _all) i.ImageSize = size;
        OnPropertyChanged(nameof(ViewMode));
    }

    private void ApplyRememberedView()
    {
        var saved = FolderKey is { } key ? FolderViewStore.Get(key) : null;
        _viewIsRemembered = saved is not null;
        OnPropertyChanged(nameof(ViewIsRemembered));
        _content = FolderContent.General;
        _sortColumn = saved?.Sort ?? DefaultSort;
        _sortProperty = saved?.SortProperty;
        if (_sortColumn == SortColumn.Property && _sortProperty is null) _sortColumn = SortColumn.Name;
        if (_sortColumn == SortColumn.Default && !IsHome) _sortColumn = SortColumn.Name;
        _sortAscending = saved?.Ascending ?? true;
        _groupBy = saved is null ? DefaultGroup : saved.GroupBy;
        if (_groupBy == ColumnCatalog.Section && !IsHome && _location?.IsDuplicates != true) _groupBy = null;
        _columnWidths.Clear();
        List<string>? columns = null;
        if (saved?.Columns is { Count: > 0 } stored)
        {
            columns = [];
            foreach (var entry in stored)
            {
                var parts = entry.Split('|');
                if (ColumnCatalog.Get(parts[0]) is null) continue;
                columns.Add(parts[0]);
                if (parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w))
                    _columnWidths[parts[0]] = w;
            }
        }
        _columnsRemembered = columns is not null;
        SetColumnsQuietly(columns ?? ColumnCatalog.DefaultFor(FolderContent.General, Kind));
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAscending));
        OnPropertyChanged(nameof(SortKey));
        OnPropertyChanged(nameof(GroupBy));
        ApplyViewMode(saved?.Mode ?? ViewMode.Details);
    }

    private void SetColumnsQuietly(List<string> columns)
    {
        if (_columns.SequenceEqual(columns, StringComparer.OrdinalIgnoreCase)) return;
        _columns = columns;
        OnPropertyChanged(nameof(Columns));
    }

    private void ChooseViewFromContent()
    {
        _content = IsCollection || !AppSettings.Current.ContentAwareViews ? FolderContent.General : ContentViewRules.Classify(_all);
        OnPropertyChanged(nameof(Content));
        if (!_columnsRemembered) SetColumnsQuietly(ColumnCatalog.DefaultFor(_content, Kind));
        if (!_viewIsRemembered) ApplyViewMode(ContentViewRules.ModeFor(_content));
    }

    private void RememberView()
    {
        if (FolderKey is not { } key) return;
        FolderViewStore.Set(key, new FolderViewState
        {
            Mode = _viewMode,
            Sort = _sortColumn,
            SortProperty = _sortProperty,
            Ascending = _sortAscending,
            GroupBy = _groupBy,
            Columns = _columnsRemembered
                ? _columns.Select(id => _columnWidths.TryGetValue(id, out var w) ? $"{id}|{w.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : id).ToList()
                : null,
        });
        if (_viewIsRemembered) return;
        _viewIsRemembered = true;
        OnPropertyChanged(nameof(ViewIsRemembered));
    }
    public void OnSettingsChanged()
    {
        foreach (var i in _all) i.RefreshDisplay();
        if (IsHome) { ScheduleRefresh(); return; } // pins or the Recent files setting may have changed
        if (AppSettings.Current.ShowFolderSizes) RequestFolderSizes(_all);
        else
        {
            _sizesCts?.Cancel();
            foreach (var i in _all) i.SetFolderSize(null, false);
        }
        _ = ApplyViewAsync(keepSelection: true);
    }

    // ---------- Live updates ----------

    private string? _selectWhenCreated;

    /// <summary>The next item created with this name (by us) gets selected.</summary>
    public void SelectWhenCreated(string name) => _selectWhenCreated = name;

    /// <summary>The next item created with this name (by us) enters rename mode.</summary>
    public void RenameWhenCreated(string name) => _renameWhenCreated = name;

    /// <summary>The next item created in this folder within a few seconds enters rename mode (shell "New ▸" menu).</summary>
    public void RenameNextCreated() => _renameNextCreatedUntil = DateTime.UtcNow.AddSeconds(4);

    private void OnShellChange(ShellChange c)
    {
        GitStatus.Invalidate();
        var dir = _location?.FileSystemPath;
        if (dir is null || !_location!.IsFileSystemFolder)
        {
            if (c.Event is not (SHCNE.FREESPACE or SHCNE.UPDATEIMAGE)) ScheduleRefresh();
            return;
        }
        if (IsLoading) { _dirtyDuringLoad = true; return; }

        switch (c.Event)
        {
            case SHCNE.CREATE:
            case SHCNE.MKDIR:
                _ = AddOrUpdateAsync(dir, c.Path1);
                break;
            case SHCNE.DELETE:
            case SHCNE.RMDIR:
                if (SamePath(c.Path1, dir)) { _ = OnCurrentFolderGoneAsync(); return; }
                Remove(dir, c.Path1);
                break;
            case SHCNE.RENAMEITEM:
            case SHCNE.RENAMEFOLDER:
                if (SamePath(c.Path1, dir)) { _ = OnCurrentFolderRenamedAsync(c.Path2); return; }
                Remove(dir, c.Path1);
                _ = AddOrUpdateAsync(dir, c.Path2);
                break;
            case SHCNE.UPDATEITEM:
            case SHCNE.ATTRIBUTES:
                if (!SamePath(c.Path1, dir)) _ = AddOrUpdateAsync(dir, c.Path1);
                break;
            case SHCNE.UPDATEDIR:
                if (c.Path1 is null || SamePath(c.Path1, dir)) ScheduleRefresh();
                break;
        }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectChild(string dir, string? path) =>
        path is not null && SamePath(Path.GetDirectoryName(path.TrimEnd('\\')), dir);

    private async Task AddOrUpdateAsync(string dir, string? path)
    {
        if (!IsDirectChild(dir, path)) return;
        var entry = await Task.Run(() => FastFolderEnumerator.Stat(path!));
        if (!SamePath(_location?.FileSystemPath, dir)) return;
        if (entry is null) { Remove(dir, path); return; }

        var comparer = new ItemComparer(_sortColumn, _sortAscending, _sortProperty, _groupBy is not null);
        var settings = AppSettings.Current;
        if (_byName.TryGetValue(entry.Name, out var vm))
        {
            Items.Remove(vm);
            vm.Update(entry);
        }
        else
        {
            vm = NewItem(entry);
            _all.Add(vm);
            _byName[entry.Name] = vm;
        }

        // Groups are ranked over the whole list, so a grouped view is rebuilt rather than patched.
        if (_groupBy is not null) _ = ApplyViewAsync(keepSelection: true);
        else if (PassesFilter(vm, ListFilter, settings.ShowHiddenFiles, settings.ShowProtectedOsFiles))
            InsertSorted(vm, comparer);
        if (vm.IsFolder) RequestFolderSizes([vm], restart: false);
        OnPropertyChanged(nameof(ShowEmptyMessage));
        UpdateStatus();

        if (_selectWhenCreated is not null && string.Equals(_selectWhenCreated, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            _selectWhenCreated = null;
            SelectionRequested?.Invoke([vm]);
        }
        var renameByName = _renameWhenCreated is not null && string.Equals(_renameWhenCreated, entry.Name, StringComparison.OrdinalIgnoreCase);
        if (renameByName || DateTime.UtcNow < _renameNextCreatedUntil)
        {
            _renameWhenCreated = null;
            _renameNextCreatedUntil = default;
            SelectionRequested?.Invoke([vm]);
            RenameRequested?.Invoke(vm);
        }
    }

    private void InsertSorted(ItemViewModel vm, ItemComparer comparer)
    {
        int lo = 0, hi = Items.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (comparer.Compare(Items[mid], vm) <= 0) lo = mid + 1; else hi = mid;
        }
        Items.Insert(lo, vm);
    }

    private void Remove(string dir, string? path)
    {
        if (!IsDirectChild(dir, path)) return;
        var name = Path.GetFileName(path!.TrimEnd('\\'));
        if (!_byName.Remove(name, out var vm)) return;
        _all.Remove(vm);
        Items.Remove(vm);
        OnPropertyChanged(nameof(ShowEmptyMessage));
        UpdateStatus();
    }

    // tag views and duplicates aren't folders, so they hear about deletes from anywhere
    private void RemoveGone(string? path)
    {
        if (path is null || _isLoading) return;
        if (_location?.DuplicatesIn is { } root && !IsSameOrInside(path, root.TrimEnd('\\'))) return;
        var folder = path.TrimEnd('\\');
        var gone = _all.Where(i => i.Entry.Path is { } p && IsSameOrInside(p, folder)).ToList();
        if (gone.Count == 0) return;
        if (_location?.IsDuplicates == true)
        {
            // a copy with no twin left isn't a duplicate any more
            var left = _all.Except(gone).GroupBy(i => i.Entry.Section).Where(g => g.Count() == 1).SelectMany(g => g);
            gone.AddRange(left.ToList());
        }
        foreach (var vm in gone)
        {
            _all.Remove(vm);
            Items.Remove(vm);
            if (_byName.TryGetValue(vm.Name, out var known) && known == vm) _byName.Remove(vm.Name);
        }
        OnPropertyChanged(nameof(ShowEmptyMessage));
        UpdateStatus();
    }

    private void OnTagsChanged()
    {
        if (_location?.IsTagView == true) { ScheduleRefresh(); return; }
        foreach (var item in _all) item.ForgetCustomColumns();
    }

    private void OnCutChanged(IReadOnlySet<string> paths)
    {
        foreach (var i in _all)
            if (i.Entry.Path is { } p && paths.Contains(p.Length > 3 ? p.TrimEnd('\\') : p)) i.RefreshOpacity();
    }

    private void OnStarsChanged()
    {
        foreach (var i in _all) i.RefreshStar();
        if (IsHome) ScheduleRefresh();
    }

    private void ScheduleRefresh(int delayMs = 300)
    {
        _refreshDebounce?.Cancel();
        var cts = _refreshDebounce = new CancellationTokenSource();
        Task.Delay(delayMs, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ = RefreshAsync();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task OnCurrentFolderGoneAsync()
    {
        // Like Explorer: when the open folder is deleted, move to the closest folder that still exists.
        var path = _location?.FileSystemPath;
        while (path is not null && !Directory.Exists(path)) path = Path.GetDirectoryName(path);
        var target = path is null ? null : await ShellLocation.ResolveAsync(path);
        target ??= await ShellLocation.ResolveAsync(ShellLocation.Known.ThisPC);
        if (target is null) return;
        Notice?.Invoke("The folder was deleted or moved.");
        await NavigateAsync(target, NavMode.Refresh);
    }

    private async Task OnCurrentFolderRenamedAsync(string? newPath)
    {
        if (newPath is null) { await OnCurrentFolderGoneAsync(); return; }
        var target = await ShellLocation.ResolveAsync(newPath);
        if (target is not null) await NavigateAsync(target, NavMode.Refresh);
    }

    // ---------- Folder sizes ----------

    /// <summary>
    /// Shows cached folder sizes at once and queues the rest for measuring (visible items first).
    /// A new listing cancels whatever the previous one still had queued.
    /// </summary>
    private void RequestFolderSizes(IReadOnlyCollection<ItemViewModel> candidates, bool restart = true)
    {
        if (restart) { _sizesCts?.Cancel(); _pendingSizes = 0; }
        if (!AppSettings.Current.ShowFolderSizes || !FolderSizeService.IsMeasurable(_location?.FileSystemPath)) return;
        var cts = restart || _sizesCts is null ? _sizesCts = new CancellationTokenSource() : _sizesCts;
        var service = FolderSizeService.Instance;
        var wanted = candidates.Where(i => i.IsFolder && i.Entry.Path is not null).ToHashSet();
        var visible = Items.ToHashSet();
        var ordered = Items.Where(wanted.Contains).Concat(wanted.Where(i => !visible.Contains(i))).ToList();
        var measure = new List<ItemViewModel>();
        foreach (var item in ordered)
        {
            if (service.TryGetCached(item.Entry.Path!, out var size)) item.SetFolderSize(size, false);
            else
            {
                item.SetFolderSize(item.FolderSize, true);
                _pendingSizes++;
                measure.Add(item);
            }
        }
        if (ordered.Count > 0) _ = SizeFromIndexAsync(ordered, measure, cts.Token);
    }

    // the index service knows every folder's total at once; measure only what it can't answer
    private async Task SizeFromIndexAsync(List<ItemViewModel> items, List<ItemViewModel> measure, CancellationToken ct)
    {
        long[]? sizes = null;
        try { sizes = await IndexSearch.GetFolderSizesAsync(items.Select(i => i.Entry.Path!).ToList(), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { }
        if (ct.IsCancellationRequested) return;
        var pending = measure.ToHashSet();
        var answered = false;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (sizes is not null && sizes[i] >= 0)
            {
                item.SetFolderSize(sizes[i], false);
                if (pending.Remove(item)) { _pendingSizes--; answered = true; }
            }
        }
        if (answered && pending.Count == 0 && _pendingSizes == 0 && (_sortColumn == SortColumn.Size || _groupBy == ColumnCatalog.Size))
            _ = ApplyViewAsync(keepSelection: true);
        if (answered) UpdateStatus();
        foreach (var item in measure)
            if (pending.Contains(item)) _ = MeasureAsync(item, ct);
    }

    private async Task MeasureAsync(ItemViewModel item, CancellationToken ct)
    {
        var size = await FolderSizeService.Instance.GetAsync(item.Entry.Path!, ct);
        if (ct.IsCancellationRequested) return;
        item.SetFolderSize(size ?? item.FolderSize, false);
        if (_selectedItems.Contains(item)) UpdateStatus();
        if (--_pendingSizes == 0 && (_sortColumn == SortColumn.Size || _groupBy == ColumnCatalog.Size)) _ = ApplyViewAsync(keepSelection: true);
    }

    private void OnFolderSizesInvalidated(IReadOnlyCollection<string> paths)
    {
        if (_isLoading || _location?.FileSystemPath is null) return;
        var stale = _all.Where(i => i.IsFolder && i.Entry.Path is { } p && paths.Any(c => IsSameOrInside(c, p))).ToList();
        if (stale.Count > 0) RequestFolderSizes(stale, restart: false);
    }

    private static bool IsSameOrInside(string path, string folder) =>
        path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && (path.Length == folder.Length || path[folder.Length] == '\\');

    // ---------- Status ----------

    private void UpdateStatus()
    {
        ItemCountText = _isLoading && Items.Count == 0
            ? _location?.IsDuplicates == true ? "Looking for duplicates…" : IsSearch ? "Searching…" : "Loading…"
            : $"{Items.Count:N0} item{(Items.Count == 1 ? "" : "s")}";
        if (_location?.IsDuplicates == true && _all.Count > 0)
        {
            var sets = _all.GroupBy(i => i.Entry.Section).ToList();
            var spare = sets.Sum(g => (g.Count() - 1) * Math.Max(0, g.First().Entry.Size));
            ItemCountText = $"{_all.Count:N0} files in {sets.Count:N0} set{(sets.Count == 1 ? "" : "s")} · {ItemViewModel.FormatSizeShort(spare)} in extra copies"
                            + (_isLoading ? " · still looking" : "");
        }
        else if (_location?.IsDuplicates == true && !_isLoading) ItemCountText = "No duplicate files";
        if (!_isLoading && _location?.IndexResults is { } found)
        {
            if (found.Truncated) ItemCountText += $" (the best of {found.Total:N0} matches)";
            if (found.Scanning) ItemCountText += " · still reading some drives";
        }
        var sel = _selectedItems;
        if (sel.Count == 0) SelectionText = "";
        else
        {
            var text = $"{sel.Count:N0} item{(sel.Count == 1 ? "" : "s")} selected";
            if (sel.All(i => i.SortSize >= 0))
                text += "  " + ItemViewModel.FormatSizeShort(sel.Sum(i => i.SortSize));
            SelectionText = text;
        }
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(SelectionText));
    }

    public ItemViewModel? FindByName(string name) => _byName.GetValueOrDefault(name);

    public void Dispose()
    {
        _loadCts?.Cancel();
        _refreshDebounce?.Cancel();
        _sizesCts?.Cancel();
        FolderSizeWatcher.Changed -= OnFolderSizesInvalidated;
        Stars.Changed -= OnStarsChanged;
        Tags.Changed -= OnTagsChanged;
        CutItems.Changed -= OnCutChanged;
        _watch?.Dispose();
        _partner?.Dispose();
    }
}
