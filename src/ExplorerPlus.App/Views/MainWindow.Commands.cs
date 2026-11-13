using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ExplorerPlus.App.Services;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Core.Navigation;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Views;

// M5: command palette (Ctrl+K or Ctrl+Shift+P), Quick Look (Space) and batch rename.
public partial class MainWindow
{
    private const int PaletteLimit = 80;
    private List<PaletteEntry> _paletteEntries = [];
    private QuickLookWindow? _quickLook;
    private long _lastTypeAhead;

    // ================= Command palette =================

    private void ShowPalette()
    {
        if (PalettePopup.IsOpen) { PalettePopup.IsOpen = false; return; }
        _paletteEntries = BuildPaletteEntries();
        PalettePopup.HorizontalOffset = Math.Max(0, (RootGrid.ActualWidth - 672) / 2);
        PalettePopup.VerticalOffset = 36;
        PaletteBox.Text = "";
        FilterPalette();
        PalettePopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { PaletteBox.Focus(); Keyboard.Focus(PaletteBox); });
    }

    private void OnPaletteClosed(object? sender, EventArgs e)
    {
        PaletteList.ItemsSource = null;
        _paletteEntries = [];
    }

    private void OnPaletteTextChanged(object sender, TextChangedEventArgs e) => FilterPalette();

    private void FilterPalette()
    {
        var query = PaletteBox.Text.Trim();
        List<PaletteEntry> shown;
        if (query.Length == 0) shown = _paletteEntries.Take(PaletteLimit * 3).ToList();
        else
        {
            shown = [];
            foreach (var entry in _paletteEntries)
            {
                var score = FuzzyMatch.Score(query, entry.Title);
                if (score is null && FuzzyMatch.Score(query, entry.Detail) is { } d) score = d / 4; // a path matched, not the name
                if (score is null) continue;
                entry.Score = score.Value;
                shown.Add(entry);
            }
            shown = shown.OrderByDescending(p => p.Score).Take(PaletteLimit).ToList();
        }
        PaletteList.ItemsSource = shown;
        PaletteList.SelectedIndex = shown.Count > 0 ? 0 : -1;
        PaletteEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown.Count > 0) PaletteList.ScrollIntoView(shown[0]);
    }

    private void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        var count = PaletteList.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                PalettePopup.IsOpen = false;
                FocusList();
                break;
            case Key.Enter:
                e.Handled = true;
                RunPaletteEntry(PaletteList.SelectedItem as PaletteEntry);
                break;
            case Key.Down or Key.Up or Key.PageDown or Key.PageUp when count > 0:
            {
                e.Handled = true;
                var step = e.Key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 10, _ => -10 };
                var i = PaletteList.SelectedIndex + step;
                i = e.Key is Key.Down or Key.Up ? (i + count) % count : Math.Clamp(i, 0, count - 1);
                PaletteList.SelectedIndex = i;
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                break;
            }
        }
    }

    private void OnPaletteListClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: PaletteEntry entry }) RunPaletteEntry(entry);
    }

    private void RunPaletteEntry(PaletteEntry? entry)
    {
        PalettePopup.IsOpen = false;
        FocusList();
        if (entry is null) return;
        // After the popup is gone and the list has focus, so commands that act on the list or open menus behave as from a key.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            try { entry.Run(); }
            catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
        });
    }

    private async Task SearchEverywhereAsync()
    {
        if (await ViewModel.OpenSearchEverywhereAsync() is null) return;
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    // ================= Workspaces =================

    private MenuItem WorkspacesMenu()
    {
        var menu = new MenuItem { Header = "Workspaces", Icon = new TextBlock { Text = Glyphs.Workspace, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"] } };
        var all = Workspaces.All;
        foreach (var w in all)
        {
            var item = MenuEntry(w.Name, null, () => App.OpenWorkspace(w), null);
            item.ToolTip = Workspaces.Describe(w);
            menu.Items.Add(item);
        }
        if (all.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Save these tabs as a workspace…", null, SaveWorkspace, null));
        if (all.Count > 0)
        {
            var delete = new MenuItem { Header = "Delete" };
            foreach (var w in all) delete.Items.Add(MenuEntry(w.Name, null, () => DeleteWorkspace(w), null));
            menu.Items.Add(delete);
        }
        return menu;
    }

    private void SaveWorkspace()
    {
        var suggested = ViewModel.CurrentTab?.Location?.DisplayName ?? "Workspace";
        var name = PromptWindow.Ask(this, "Save workspace",
            "Saves the tabs in this window, with their right panes, under a name. Open it again from More ▸ Workspaces or the command palette (Ctrl+K).",
            suggested, "Save");
        if (name is null) return;
        var workspace = ViewModel.CaptureWorkspace(name);
        var tabs = workspace.Tabs.Count == 1 ? "1 tab" : $"{workspace.Tabs.Count} tabs";
        ViewModel.ShowNotice(Workspaces.Save(workspace) ? $"Updated workspace \"{name}\" ({tabs})." : $"Saved workspace \"{name}\" ({tabs}).");
    }

    private void DeleteWorkspace(Workspace workspace)
    {
        if (MessageBox.Show(this, $"Delete the workspace \"{workspace.Name}\"? Its folders are not touched.", "Delete workspace - Explorer+",
                MessageBoxButton.OKCancel, MessageBoxImage.None, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Workspaces.Delete(workspace.Name);
        ViewModel.ShowNotice($"Deleted workspace \"{workspace.Name}\".");
    }

    // ================= Tags =================

    private static List<string> TaggablePaths(IReadOnlyList<ItemViewModel> items) =>
        items.Select(i => i.Entry.Path).OfType<string>().Where(Path.IsPathFullyQualified).ToList();

    private MenuItem TagsMenu(IReadOnlyList<ItemViewModel> items)
    {
        var menu = new MenuItem { Header = "Tags", Icon = new TextBlock { Text = Glyphs.Tag, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"] } };
        var paths = TaggablePaths(items);
        var names = Tags.Names;
        foreach (var tag in names)
        {
            var item = MenuCheck(tag, paths.Count > 0 && paths.All(p => Tags.Has(p, tag)), () => ToggleTag(paths, tag));
            item.IsEnabled = paths.Count > 0;
            menu.Items.Add(item);
        }
        if (names.Count > 0) menu.Items.Add(new Separator());
        var add = MenuEntry("New tag…", null, () => NewTag(paths), null);
        add.IsEnabled = paths.Count > 0;
        menu.Items.Add(add);
        if (names.Count > 0)
        {
            var show = new MenuItem { Header = "Show tagged items" };
            foreach (var tag in names) show.Items.Add(MenuEntry(tag, null, () => ShowTag(tag, OpenTarget.Here), null));
            menu.Items.Add(show);
            var delete = new MenuItem { Header = "Delete tag" };
            foreach (var tag in names) delete.Items.Add(MenuEntry(tag, null, () => DeleteTag(tag), null));
            menu.Items.Add(delete);
        }
        return menu;
    }

    private void ShowSidebarTagMenu(SidebarFolder folder)
    {
        var tag = folder.Name;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(MenuEntry("Open", null, () => ShowTag(tag, OpenTarget.Here), null));
        menu.Items.Add(MenuEntry("Open in new tab", null, () => ShowTag(tag, OpenTarget.NewTab), null));
        menu.Items.Add(MenuEntry("Open in new window", null, () => App.OpenWindow(folder.Target), null));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Delete tag", null, () => DeleteTag(tag), "\uE74D"));
        menu.IsOpen = true;
    }

    private void ToggleTag(IReadOnlyList<string> paths, string tag)
    {
        if (paths.Count == 0) return;
        var what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : $"{paths.Count} items";
        ViewModel.ShowNotice(Tags.Toggle(paths, tag) ? $"Tagged {what} \"{tag}\"." : $"Removed the tag \"{tag}\" from {what}.");
    }

    private void NewTag(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var tag = PromptWindow.Ask(this, "Tag items",
            "Tagged items show in the sidebar under Tags and in the Tags column. Use a name you already have to add that tag.",
            "", "Tag");
        if (tag is null) return;
        Tags.Set(paths, tag, add: true);
        var what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : $"{paths.Count} items";
        ViewModel.ShowNotice($"Tagged {what} \"{tag}\".");
    }

    private async void ShowTag(string tag, OpenTarget target)
    {
        try { await ViewModel.NavigateAsync(TagView.Url(tag), target); }
        catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
    }

    private void DeleteTag(string tag)
    {
        var count = Tags.PathsWith(tag).Count;
        if (MessageBox.Show(this, $"Delete the tag \"{tag}\" from {count} item{(count == 1 ? "" : "s")}? The items themselves are not touched.",
                "Delete tag - Explorer+", MessageBoxButton.OKCancel, MessageBoxImage.None, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Tags.RemoveTag(tag);
        ViewModel.ShowNotice($"Deleted the tag \"{tag}\".");
    }

    // ================= Duplicates and folder compare =================

    // the selected folder, or the one on screen
    private string? FolderForTools()
    {
        if (Selection is [{ IsFolder: true, Entry.Path: { } picked }] && Path.IsPathFullyQualified(picked)) return picked;
        return Tab?.Location is { IsFileSystemFolder: true, FileSystemPath: { } here } ? here : null;
    }

    private async void FindDuplicates()
    {
        if (FolderForTools() is not { } folder)
        {
            ViewModel.ShowNotice("Open or select a folder to look for duplicate files in.");
            return;
        }
        try { await ViewModel.NavigateAsync(DuplicateFinder.Url(folder), OpenTarget.NewTab); }
        catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
    }

    private async void CompareFolders()
    {
        static string? FolderOf(TabViewModel? t) => t?.Location is { IsFileSystemFolder: true, FileSystemPath: { } p } ? p : null;
        var left = FolderOf(ViewModel.CurrentTab);
        var right = FolderOf(ViewModel.RightTab);
        if (right is null && Selection is [{ IsFolder: true, Entry.Path: { } picked }] && Path.IsPathFullyQualified(picked)) right = picked;
        if (CompareWindow.Ask(this, left, right) is not { } plan) return;
        try { await ViewModel.SyncAsync(plan); }
        catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
    }

    private static class Glyphs
    {
        public const string Tag = SidebarViewModel.TagGlyph;
        public const string Duplicates = "\uE8C8";
        public const string Compare = "\uE8AB";
        public const string Folder = "";
        public const string Tab = "";
        public const string Star = "";
        public const string Recent = "";
        public const string Drive = "";
        public const string Location = "";
        public const string Dual = "";
        public const string Workspace = "\uE8A9";
    }

    /// <summary>Every command, with its shortcut, plus the places you can jump to: tabs, the sidebar, recent folders.</summary>
    private List<PaletteEntry> BuildPaletteEntries()
    {
        var list = new List<PaletteEntry>();
        void Add(string title, Action run, string? gesture = null, string? glyph = null) => list.Add(new PaletteEntry(title, run, gesture, null, glyph));
        async void Await(Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex) { ViewModel.ShowNotice(ex.Message); }
        }

        var tab = Tab;
        var selection = Selection;
        var inFolder = tab?.Location?.IsFileSystemFolder == true;

        // File
        if (inFolder)
        {
            Add("New folder", () => Await(ViewModel.NewFolderAsync), "Ctrl+Shift+N", "");
            Add("New text document", () => Await(ViewModel.NewTextFileAsync), null, "");
        }
        if (selection.Count > 0)
        {
            Add("Cut", () => Await(() => ViewModel.CopyToClipboardAsync(selection, cut: true)), "Ctrl+X", "");
            Add("Copy", () => Await(() => ViewModel.CopyToClipboardAsync(selection, cut: false)), "Ctrl+C", "");
        }
        Add("Paste", () => Await(ViewModel.PasteAsync), "Ctrl+V", "");
        if (selection.Count == 1) Add("Rename", BeginRenameSelected, "F2", "");
        if (selection.Count(i => i.Entry.Path is not null) > 0)
            Add(selection.Count > 1 ? $"Rename {selection.Count} items (batch rename)…" : "Batch rename…", () => ShowBatchRename(selection),
                selection.Count > 1 ? "F2" : "Ctrl+Shift+F2", "");
        if (selection.Count > 0)
        {
            Add("Delete", () => Await(() => ViewModel.DeleteAsync(selection, permanent: false)), "Del", "");
            Add("Delete permanently", () => Await(() => ViewModel.DeleteAsync(selection, permanent: true)), "Shift+Del", "");
            Add("Open", () => Await(() => ViewModel.OpenItemsAsync(selection)), "Enter", "");
            Add("Open in new tab", () => Await(() => ViewModel.OpenItemsAsync(selection, OpenTarget.NewTab)), "Ctrl+Enter", Glyphs.Tab);
            Add("Quick Look", ToggleQuickLook, "Space", "");
        }
        Add("Copy as path", () => ViewModel.CopyPaths(selection), "Ctrl+Shift+C", "");
        Add(selection.Count > 0 && selection.All(i => i.IsStarred) ? "Remove from Starred" : "Add to Starred", () => ViewModel.ToggleStar(selection), "Ctrl+D", Glyphs.Star);
        Add("Properties", ShowProperties, "Alt+Enter", "");
        var paths = selection.Select(i => i.Entry.Path).OfType<string>().ToList();
        if (FolderForTools() is { } dupesIn)
            list.Add(new PaletteEntry("Find duplicate files", FindDuplicates, null, dupesIn, Glyphs.Duplicates));
        Add("Compare and sync folders…", CompareFolders, null, Glyphs.Compare);
        if (paths.Count == selection.Count && paths.Count > 0)
        {
            foreach (var format in new[] { ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.TarGz })
                Add($"Compress to {Archives.Label(format)}", () => Await(() => ViewModel.CompressAsync(selection, format)), null, "");
            if (paths.All(Archives.IsExtractable)) Add("Extract here", () => Await(() => ViewModel.ExtractAsync(selection)), null, "");
        }
        Add(UndoService.UndoLabel is { } u ? $"Undo {u.ToLowerInvariant()}" : "Undo", () => Await(ViewModel.UndoAsync), "Ctrl+Z", "");
        Add(UndoService.RedoLabel is { } r ? $"Redo {r.ToLowerInvariant()}" : "Redo", () => Await(ViewModel.RedoAsync), "Ctrl+Y", "");

        // Selection
        Add("Select all", () => FileList.SelectAll(), "Ctrl+A", "");
        Add("Select none", () => FileList.UnselectAll(), null, "");
        Add("Invert selection", InvertSelection, null, "");

        // Navigation
        if (tab is not null)
        {
            if (tab.CanGoBack) Add("Back", () => Await(tab.GoBackAsync), "Alt+Left", "");
            if (tab.CanGoForward) Add("Forward", () => Await(tab.GoForwardAsync), "Alt+Right", "");
            if (tab.CanGoUp) Add("Up to the parent folder", () => Await(tab.GoUpAsync), "Alt+Up", "");
            Add("Refresh", () => Await(tab.RefreshAsync), "F5", "");
        }
        Add("Edit the address", () => Address.BeginEdit(), "Ctrl+L", "");
        Add("Filter or search this folder", () => { FilterBox.Focus(); FilterBox.SelectAll(); }, "Ctrl+E", "");
        Add("Search all drives", () => Await(SearchEverywhereAsync), "Ctrl+Shift+F", "");
        Add("New tab", () => Await(() => ViewModel.OpenTabAsync(AppSettings.Current.StartLocation)), "Ctrl+T", Glyphs.Tab);
        if (ViewModel.CurrentTab is { } stripTab)
        {
            Add("Duplicate tab", () => Await(() => ViewModel.DuplicateTabAsync(stripTab)), null, Glyphs.Tab);
            Add("Close tab", () => CloseTab(stripTab), "Ctrl+W", "");
            if (ViewModel.Tabs.Count > 1) Add("Close other tabs", () => ViewModel.CloseOtherTabs(stripTab), null, "");
        }
        Add(ViewModel.IsDualPane ? "Close the right pane" : "Dual pane: split this tab", () => Await(ToggleDualPaneAsync), "Ctrl+\\", Glyphs.Dual);
        if (ViewModel.IsDualPane)
        {
            Add("Switch to the other pane", SwitchPane, "Tab", Glyphs.Dual);
            if (selection.Count > 0)
            {
                Add("Copy to the other pane", () => Await(() => ViewModel.TransferToOtherPaneAsync(selection, move: false)), "Alt+F5", "");
                Add("Move to the other pane", () => Await(() => ViewModel.TransferToOtherPaneAsync(selection, move: true)), "Alt+F6", "");
            }
            if (tab?.Location is { } here && ViewModel.OtherPane is { } other)
                Add("Show this folder in the other pane", () => Await(() => other.NavigateAsync(here)), null, Glyphs.Dual);
        }
        Add("Reopen closed tab", () => Await(ViewModel.ReopenClosedTabAsync), "Ctrl+Shift+T", Glyphs.Tab);
        if (ViewModel.Tabs.Count > 1)
        {
            Add("Next tab", () => ViewModel.SelectTabRelative(1), "Ctrl+Tab", Glyphs.Tab);
            Add("Previous tab", () => ViewModel.SelectTabRelative(-1), "Ctrl+Shift+Tab", Glyphs.Tab);
        }
        Add("New window", NewWindowHere, "Ctrl+N", "");
        Add("Full screen", ToggleFullScreen, "F11", "");

        // View
        if (tab is not null)
        {
            for (var i = 0; i < ViewModes.All.Length; i++)
            {
                var mode = ViewModes.All[i];
                Add($"View: {ViewModes.Label(mode)}", () => tab.SetViewMode(mode), $"Ctrl+Shift+{i + 1}", ViewModes.Glyph(mode));
            }
            if (tab.ViewIsRemembered) Add("View: Reset this folder's view", tab.ResetView, null, "");
        }
        Add(ViewModel.Pane.IsPreview ? "Hide the preview pane" : "Show the preview pane", () => ViewModel.Pane.Toggle(PaneMode.Preview), "Alt+P", "");
        Add(ViewModel.Pane.IsDetails ? "Hide the details pane" : "Show the details pane", () => ViewModel.Pane.Toggle(PaneMode.Details), "Alt+Shift+P", "");
        Add(ViewModel.ShowHiddenFiles ? "Hide hidden items" : "Show hidden items", () => ViewModel.ShowHiddenFiles = !ViewModel.ShowHiddenFiles, null, "");
        Add(ViewModel.ShowFileExtensions ? "Hide file name extensions" : "Show file name extensions", () => ViewModel.ShowFileExtensions = !ViewModel.ShowFileExtensions, null, "");
        Add(ViewModel.ShowCheckboxes ? "Hide item check boxes" : "Show item check boxes", () => ViewModel.ShowCheckboxes = !ViewModel.ShowCheckboxes, null, "");
        Add(ViewModel.ShowFolderSizes ? "Hide folder sizes" : "Show folder sizes", () => ViewModel.ShowFolderSizes = !ViewModel.ShowFolderSizes, null, "");
        Add("Zoom in", () => Zoom(1), "Ctrl++", "");
        Add("Zoom out", () => Zoom(-1), "Ctrl+-", "");

        // Tools
        if (tab?.Location?.FileSystemPath is not null)
        {
            Add("Open terminal here", () => Await(() => ViewModel.OpenTerminalAsync()), "Ctrl+Shift+`", "");
            Add("Open terminal here as administrator", () => Await(() => ViewModel.OpenTerminalAsync(elevated: true)), null, "");
        }
        Add("Map network drive…", ViewModel.MapNetworkDrive, null, "");
        Add("Disconnect network drive…", ViewModel.DisconnectNetworkDrive, null, "");
        Add("Empty Recycle Bin", ViewModel.EmptyRecycleBin, null, "");
        Add("Settings", ShowSettings, "Ctrl+,", "");
        Add("Exit Explorer+", App.ExitAll, null, "");

        // Workspaces
        list.Add(new PaletteEntry("Save these tabs as a workspace…", SaveWorkspace, null, null, Glyphs.Workspace));
        foreach (var w in Workspaces.All)
        {
            list.Add(new PaletteEntry($"Open workspace: {w.Name}", () => App.OpenWorkspace(w), null, Workspaces.Describe(w), Glyphs.Workspace));
            list.Add(new PaletteEntry($"Delete workspace: {w.Name}", () => DeleteWorkspace(w), null, null, Glyphs.Workspace));
        }

        // Tags
        var taggable = TaggablePaths(selection);
        var canTag = taggable.Count > 0 && taggable.Count == selection.Count;
        if (canTag) list.Add(new PaletteEntry("Tag selected items…", () => NewTag(taggable), null, null, Glyphs.Tag));
        foreach (var name in Tags.Names)
        {
            if (canTag)
                list.Add(new PaletteEntry($"Tag selected items: {name}", () => ToggleTag(taggable, name), null,
                    taggable.All(p => Tags.Has(p, name)) ? "Removes the tag" : null, Glyphs.Tag));
            list.Add(new PaletteEntry($"Delete tag: {name}", () => DeleteTag(name), null, null, Glyphs.Tag));
        }

        // Places: open tabs, then the sidebar's own entries, then recent folders.
        foreach (var t in ViewModel.Tabs.Where(t => t != tab && t.Location is not null))
            list.Add(new PaletteEntry($"Switch to tab: {t.Title}", () => ViewModel.SelectedTab = t, null, t.Location!.EditText, Glyphs.Tab));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in ViewModel.Sidebar.Roots)
            foreach (var place in header.Children.OfType<SidebarFolder>())
            {
                if (!seen.Add(place.Target)) continue;
                var glyph = place.Glyph ?? (place is SidebarDrive ? Glyphs.Drive : place.IsStarredEntry ? Glyphs.Star : place.FileSystemPath is null ? Glyphs.Location : Glyphs.Folder);
                var target = place.Target;
                list.Add(new PaletteEntry($"Go to {place.Name}", () => Await(() => ViewModel.NavigateAsync(target)), null,
                    place.FileSystemPath ?? header.Name, glyph));
            }
        foreach (var path in AppSettings.Current.RecentFolders.Where(seen.Add))
        {
            var name = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
            list.Add(new PaletteEntry($"Go to {name}", () => Await(() => ViewModel.NavigateAsync(path)), null, path, Glyphs.Recent));
        }
        return list;
    }

    // ================= Quick Look =================

    private void OnFileListTextInput(object sender, TextCompositionEventArgs e)
    {
        // Type-ahead jumps to names; a space typed right after letters belongs to the name, not to Quick Look.
        if (e.Text.Length > 0 && !char.IsWhiteSpace(e.Text[0])) _lastTypeAhead = Environment.TickCount64;
    }

    private bool IsTypingAhead => Environment.TickCount64 - _lastTypeAhead < 1000;

    private void ToggleQuickLook()
    {
        if (_quickLook is not null) { CloseQuickLook(); return; }
        if (Tab is not { } tab || tab.SelectedItems.Count == 0) return;
        _quickLook = new QuickLookWindow(this, tab);
        _quickLook.Closed += (_, _) => _quickLook = null;
        _quickLook.Show();
    }

    private void CloseQuickLook()
    {
        _quickLook?.Close();
        _quickLook = null;
    }

    // ================= Batch rename =================

    private async void ShowBatchRename(IReadOnlyList<ItemViewModel> items)
    {
        if (_renaming is not null) CommitRename(_renaming);
        var withPaths = items.Where(i => i.Entry.Path is not null).ToList();
        if (withPaths.Count == 0) { ViewModel.ShowNotice("Only files and folders can be renamed together."); return; }
        var plan = BatchRenameWindow.Ask(this, withPaths);
        if (plan.Count == 0) return;
        await ViewModel.BatchRenameAsync(plan);
    }
}
