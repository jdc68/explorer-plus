using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ExplorerPlus.App.Infrastructure;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Navigation;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.ViewModels;

public enum OpenTarget { Here, NewTab, NewWindow }

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Func<IntPtr> _hwnd;
    private readonly Stack<string> _closedTabs = new();
    private TabViewModel? _currentTab;
    private TabViewModel? _activeTab;
    private bool _rightActive;
    private string? _notice;
    private CancellationTokenSource? _noticeCts;

    public MainViewModel(Func<IntPtr> hwnd)
    {
        _hwnd = hwnd;
        Sidebar = new SidebarViewModel();
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanCloseTab));
        AppSettings.Current.Changed += OnSettingsChanged;
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public SidebarViewModel Sidebar { get; }
    public PaneViewModel Pane { get; } = new();

    /// <summary>The tab picked in the tab strip. When it is split, its right pane is <see cref="RightTab"/>.</summary>
    public TabViewModel? CurrentTab
    {
        get => _currentTab;
        set => Select(value);
    }

    /// <summary>The right pane of the current tab (dual pane), or null.</summary>
    public TabViewModel? RightTab => _currentTab?.Partner;

    public bool IsDualPane => RightTab is not null;

    public bool IsRightActive => _rightActive && RightTab is not null;

    /// <summary>
    /// The view the commands act on: the current tab, or its right pane when that one is active. Setting a tab from
    /// the strip selects it; setting a right pane selects its tab and activates that pane.
    /// </summary>
    public TabViewModel? SelectedTab
    {
        get => IsRightActive ? RightTab : _currentTab;
        set => Select(value);
    }

    /// <summary>The pane that isn't active, when the tab is split.</summary>
    public TabViewModel? OtherPane => IsDualPane ? (IsRightActive ? _currentTab : RightTab) : null;

    /// <summary>The tab-strip tab a view belongs to (itself, or the tab whose right pane it is).</summary>
    public TabViewModel? StripTabOf(TabViewModel? view) =>
        view is null ? null : Tabs.Contains(view) ? view : Tabs.FirstOrDefault(t => t.Partner == view);

    private void Select(TabViewModel? value)
    {
        if (value is not null && !Tabs.Contains(value) && StripTabOf(value) is { } owner)
        {
            SetCurrent(owner);
            _rightActive = true;
        }
        else SetCurrent(value); // a split tab keeps whichever side was active
        RefreshActive();
    }

    private void SetCurrent(TabViewModel? tab)
    {
        if (ReferenceEquals(_currentTab, tab)) return;
        if (_currentTab is not null) _currentTab.PropertyChanged -= OnCurrentTabPropertyChanged;
        _currentTab = tab;
        if (tab is not null) tab.PropertyChanged += OnCurrentTabPropertyChanged;
        OnPropertyChanged(nameof(CurrentTab));
        OnPropertyChanged(nameof(RightTab));
        OnPropertyChanged(nameof(IsDualPane));
    }

    private void OnCurrentTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TabViewModel.Partner)) return;
        OnPropertyChanged(nameof(RightTab));
        OnPropertyChanged(nameof(IsDualPane));
        RefreshActive();
    }

    private void RefreshActive()
    {
        var active = SelectedTab;
        if (ReferenceEquals(active, _activeTab))
        {
            OnPropertyChanged(nameof(IsRightActive));
            return;
        }
        if (_activeTab is not null) _activeTab.PropertyChanged -= OnTabPropertyChanged;
        _activeTab = active;
        if (active is not null) active.PropertyChanged += OnTabPropertyChanged;
        OnPropertyChanged(nameof(IsRightActive));
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(WindowTitle));
        Sidebar.SyncSelection(active?.Location);
        Pane.Attach(active);
    }

    /// <summary>Makes the left or the right pane the one commands act on (a click or focus in it).</summary>
    public void ActivatePane(bool right)
    {
        if (right && RightTab is null) return;
        if (_rightActive == right && ReferenceEquals(SelectedTab, _activeTab)) return;
        _rightActive = right;
        RefreshActive();
    }

    /// <summary>Ctrl+\: splits the current tab in two panes (the new one shows the same folder), or closes the right pane.</summary>
    public async Task ToggleDualPaneAsync()
    {
        if (_currentTab is not { } tab) return;
        if (tab.Partner is { } partner)
        {
            _rightActive = false;
            tab.Partner = null;
            Unsubscribe(partner);
            partner.Dispose();
            RefreshActive();
            return;
        }
        var right = new TabViewModel();
        Subscribe(right);
        tab.Partner = right;
        _rightActive = true;
        RefreshActive();
        var start = tab.Location ?? await ShellLocation.ResolveAsync(AppSettings.Current.StartLocation);
        if (start is not null) await right.NavigateAsync(start);
    }

    /// <summary>Opens a folder in the pane that isn't active, splitting the tab first if needed.</summary>
    public async Task OpenInOtherPaneAsync(ShellLocation location)
    {
        if (OtherPane is { } other)
        {
            await other.NavigateAsync(location);
            return;
        }
        if (_currentTab is not { } tab) return;
        var right = new TabViewModel();
        Subscribe(right);
        tab.Partner = right; // the left pane stays active
        await right.NavigateAsync(location);
    }

    public bool CanCloseTab => Tabs.Count > 1;

    public string WindowTitle => SelectedTab?.Location is { } l ? $"{l.DisplayName} - Explorer+" : "Explorer+";

    public bool ShowHiddenFiles
    {
        get => AppSettings.Current.ShowHiddenFiles;
        set { AppSettings.Current.ShowHiddenFiles = value; AppSettings.Current.NotifyChanged(); }
    }

    public bool ShowCheckboxes
    {
        get => AppSettings.Current.ShowCheckboxes;
        set { AppSettings.Current.ShowCheckboxes = value; AppSettings.Current.NotifyChanged(); }
    }

    public bool ShowFolderSizes
    {
        get => AppSettings.Current.ShowFolderSizes;
        set { AppSettings.Current.ShowFolderSizes = value; AppSettings.Current.NotifyChanged(); }
    }

    public bool ShowFileExtensions
    {
        get => AppSettings.Current.ShowFileExtensions;
        set { AppSettings.Current.ShowFileExtensions = value; AppSettings.Current.NotifyChanged(); }
    }

    public bool ShowProtectedOsFiles
    {
        get => AppSettings.Current.ShowProtectedOsFiles;
        set { AppSettings.Current.ShowProtectedOsFiles = value; AppSettings.Current.NotifyChanged(); }
    }

    /// <summary>Transient message shown in the status bar (errors, "Copied path").</summary>
    public string? Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    public void ShowNotice(string message)
    {
        Notice = message;
        _noticeCts?.Cancel();
        var cts = _noticeCts = new CancellationTokenSource();
        Task.Delay(5000, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Notice = null; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.Location) or nameof(TabViewModel.Title))
        {
            OnPropertyChanged(nameof(WindowTitle));
            Sidebar.SyncSelection(SelectedTab?.Location);
        }
    }

    private void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(ShowHiddenFiles));
        OnPropertyChanged(nameof(ShowFileExtensions));
        OnPropertyChanged(nameof(ShowProtectedOsFiles));
        OnPropertyChanged(nameof(ShowCheckboxes));
        OnPropertyChanged(nameof(ShowFolderSizes));
        foreach (var t in Tabs)
        {
            t.OnSettingsChanged();
            t.Partner?.OnSettingsChanged();
        }
    }

    private IntPtr Hwnd => _hwnd();

    // ---------- Tabs ----------

    public async Task<TabViewModel?> OpenTabAsync(string parsingName, bool activate = true, string? selectPath = null)
    {
        var location = await ShellLocation.ResolveAsync(parsingName);
        if (location is null)
        {
            ShowNotice($"Can't find \"{parsingName}\".");
            return null;
        }
        return await OpenTabAsync(location, activate, selectPath);
    }

    public async Task<TabViewModel> OpenTabAsync(ShellLocation location, bool activate = true, string? selectPath = null, int? index = null)
    {
        var tab = new TabViewModel();
        Subscribe(tab);
        if (index is { } i && i >= 0 && i <= Tabs.Count) Tabs.Insert(i, tab); else Tabs.Add(tab);
        if (activate || CurrentTab is null) CurrentTab = tab;
        await tab.NavigateAsync(location, selectPath: selectPath);
        return tab;
    }

    private void Subscribe(TabViewModel tab)
    {
        tab.Notice += ShowNotice;
        tab.PropertyChanged += OnAnyTabPropertyChanged;
        if (tab.Partner is { } right) Subscribe(right);
    }

    private void Unsubscribe(TabViewModel tab)
    {
        tab.Notice -= ShowNotice;
        tab.PropertyChanged -= OnAnyTabPropertyChanged;
        if (tab.Partner is { } right) Unsubscribe(right);
    }

    private static void OnAnyTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Location) && sender is TabViewModel tab) JumpLists.NoteVisited(tab.Location);
    }

    /// <summary>Takes a tab out of this window without disposing it: it is moving to another window.</summary>
    public void DetachTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        // Removing the selected tab makes the tab strip clear the selection through its binding first.
        var wasSelected = CurrentTab == tab;
        Unsubscribe(tab);
        Tabs.Remove(tab);
        if (wasSelected || CurrentTab is null) CurrentTab = Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : null;
    }

    /// <summary>Adds a tab that was dragged in from another window, keeping its history, view and items.</summary>
    public void AttachTab(TabViewModel tab, int index)
    {
        Subscribe(tab);
        Tabs.Insert(Math.Clamp(index, 0, Tabs.Count), tab);
        CurrentTab = tab;
    }

    /// <summary>Reorders a tab inside this window (tab drag).</summary>
    public void MoveTab(TabViewModel tab, int index)
    {
        var from = Tabs.IndexOf(tab);
        index = Math.Clamp(index, 0, Tabs.Count - 1);
        if (from < 0 || from == index) return;
        Tabs.Move(from, index);
        CurrentTab = tab;
    }

    public void CloseTab(TabViewModel tab)
    {
        if (Tabs.Count <= 1) return;
        var index = Tabs.IndexOf(tab);
        if (tab.Location is { } l) _closedTabs.Push(l.EditText);
        Tabs.Remove(tab);
        tab.Dispose();
        if (CurrentTab == tab || CurrentTab is null) CurrentTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    public void CloseOtherTabs(TabViewModel keep)
    {
        foreach (var t in Tabs.Where(t => t != keep).ToList()) CloseTab(t);
    }

    public async Task ReopenClosedTabAsync()
    {
        if (_closedTabs.Count > 0) await OpenTabAsync(_closedTabs.Pop());
    }

    public async Task DuplicateTabAsync(TabViewModel tab)
    {
        if (tab.Location is { } l) await OpenTabAsync(l, index: Tabs.IndexOf(tab) + 1);
    }

    public void SelectTabRelative(int delta)
    {
        if (Tabs.Count == 0 || CurrentTab is null) return;
        var i = (Tabs.IndexOf(CurrentTab) + delta + Tabs.Count) % Tabs.Count;
        CurrentTab = Tabs[i];
    }

    // A search tab comes back as the folder that was searched.
    public IReadOnlyList<string> SessionTabs => Tabs.Where(t => t.Location is not null).Select(t => (t.Location!.SearchScope ?? t.Location).EditText).ToList();

    // ---------- Navigation ----------

    public async Task NavigateAsync(string parsingName, OpenTarget target = OpenTarget.Here)
    {
        var location = await ShellLocation.ResolveAsync(parsingName);
        if (location is null)
        {
            ShowNotice($"Windows can't find \"{parsingName}\".");
            return;
        }
        await NavigateAsync(location, target);
    }

    public async Task NavigateAsync(ShellLocation location, OpenTarget target = OpenTarget.Here, string? selectPath = null)
    {
        if (!location.IsFolder)
        {
            // A file path typed into the address bar opens the file, as in Explorer.
            await LaunchAsync(() => ShellLauncher.OpenAsync(location.Pidl, Hwnd));
            return;
        }
        switch (target)
        {
            case OpenTarget.NewTab:
                await OpenTabAsync(location, activate: !KeyboardShift(), index: CurrentTab is null ? null : Tabs.IndexOf(CurrentTab) + 1);
                break;
            case OpenTarget.NewWindow:
                App.OpenWindow(location.EditText);
                break;
            default:
                if (SelectedTab is null) await OpenTabAsync(location, selectPath: selectPath);
                else await SelectedTab.NavigateAsync(location, selectPath: selectPath);
                break;
        }
    }

    private static bool KeyboardShift() => (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

    /// <summary>Handles Enter in the address bar: paths, shell: names, URLs and commands.</summary>
    public async Task NavigateFromAddressBarAsync(string text)
    {
        var current = SelectedTab?.Location?.FileSystemPath;
        var parsed = PathParser.Parse(text, current);
        switch (parsed.Kind)
        {
            case AddressKind.Location:
                await NavigateAsync(parsed.Text);
                break;
            case AddressKind.Url:
                await LaunchAsync(() => ShellLauncher.RunAsync(parsed.Text, null, null, Hwnd));
                break;
            case AddressKind.Command:
                var dir = current ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var exe = parsed.Text.ToLowerInvariant();
                if (exe is "wt" or "wt.exe" && parsed.Arguments is null)
                    await LaunchAsync(() => ShellLauncher.OpenTerminalAsync(dir, TerminalKind.WindowsTerminal, Hwnd));
                else
                    await LaunchAsync(() => ShellLauncher.RunAsync(parsed.Text, parsed.Arguments, dir, Hwnd));
                break;
        }
    }

    // ---------- Opening items ----------

    public async Task OpenItemsAsync(IReadOnlyList<ItemViewModel> items, OpenTarget target = OpenTarget.Here)
    {
        if (items.Count == 0) return;
        // Several folders: open each in its own tab (Explorer would open a window per folder).
        if (items.Count > 1 && target == OpenTarget.Here) target = OpenTarget.NewTab;

        foreach (var item in items)
        {
            var location = await GetBrowseTargetAsync(item);
            if (location is not null) await NavigateAsync(location, target);
            else if (target == OpenTarget.Here || !item.IsFolder)
                await LaunchAsync(() => ShellLauncher.OpenAsync(item.Entry, Hwnd));
        }
    }

    /// <summary>The location to browse into for an item, or null if the item should be launched.</summary>
    private static async Task<ShellLocation?> GetBrowseTargetAsync(ItemViewModel item)
    {
        var e = item.Entry;
        if (e.IsFolder)
            return e.Path is not null ? await ShellLocation.ResolveAsync(e.Path) : await ShellLocation.FromPidlAsync(e.GetPidl());
        if (e.IsBrowsableFile && Associations.IsBrowsedByShell(e.Extension))
            return await ShellLocation.FromPidlAsync(await e.GetPidlAsync());
        if (e.Path is not null && e.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return await LinkResolver.GetFolderTargetAsync(e.Path);
        return null;
    }

    public async Task RunVerbAsync(IReadOnlyList<ItemViewModel> items, string verb)
    {
        foreach (var item in items) await LaunchAsync(() => ShellLauncher.OpenAsync(item.Entry, Hwnd, verb));
    }

    public async Task OpenTerminalAsync(bool elevated = false)
    {
        var dir = SelectedTab?.Location?.FileSystemPath;
        if (dir is null) { ShowNotice("A terminal can only be opened in a file-system folder."); return; }
        await LaunchAsync(() => ShellLauncher.OpenTerminalAsync(dir, TerminalKind.Default, Hwnd, elevated));
    }

    private async Task LaunchAsync(Func<Task> launch)
    {
        try { await launch(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowNotice(ex.Message); }
    }

    // ---------- File operations ----------

    public async Task NewFolderAsync()
    {
        var tab = SelectedTab;
        var location = tab?.Location;
        if (tab is null || location?.FileSystemPath is null) { ShowNotice("You can't create a folder here."); return; }
        var name = FileOperations.UniqueName(location.FileSystemPath, "New folder");
        tab.RenameWhenCreated(name);
        var log = new FileOpLog();
        var result = await FileOperations.NewFolderAsync(location.Pidl, name, Hwnd, log);
        if (result == FileOpResult.Failed) ShowNotice("Couldn't create the folder.");
        UndoService.PushFrom(log, FileOpKind.New, "New folder");
    }

    public async Task NewTextFileAsync()
    {
        var tab = SelectedTab;
        var location = tab?.Location;
        if (tab is null || location?.FileSystemPath is null) return;
        var name = FileOperations.UniqueName(location.FileSystemPath, "New Text Document", ".txt");
        tab.RenameWhenCreated(name);
        var log = new FileOpLog();
        await FileOperations.NewFileAsync(location.Pidl, name, Hwnd, log);
        UndoService.PushFrom(log, FileOpKind.New, "New");
    }

    public async Task DeleteAsync(IReadOnlyList<ItemViewModel> items, bool permanent)
    {
        if (items.Count == 0) return;
        // Deleting from the Recycle Bin is always for good.
        if (SelectedTab?.IsRecycleBin == true) permanent = true;
        var pidls = await Task.WhenAll(items.Select(i => i.Entry.GetPidlAsync()));
        var (log, progress) = TrackedLog();
        using (progress) await FileOperations.DeleteAsync(pidls, Hwnd, permanent, log: log);
        if (!permanent) UndoService.PushFrom(log, FileOpKind.Delete, "Delete");
    }

    public async Task<bool> RenameAsync(ItemViewModel item, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == item.Name) return false;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowNotice("A file name can't contain any of the following characters: \\ / : * ? \" < > |");
            return false;
        }
        var pidl = await item.Entry.GetPidlAsync();
        var log = new FileOpLog();
        var result = await FileOperations.RenameAsync(pidl, newName, Hwnd, log);
        UndoService.PushFrom(log, FileOpKind.Rename, "Rename");
        if (log.TopLevel(FileOpKind.Rename).FirstOrDefault() is { Source: { } from, Result: { } to }) Stars.Rename(from, to);
        return result == FileOpResult.Succeeded;
    }

    /// <summary>Applies a batch rename planned in the Batch rename window; one undo step reverts all of it.</summary>
    public async Task BatchRenameAsync(IReadOnlyList<BatchRenamePlan> plan)
    {
        var work = plan.Where(p => p.IsChanged && p.Error is null).Select(p => (p.Path, p.NewName)).ToList();
        if (work.Count == 0) return;
        var tab = SelectedTab;
        BatchRenameResult result;
        try { result = await BatchRename.ApplyAsync(work, Hwnd); }
        catch (Exception ex) { ShowNotice(ex.Message); return; }
        foreach (var (now, oldName) in result.Renamed)
            Stars.Rename(Path.Combine(Path.GetDirectoryName(now) ?? "", oldName), now);
        if (result.Renamed.Count > 0) UndoService.Push(new BatchRenameRecord(result.Renamed));
        ShowNotice(result.Renamed.Count == work.Count
            ? $"Renamed {work.Count} item{(work.Count == 1 ? "" : "s")}."
            : $"Renamed {result.Renamed.Count} of {work.Count} items.");
        // Search results don't get change notifications.
        if (tab is { IsSearch: true }) await tab.RefreshAsync();
    }

    public async Task CopyToClipboardAsync(IReadOnlyList<ItemViewModel> items, bool cut)
    {
        if (items.Count == 0) return;
        var pidls = await Task.WhenAll(items.Select(i => i.Entry.GetPidlAsync()));
        if (!ShellClipboard.SetItems(pidls, cut, Hwnd)) ShowNotice("Couldn't copy to the clipboard.");
    }

    public async Task PasteAsync()
    {
        var location = SelectedTab?.Location;
        if (location is null) return;
        if (!Clipboard.ContainsFileDropList()) return;
        var files = Clipboard.GetFileDropList().Cast<string>().ToList();
        if (files.Count == 0) return;

        var move = false;
        if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
        {
            var effect = new BinaryReader(ms).ReadInt32();
            move = (effect & 2) != 0 && (effect & 1) == 0;
        }

        var result = await TransferAsync(files, location.Pidl, location.FileSystemPath, location.DisplayName, move);
        if (move && result == FileOpResult.Succeeded) Clipboard.Clear();
    }

    /// <summary>
    /// Copies or moves files and folders into a folder through the transfer queue, with undo. Returns null when there
    /// was nothing to do or the queued job was cancelled before it started.
    /// </summary>
    public async Task<FileOpResult?> TransferAsync(IReadOnlyList<string> files, Pidl destination, string? destinationPath,
        string destinationName, bool move)
    {
        if (files.Count == 0) return null;
        var sameFolder = destinationPath is not null &&
                         files.All(f => string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), destinationPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (move && sameFolder) return null; // moving onto itself: nothing to do, as in Explorer

        var single = Path.GetFileName(files[0].TrimEnd('\\')) is { Length: > 0 } n ? n : files[0];
        var what = files.Count == 1 ? $"\u201c{single}\u201d" : $"{files.Count} items";
        var result = FileOpResult.Failed;
        var ran = await TransferQueue.Enqueue($"{(move ? "Move" : "Copy")} {what}", $"to {destinationName}", async () =>
        {
            try
            {
                var sources = await Task.WhenAll(files.Select(f => Core.Threading.StaWorkerPool.General.Run(() => ShellNative.Parse(f))));
                // The window that queued the job may have closed while it waited; the dialogs then go without an owner.
                var owner = User32.IsWindow(Hwnd) ? Hwnd : IntPtr.Zero;
                var (log, progress) = TrackedLog();
                using (progress)
                    result = move
                        ? await FileOperations.MoveAsync(sources, destination, owner, log)
                        : await FileOperations.CopyAsync(sources, destination, owner, renameOnCollision: sameFolder, log: log);
                // A cancelled transfer may still have done part of the work; that part is undoable too.
                UndoService.PushFrom(log, move ? FileOpKind.Move : FileOpKind.Copy, move ? "Move" : "Copy");
            }
            catch (Exception ex) { ShowNotice(ex.Message); }
        });
        return ran ? result : null;
    }

    /// <summary>Alt+F5 / Alt+F6 in dual pane: copies or moves the selection into the folder shown in the other pane.</summary>
    public async Task TransferToOtherPaneAsync(IReadOnlyList<ItemViewModel> items, bool move)
    {
        if (OtherPane is not { } other) { ShowNotice("Split the tab into two panes first (Ctrl+\\)."); return; }
        if (other.Location is not { IsFileSystemFolder: true } target) { ShowNotice("The other pane isn't showing a folder you can copy to."); return; }
        var files = items.Select(i => i.Entry.Path).OfType<string>().ToList();
        if (files.Count == 0) { ShowNotice(items.Count == 0 ? "Select the items first." : "Only files and folders can be copied to the other pane."); return; }
        await TransferAsync(files, target.Pidl, target.FileSystemPath, target.DisplayName, move);
    }

    public async Task UndoAsync()
    {
        var label = UndoService.UndoLabel;
        if (label is null) { ShowNotice("Nothing to undo."); return; }
        ShowNotice(await UndoService.UndoAsync(Hwnd) ? $"Undid {label.ToLowerInvariant()}." : $"Couldn't undo {label.ToLowerInvariant()}.");
    }

    public async Task RedoAsync()
    {
        var label = UndoService.RedoLabel;
        if (label is null) { ShowNotice("Nothing to redo."); return; }
        ShowNotice(await UndoService.RedoAsync(Hwnd) ? $"Redid {label.ToLowerInvariant()}." : $"Couldn't redo {label.ToLowerInvariant()}.");
    }

    public static bool CanPaste()
    {
        try { return Clipboard.ContainsFileDropList(); } catch { return false; }
    }

    public void CopyPaths(IReadOnlyList<ItemViewModel> items)
    {
        var paths = items.Count > 0
            ? items.Select(i => i.Entry.Path ?? i.Entry.Name).ToList()
            : SelectedTab?.Location is { } l ? [l.EditText] : [];
        if (paths.Count == 0) return;
        Clipboard.SetText(string.Join(Environment.NewLine, paths.Select(p => p.Contains(' ') ? $"\"{p}\"" : p)));
        ShowNotice(paths.Count == 1 ? "Path copied." : $"{paths.Count} paths copied.");
    }

    // ---------- Starred ----------

    /// <summary>Ctrl+D: stars the selected items (or this folder when nothing is selected), or unstars them.</summary>
    public void ToggleStar(IReadOnlyList<ItemViewModel> items)
    {
        var paths = items.Count > 0
            ? items.Select(i => i.Entry.Path).OfType<string>().ToList()
            : SelectedTab?.Location?.FileSystemPath is { } here ? [here] : [];
        if (paths.Count == 0) { ShowNotice("Only files and folders can be starred."); return; }
        var starred = Stars.Toggle(paths);
        var single = Path.GetFileName(paths[0].TrimEnd('\\')) is { Length: > 0 } n ? n : paths[0];
        var what = paths.Count == 1 ? $"\u201c{single}\u201d" : $"{paths.Count} items";
        ShowNotice(starred ? $"Starred {what}." : $"Removed {what} from Starred.");
    }

    // ---------- Home ----------

    /// <summary>Opens the folder an item lives in, with the item selected (Home and search results).</summary>
    public async Task OpenFileLocationAsync(ItemViewModel item, OpenTarget target = OpenTarget.Here)
    {
        if (item.Entry.Path is not { } path || Path.GetDirectoryName(path.TrimEnd('\\')) is not { } parent) return;
        var location = await ShellLocation.ResolveAsync(parent);
        if (location is null) { ShowNotice($"Windows can't find \u201c{parent}\u201d."); return; }
        if (target == OpenTarget.NewTab)
            await OpenTabAsync(location, selectPath: path, index: CurrentTab is null ? null : Tabs.IndexOf(CurrentTab) + 1);
        else await NavigateAsync(location, OpenTarget.Here, selectPath: path);
    }

    /// <summary>Takes items off Home's Recent files by deleting Windows' recent-item shortcuts (the files stay).</summary>
    public async Task RemoveFromRecentAsync(IReadOnlyList<ItemViewModel> items)
    {
        var links = items.Select(i => i.Entry.RecentLink).OfType<string>().ToList();
        if (links.Count == 0) return;
        await Task.Run(() =>
        {
            foreach (var link in links)
                try { File.Delete(link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        });
        if (SelectedTab is { IsHome: true } tab) await tab.RefreshAsync();
    }

    // ---------- Search ----------

    /// <summary>Enter in the search box: Windows Search in the current folder.</summary>
    public async Task<bool> SearchAsync(string query)
    {
        if (SelectedTab is not { } tab || string.IsNullOrWhiteSpace(query)) return false;
        if (!tab.CanSearch) { ShowNotice("Windows Search works in folders, drives and This PC."); return false; }
        if (await tab.SearchAsync(query.Trim())) return true;
        ShowNotice("Couldn't start the search.");
        return false;
    }

    // ---------- New ▸ templates ----------

    public async Task NewFromTemplateAsync(NewItemTemplate template)
    {
        var tab = SelectedTab;
        var location = tab?.Location;
        if (tab is null || location?.FileSystemPath is not { } dir || !location.IsFileSystemFolder) { ShowNotice("You can't create a file here."); return; }
        var name = FileOperations.UniqueName(dir, template.BaseName, template.Extension);
        // The shortcut wizard names the shortcut itself; everything else is renamed in place, as in Explorer.
        if (template.Kind != NewItemKind.Command) tab.RenameWhenCreated(name);
        var log = new FileOpLog();
        try
        {
            var (result, _, kind) = await FileOperations.CreateFromTemplateAsync(template, location.Pidl, dir, name, Hwnd, log);
            if (result == FileOpResult.Failed) ShowNotice($"Couldn't create the {template.TypeName.ToLowerInvariant()}.");
            UndoService.PushFrom(log, kind, "New");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowNotice(ex.Message); }
    }

    // ---------- Compress / extract ----------

    private CancellationTokenSource? _archiveCts;

    public bool IsArchiveBusy => _archiveCts is not null;

    public void CancelArchive() => _archiveCts?.Cancel();

    public async Task CompressAsync(IReadOnlyList<ItemViewModel> items, ArchiveFormat format)
    {
        var sources = items.Select(i => i.Entry.Path).OfType<string>().ToList();
        if (sources.Count == 0 || _archiveCts is not null) return;
        var tab = SelectedTab;
        var here = tab?.Location?.IsFileSystemFolder == true ? tab.Location.FileSystemPath : null;
        // From Home or search results the archive goes next to the first item.
        var folder = here ?? Path.GetDirectoryName(sources[0].TrimEnd('\\'))!;
        var baseName = Archives.BaseName(Archives.SuggestName(sources, format));
        var target = Path.Combine(folder, FileOperations.UniqueName(folder, baseName, Archives.Extension(format)));
        await RunArchiveJobAsync($"Compressing to \u201c{Path.GetFileName(target)}\u201d", async (progress, ct) =>
        {
            await Archives.CompressAsync(sources, target, format, progress, ct);
            return target;
        }, created =>
        {
            UndoService.Push(new CreatedRecord([created], "Compress"));
            if (here is not null && tab is not null) tab.RenameWhenCreated(Path.GetFileName(created));
        });
    }

    public async Task ExtractAsync(IReadOnlyList<ItemViewModel> items)
    {
        var archives = items.Select(i => i.Entry.Path).Where(Archives.IsExtractable).OfType<string>().ToList();
        if (archives.Count == 0 || _archiveCts is not null) return;
        foreach (var archive in archives)
        {
            var tab = SelectedTab;
            await RunArchiveJobAsync($"Extracting \u201c{Path.GetFileName(archive)}\u201d", (progress, ct) => Archives.ExtractAsync(archive, progress, ct), created =>
            {
                UndoService.Push(new CreatedRecord([created], "Extract"));
                tab?.SelectWhenCreated(Path.GetFileName(created.TrimEnd(Path.DirectorySeparatorChar)));
            });
        }
    }

    private async Task RunArchiveJobAsync(string label, Func<IProgress<double>, CancellationToken, Task<string>> job, Action<string> done)
    {
        var cts = _archiveCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(IsArchiveBusy));
        var lastPercent = -1;
        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100);
            if (percent == lastPercent || cts.IsCancellationRequested) return;
            lastPercent = percent;
            ShowNotice($"{label}… {percent}%");
            if (_archiveCts == cts) ReportProgress(cts, p);
        });
        ShowNotice(label + "…");
        try
        {
            var created = await job(progress, cts.Token);
            ShowNotice($"Created \u201c{Path.GetFileName(created.TrimEnd('\\'))}\u201d.");
            done(created);
        }
        catch (OperationCanceledException) { ShowNotice("Cancelled."); }
        catch (Exception ex) { ShowNotice(ex.Message); }
        finally
        {
            _archiveCts = null;
            EndProgress(cts);
            cts.Dispose();
            OnPropertyChanged(nameof(IsArchiveBusy));
        }
    }

    // ---------- Taskbar progress ----------

    private readonly Dictionary<object, double> _progress = [];

    /// <summary>Share of work done by this window's running operations (0…1), or null when nothing runs. Shown on the taskbar button.</summary>
    public double? Progress => _progress.Count == 0 ? null : _progress.Values.Average();

    private void ReportProgress(object key, double value)
    {
        _progress[key] = value;
        OnPropertyChanged(nameof(Progress));
    }

    private void EndProgress(object key)
    {
        if (_progress.Remove(key)) OnPropertyChanged(nameof(Progress));
    }

    /// <summary>A log for a file operation whose progress shows on the taskbar until the returned scope ends.</summary>
    private (FileOpLog Log, IDisposable Scope) TrackedLog()
    {
        var log = new FileOpLog();
        var key = new object();
        var finished = false; // only touched on the UI thread
        var dispatcher = Dispatcher.CurrentDispatcher;
        log.Progress += p => dispatcher.BeginInvoke(() => { if (!finished) ReportProgress(key, p); });
        return (log, new Scope(() => { finished = true; EndProgress(key); }));
    }

    private sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }

    // ---------- Recycle Bin and network ----------

    public void EmptyRecycleBin()
    {
        var (count, _) = RecycleBin.Query();
        if (count == 0) { ShowNotice("The Recycle Bin is already empty."); return; }
        RecycleBin.Empty(Hwnd);
    }

    /// <summary>Puts items from the Recycle Bin back where they were deleted from (the bin's own Restore verb).</summary>
    public void Restore(IReadOnlyList<ItemViewModel> items)
    {
        if (items.Count == 0) return;
        try
        {
            var pidls = items.Select(i => i.Entry.GetPidl()).ToList();
            using var menu = ShellContextMenu.ForItems(pidls, Hwnd, null);
            if (menu?.InvokeVerb("undelete", Hwnd) != true) ShowNotice("Couldn't restore the items.");
        }
        catch (Exception ex) { ShowNotice(ex.Message); }
    }

    public void MapNetworkDrive() => NetworkDrives.ShowMapDialog(Hwnd);

    public void DisconnectNetworkDrive() => NetworkDrives.ShowDisconnectDialog(Hwnd);

    public void Dispose()
    {
        AppSettings.Current.Changed -= OnSettingsChanged;
        foreach (var t in Tabs) t.Dispose();
        Sidebar.Dispose();
    }
}
