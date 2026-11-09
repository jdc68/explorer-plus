# Explorer+ — Plan

A fast, keyboard-friendly file manager for Windows 11, built for work and everyday use.

---

## 1. Why Windows 11 Explorer feels bad (research summary)

| Problem | Root cause | What we do instead |
|---|---|---|
| Slow to open folders, "Working on it…" | Folder-type auto-discovery sniffs files to pick a view template; enumeration and UI are coupled | No content sniffing. Enumerate on a background thread, stream rows into a virtualized list in batches |
| Slow right-click menu | Every installed shell extension (7-Zip, antivirus, cloud clients) loads in-process before the menu shows; one bad DLL stalls everything. Win11 adds a second XAML menu layer ("Show more options") | Our own instant menu with common actions. Shell (`IContextMenu`) entries load lazily, with a timeout and a per-extension blocklist |
| General sluggishness after launch | Hybrid Win32 + WinUI/XAML shell; Microsoft's 2026 fixes mostly target launch time (preloading), not navigation | Lean UI, nothing heavy on the UI thread, measure everything |
| Freezes on network / disconnected drives | Synchronous I/O on unreachable paths | Every file-system call async, cancellable, with timeouts |
| OneDrive / cloud folders slow | Reading placeholder files can trigger download (hydration) and sync-status queries | Never read file contents to build the list; respect `RECALL_ON_DATA_ACCESS` attributes |
| Search is slow and unreliable | Windows Search indexer is content-oriented, partial, often out of date | Instant name search from the NTFS MFT + USN journal (how *Everything* works), plus instant type-to-filter in the current folder |
| Weak power-user tooling | No dual pane, no batch rename, no queued copies, no sync/compare, poor keyboard use, tabs added late and clumsy | All of these, built in |

Microsoft has acknowledged most of this (Feedback Hub's #1 Explorer request is "speed up common actions"), but the fixes are slow and incremental.

## 2. What the best alternatives get right

- **Directory Opus** (paid): dual pane, per-folder view formats, powerful batch rename, folder sync, queued copies, scripting. The power-user benchmark — but complex and dated-looking.
- **Total Commander / Double Commander**: keyboard-first dual pane; dated UI.
- **XYplorer**: portable, scripting, strong search.
- **OneCommander**: modern columns (Miller) view; licensing unclear.
- **Files** (open source, WinUI 3): looks native on Win11 but is widely called slower and heavier than Explorer. **Lesson: a modern look alone isn't enough; speed has to come first.**
- **Everything** (voidtools): instant search from MFT + USN journal. The search experience to match.

## 3. Design principles

1. **The UI thread never waits on the disk.** Every navigation is async and cancellable; you can navigate away mid-load.
2. **First rows visible in < 50 ms** on a local SSD folder; 100k-item folders scroll smoothly (virtualization).
3. **Lazy everything**: icons, thumbnails, sizes, metadata, shell menus load only for visible items, off-thread, cached.
4. **Keyboard-first, mouse-friendly**: every action has a shortcut and appears in a command palette.
5. **Sensible defaults**: extensions and hidden files visible, opens to a useful start page, no ads or cloud nagging.
6. **Plays well with Windows**: uses the real shell for recycle bin, undo-compatible operations, drag-and-drop with other apps, and the native context menu when you ask for it.

## 4. Scope: a full Explorer replacement first, power features second

Explorer+ replaces File Explorer, it doesn't patch it. Before any extra feature, it must do **everything Explorer does** and integrate with Windows and other apps at least as well.

### Core design: the Windows Shell namespace is the data model
Explorer's features (This PC, Recycle Bin, Network, phones (MTP), zip folders, WSL, Control Panel, shell extensions, thumbnails, preview handlers, property columns) all come from the shell namespace (`IShellItem` / `IShellFolder` / PIDLs). Building on it gives parity for free; plain paths would never get there.

Speed comes from a hybrid:
- **File-system folders** are listed with `FindFirstFileExW` (`FindExInfoBasic` + `LARGE_FETCH`) and streamed in batches. Shell objects are created only when needed (menus, properties, real icons).
- **Virtual folders** are listed with `IShellFolder::EnumObjects`.
- **All shell COM calls** run on background STA worker threads, never on the UI thread, and are cancelled on navigation.
- **Live updates** come from `SHChangeNotifyRegister`, which also covers virtual folders.
- **Icons** come from the system image list, cached per extension; per-file icons fill in asynchronously.

### Parity checklist
- **Navigation:** windows and tabs (new, duplicate, close others, reopen closed, drag between windows); back/forward/up with history menus; breadcrumb with child-folder chevrons; editable address bar with autocomplete, `shell:` paths, `%ENV%` variables, and `cmd`/`powershell`/`wt` to open a terminal.
- **Locations:** Home (Quick access pins, Recent, Starred), This PC, Drives, Network, Recycle Bin, WSL, phones and cameras, Control Panel.
- **Navigation pane:** tree, pinned items, drop targets.
- **Views:**
  - Layouts: icon sizes, list, details, tiles, content; compact mode.
  - Organising: sort, group-by, a column chooser for any shell property, per-folder view memory, item checkboxes.
  - Visibility: show/hide extensions, hidden files and protected OS files.
  - Panes and zoom: preview pane, details pane, status bar, Ctrl+wheel zoom.
- **Selection:** click / Ctrl / Shift, marquee, select all / none / invert, type-ahead jump.
- **File operations** (through `IFileOperation`, so dialogs, elevation and the Recycle Bin behave as in Explorer):
  - Opening: open, open with, run as admin.
  - Creating: new folder, New ▸ templates.
  - Clipboard: cut / copy / paste, paste shortcut.
  - Rename and delete: rename, recycle, permanent delete.
  - Other: undo/redo, copy as path, Properties, Share, pin, compress / extract, map network drive.
- **Menus:** the full native context menu, including third-party extensions, the folder background menu and Send to.
- **Drag & drop** with other apps in both directions, including modifiers, right-drag and drag images.
- **Search:** Windows Search in the current folder (with `kind:` / `date:` / `size:` filters) and instant type-to-filter.
- **Keyboard:** Explorer's full shortcut set.
- **Look:** light/dark/accent colours, Mica, per-monitor DPI, UI Automation.

### Windows integration
- **`explorer.exe`-compatible command line:** `path`, `/select,"file"`, `/e,`, `/n`, `/root,`, `shell:` and CLSID paths.
- **Single instance:** a second launch hands its arguments to the running window over a named pipe and opens a tab there.
- **Opt-in "default file manager":** per user in HKCU, with one-click revert. It sets the `Directory` and `Drive` default verb and Win+E's `opennewwindow` verb, so `Win+E` and folder links from apps land in Explorer+. `Folder` is left alone, so virtual folders opened from elsewhere (Control Panel, the desktop's This PC) stay File Explorer's.
- **Jump list** with pinned and recent folders, and **taskbar progress** during file operations.
- **Known limits:**
  - The Open/Save dialogs inside other apps, the desktop and the taskbar stay Explorer's, because they run in other processes.
  - `SHOpenFolderAndSelectItems` ("Show in folder" in some apps) may bypass the verb override. This needs testing in M4.

## 5. Decisions taken with the user

- **Duplicate drives.** Hot-plug SATA drives (D:, E: here) show twice in Explorer: under This PC and again as top-level "removable" entries. Explorer+ has one top-level **Drives** section with free-space bars, each volume listed **once** (deduplicated by volume GUID).
- **No OneDrive integration anywhere:** no tree node, status column, overlays, "Free up space" / "Always keep", or OneDrive menu entries of our own.
- **Favourites become local "Starred" items** (M3): files and folders, a sidebar section and a Home row, toggled with a star or `Ctrl+D`, stored locally.
- **Folder sizes always visible** (M2): computed in the background for visible folders at low I/O priority, cached, and invalidated by change notifications. M5 moves this to the MFT/USN index.
- **Content-aware views** (M2): the view is picked *after* listing, from extension stats we already have, with no file reads:
  - Folders that are mostly images or videos get large thumbnails.
  - Documents and code get details view with small icons.
  - Music gets details view with album and artist.
  - The rules are editable, and a manual change is remembered per folder and always wins.
- **Bloat review.** Every item below is a setting and can be flipped:

| Item | Default | Milestone |
|---|---|---|
| Gallery | cut | M1 ✔ |
| Libraries | cut | M1 ✔ |
| Network | keep | M1 ✔ |
| Linux (WSL) | keep, shown only if WSL is installed | M1 ✔ |
| Home: Recommended, Shared | cut | M3  ✔ |
| Home: auto-added frequent folders | cut (manual pins kept) | M3  ✔ |
| Home: Recent files | keep | M3  ✔ |
| Menu: Copilot / M365 AI entries | hidden | M2 ✔ |
| Menu: Clipchamp / Photos / Paint / Designer edit entries | hidden | M2 ✔ |
| Menu: Give access to / Cast to device / Include in library | hidden | M2 ✔ |
| Menu: Nearby share | removed | M2 ✔ |
| Menu: Share (share sheet) | keep | — |

Hidden menu entries stay reachable with Shift+right-click, which shows the full, unfiltered menu.

## 6. Milestones

**M1: core browser (done).**
- Solution, interop layer and STA worker pool.
- Fast and shell enumerators with streamed loading.
- Fluent window with tabs; back/forward/up with history menus.
- Breadcrumb and editable address bar (`shell:`, `%ENV%`, CLSID, commands, autocomplete).
- Sidebar with Quick access, deduplicated Drives and Locations.
- Virtualized details view: sortable Name / Date modified / Type / Size, real icons, faded hidden items.
- Native item and background context menus.
- Rename, delete, new folder and file, cut/copy/paste through `IFileOperation` and the shell clipboard.
- Live refresh, status bar, Explorer's core shortcuts, compatible command line, single instance, session restore, type-to-filter.

**M2: views and interaction (done; parts need hands-on testing, see below).**
- All eight view modes (Ctrl+Shift+1…8, Ctrl+wheel zoom) with shell thumbnails; custom virtualizing wrap panel.
- Group-by (Explorer's group names for dates and sizes), column chooser on the header right-click (any shell property, by category), per-folder view memory (`views.json`).
- Preview pane (Alt+P): images, text/code, native preview handlers out of process. Details pane (Alt+Shift+P): Explorer's own per-type property list.
- Drag & drop as source and target, including tabs and the sidebar, with drag images.
- Undo/redo (Ctrl+Z / Ctrl+Y) for rename, new, copy, move and delete to the Recycle Bin, logged with `IFileOperationProgressSink`.
- Checkboxes and marquee select.
- Background folder sizes and content-aware views.
- Context-menu bloat filtering.
- Total size / Free space columns on This PC.

Verified by screenshots of a dev build in an isolated profile (`EXPLORERPLUS_PROFILE`): content-aware large icons, grouped details with a property column and property sort, grouped icons, This PC space columns, folder sizes, text/image/font previews, details pane. Covered by tests: file-operation logs for copy, rename, move and new folder. **Not yet tried by hand:** mouse and keyboard work (drag & drop, marquee, header column menu, zoom, group collapse), undo of a delete (Recycle Bin restore), Office/PDF preview handlers.

**M3: locations and search (done; parts need hands-on testing, see below).**
- Home (Quick access, Starred, Recent files) as one grouped list in a fixed order, with a Location column. Home is the start location for new settings files; existing settings keep what they had. Right-click offers Open file location and Remove from Recent (deletes Windows' recent-item shortcut, not the file).
- Starred: a star on items, a Starred sidebar section, a Home row, `Ctrl+D`, the star button and context menus. Stored in `settings.json`, follows renames. `Ctrl+D` is Explorer's Delete shortcut; Delete stays on the Delete key.
- Recycle Bin: Original location / Date deleted columns, Empty, Restore all, Restore selected; Delete there is always permanent. Network and This PC: Map / Disconnect network drive.
- New ▸ lists the system's `ShellNew` templates (NullFile, Data, FileName, Command), plus Text Document when Windows has none; the new item goes into rename.
- Compress to ZIP / 7z / TAR.GZ and Extract here (ZIP via `System.IO.Compression`, the rest via Windows' own `%SystemRoot%\System32\tar.exe`; the `tar` on PATH may be GNU tar from Git). Work goes to a hidden temp name first, a single top-level item is extracted next to the archive, several go into a folder named after it, nothing is overwritten, zip-slip is refused. Undoable; Esc cancels.
- Windows Search: Enter in the filter box searches the folder and below (or every fixed and removable drive from This PC), with AQS (`kind:`, `date:`, `size:`, `ext:`). Results are a tab whose breadcrumb hangs off the scope.

Findings worth keeping:
- `search-ms:` URLs parse into shell folders, but those folders can't be enumerated outside Explorer (`E_UNEXPECTED` for every flag tried). Searches are built with `ISearchFolderItemFactory` plus the `SystemIndex` query parser; the URL is only the tab's identity (address bar, session).
- The shell builds item context menus for one parent folder only (asking the desktop folder with absolute PIDLs crashed). Selections that span folders (Home, search) get our own WPF menu; clipboard and drag still work through `SHCreateDataObject` with absolute PIDLs.

Verified by screenshots of a dev build in an isolated profile: Home sections and Location column, Recycle Bin columns and command bar actions, a Windows Search tab with its breadcrumb. Covered by tests: Home order and skipped items, search URL round trip and a real indexed search, templates, archive round trips, extraction rules and zip-slip, the multi-folder data object. **Not yet tried by hand:** the Starred section and stars, New ▸ templates of each kind, compress/extract from the menus with progress and Esc, Empty / Restore, Map / Disconnect, the cross-folder menu in Home and search, Remove from Recent.

**M4: integration and polish (done; parts need hands-on testing, see below).**
- Default file manager (Settings ▸ Windows integration), per user in HKCU, no elevation: `Directory` and `Drive` get a default verb `ExplorerPlus.open`, and Home's `opennewwindow` verb (Win+E) runs Explorer+ with an empty `DelegateExecute`. Values already in HKCU are backed up under `HKCU\Software\ExplorerPlus\DefaultFileManager` and put back on revert; keys we created are removed. `ExplorerPlus.exe --register-default` / `--unregister-default` do the same without a window (the MSI runs the second on uninstall). Settings shows when the registration points at another copy of the exe and offers "Use this copy".
- Jump list: New window, the sidebar pins and the last 10 folders opened (stored in `settings.json`, cleared from Settings). An explicit AppUserModelID (`ExplorerPlus`, or `ExplorerPlus.<profile>` for dev profiles) ties the taskbar button, the jump list and the Start menu shortcut together.
- Taskbar progress for copy, move, delete (`IFileOperationProgressSink.UpdateProgress`) and compress/extract.
- Tabs in the title bar (`WindowChrome` with the DWM caption buttons, Mica kept). Tabs drag to reorder, onto another window's strip to move there (history, view and items go along), or off every window to become a new window at the drop point; a window whose last tab leaves closes. Esc cancels.
- Settings window (More ▸ Settings, `Ctrl+,`): theme, start location, tab behaviour, visibility options, content-aware views on/off and "forget folder views", navigation-pane sections, the context-menu filter list, default file manager, jump list, version and data folder.
- Accessibility: UI Automation names on every icon button, the tabs, list rows, sidebar items, breadcrumb and search box; `F6` / `Shift+F6` cycle address bar, search, navigation pane and file list, as in Explorer.
- App icon. Per-user MSI (WiX 5, `installer/`), installed to `%LocalAppData%\Programs\Explorer+` without admin rights, and winget manifests (`installer/winget`, filled in by `installer/build.ps1`).

Fixed along the way: menus and tooltips opened from icon buttons (More, Back/Forward history) showed boxes because they inherited the icon font; tab tooltips showed CLSIDs for virtual folders; a second window opened exactly on top of the first when no placement was saved (it now cascades from the last window).

Verified by screenshots of a dev build in an isolated profile: tabs in the title bar (normal and maximized), the Settings window, menu text after the font fix. Driven with real mouse input: tab reorder, moving a tab onto another window's strip, tearing a tab off into a new window. UI Automation names checked with an automation tree dump. Covered by tests: default-file-manager registration, re-pointing and full revert (in a scratch registry key). The MSI builds and its tables were checked with `wix msi decompile`. **Not yet tried by hand:** turning the default file manager on for real (folder links, Win+E, `explorer.exe`-launching apps), installing and uninstalling the MSI, the jump list and taskbar progress on the taskbar, `Ctrl+N` (missed once under automation, worked on retry), a screen reader pass with Narrator.
- **Integration with VS Code, browsers' "Show in folder" and Office: still to test**, with the default file manager turned on. Apps that call `SHOpenFolderAndSelectItems` or start `explorer.exe` directly are expected to keep opening File Explorer (known limit above).

**M5+: power features.**
- Dual pane and command palette.
- Quick Look on `Space`.
- Transfer queue and batch rename.
- Instant MFT/USN search and instant folder sizes.
- Workspaces, folder compare & sync, Git status column, tags, duplicate finder.
- SFTP/WebDAV/S3 locations; plugin API.

## 7. Architecture

```
src/ExplorerPlus.Interop   hand-written P/Invoke + [ComImport] shell interfaces (IShellItem2, IShellFolder,
                           IContextMenu2/3, IFileOperation, IShellLinkW …)
src/ExplorerPlus.Core      no WPF: Pidl, ShellLocation, FolderEntry, FastFolderEnumerator, ShellFolderEnumerator,
                           FolderLoader (streaming), StaWorkerPool, ShellInfoCache, FileOperations, ShellContextMenu,
                           ShellClipboard, ShellLauncher, DriveService, ShellFilter, PathParser, ExplorerCommandLine
                           M2: FolderSizeService, ThumbnailExtractor, PropertyReader, PreviewHandlers, FileOpLog
                           M3: HomeFeed, SearchLocation, ShellNewTemplates, Archives, RecycleBin, NetworkDrives
                           M4: Integration/DefaultFileManager
src/ExplorerPlus.App       WPF, .NET 9 Fluent theme: MainWindow (+ .DragDrop, .Columns), AddressBar, Themes/FileList.xaml,
                           MainViewModel / TabViewModel / SidebarViewModel / PaneViewModel, VirtualizingWrapPanel,
                           PreviewHost, IconCache, ThumbnailService, FolderViewStore, UndoService, ShellChangeWatcher,
                           SingleInstance, AppSettings (%AppData%\ExplorerPlus\settings.json), Stars, QuickAccess
                           M4: MainWindow.Tabs (tab drag, title bar), SettingsWindow / SettingsViewModel, JumpLists
installer/                 WiX 5 per-user MSI (Package.wxs), build.ps1, winget manifest templates
tests/ExplorerPlus.Core.Tests   xUnit: enumerator parity, PathParser, command line, drives, file operations,
                           archives, Home, search, templates, default file manager registration
```

Why WPF on .NET 9:
- Mature list virtualization.
- A Windows 11 Fluent theme with light/dark modes.
- Easy COM interop.

WinUI 3 is the stack Microsoft is still fixing for Explorer, and the Files app shows its cost. C++/Win32 is faster but much slower to build in.

## 8. Performance budget (tested every milestone)

| Scenario | Target | M1 measured |
|---|---|---|
| Cold start to usable window | < 400 ms | not measured yet |
| Open local folder, first rows | < 50 ms | 10,000-file folder enumerated in 13 ms (test) |
| 100,000-file folder fully listed | < 1.5 s, UI responsive throughout | not measured yet |
| Right-click menu (own) | < 30 ms | not measured yet |
| Type-to-filter, 10k items | < 16 ms per keystroke | not measured yet |
| Idle memory, 3 tabs | < 150 MB | not measured yet |

## Sources
- [Windows Latest — File Explorer finally getting faster in 2026](https://www.windowslatest.com/2026/03/30/windows-11-file-explorer-is-finally-getting-faster-in-2026-but-its-been-slower-than-windows-10-for-years)
- [WebProNews — Legacy folder-discovery feature causes lag](https://www.webpronews.com/windows-11-file-explorer-lag-legacy-xp-feature-fix-via-registry-tweak)
- [TechRadar — Win11 slower than Win10](https://www.techradar.com/computing/windows/microsoft-says-its-fixing-a-baffling-way-in-which-windows-11-is-much-slower-than-windows-10-and-its-about-time)
- [TweakTown — preloading doubles RAM, still slow](https://tweaktown.com/news/109138/microsofts-plan-to-speed-up-file-explorer-in-windows-11-backfires-double-the-ram-still-slow/index.html)
- [DevClass — WinUI 3 performance push](https://www.devclass.com/development/2026/05/14/microsoft-aims-to-speed-windows-with-leap-forward-in-winui-3-perf/5239721)
- [Neowin — Top 10 File Explorer requests](https://www.neowin.net/news/top-10-features-and-changes-windows-11-users-want-for-file-explorer/)
- [Neowin — What users wanted vs. delivered](https://www.neowin.net/amp/file-explorer-in-windows-11-what-users-wanted-and-what-microsoft-delivered/)
- [Cloudhouse — Why the Win11 right-click menu is slow](https://cloudhousetechnologies.com/blog/windows-11-right-click-context-menu-slow-fix-2026)
- [voidtools forum — Everything uses the USN journal](https://www.voidtools.com/forum/viewtopic.php?p=486)
- [Wikipedia — USN Journal](https://en.wikipedia.org/wiki/USN_Journal)
- [Files v3 release notes](https://files.community/blog/posts/v3)
- [Directory Opus](https://dopus.com) · [Zabkat file manager review](https://www.zabkat.com/declone/best-windows-file-manager.htm)
