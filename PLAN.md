# Explorer+ — Plan

A fast, keyboard-friendly file manager for Windows 11, built for work and everyday use.

## Status (18 Nov 2026)

- **Now: I17** recent changes. Its "Decide with the user" lines are still open, so they are settled with the user before it is built.
- **Then, in this order:** I18 big, old and empty finders, I19 flat view, I20 disk usage treemap, I21 drop shelf.
- **Backlog, only when the user asks:** I14, Save and Open dialogs ([#9](https://github.com/jdc68/explorer-plus/issues/9)).
- **Postponed:** M7, remote locations and plugins.
- **Done:** M1 to M6, I1 to I13, I15 (closed for now), I16 (0.1.35), I22, I23 (0.1.33), I24. What was built, the findings and the checks made are in [HISTORY.md](HISTORY.md). What is still open from them is in section 6, under "Open checks" and "Loose ends".

How this file is kept:
- This section is the only place that gives the order. Update it when a step starts or finishes, or when the user changes the order.
- A new step takes the next free number (I25 is next) and keeps it for good, since commits and issues refer to it. Its spec goes into section 6 and its place in the order goes here.
- A step's "Decide with the user" lines are settled before it is built.
- When a step is done, its spec and write-up (what was built, findings, how it was checked) move to HISTORY.md. Its "not tried by hand" items go to "Open checks" and anything left for later to "Loose ends". Section 7 is updated when files are added, section 8 when something is measured, and section 5 when the user makes a decision that must hold.

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
  - The Open/Save dialogs inside other apps, the desktop and the taskbar stay Explorer's, because they run in other processes. Their Quick access sidebar shows the pins Explorer+ keeps in Windows (section 5).
  - "Show in folder" (`SHOpenFolderAndSelectItems`) works through the verb and the shell's window list, where Explorer+ registers its windows (I6). Apps that start `explorer.exe` directly may still bypass the verb override.

## 5. Decisions taken with the user

- **Duplicate drives.** Hot-plug SATA drives (D:, E: here) show twice in Explorer: under This PC and again as top-level "removable" entries. Explorer+ has one top-level **Drives** section with free-space bars, each volume listed **once** (deduplicated by volume GUID).
- **No OneDrive integration anywhere:** no tree node, status column, overlays, "Free up space" / "Always keep", or OneDrive menu entries of our own.
- **Favourites become local "Starred" items** (M3): files and folders, a sidebar section and a Home row, toggled with a star or `Ctrl+D`, stored locally.
- **Folder sizes always visible** (M2): computed in the background for visible folders at low I/O priority, cached, and invalidated by change notifications. A later milestone moves this to the MFT/USN index.
- **Closing a window closes its tabs** (13 Nov 2026): Explorer+ keeps running in the background so it opens fast, but the next window starts at the start location instead of reopening last time's tabs. "Reopen the tabs from last time" is opt-in (off by default; settings files from older versions are switched off once through `AppSettings.Format`).
- **Works with HoverPreview** (the user's own hover-to-preview tool, `E:\Programming\Previewer`): file-list items expose their full path as the UI Automation `AutomationId`, which HoverPreview reads instead of looking up the folder through `IShellWindows` (Explorer+ isn't in that list). Keep that `AutomationId` on both item styles.
- **The built-in hover preview covers Explorer+'s own lists only** (16 Nov 2026, I13): no previews over File Explorer or the desktop; HoverPreview keeps doing those. While "Preview on hover" is on, every Explorer+ main window carries the window property `ExplorerPlus.HoverPreview` (`SetProp`), and HoverPreview skips windows that have it, so there are never two popups. Keep the property's name as it is: the tool checks it.
- **The installed copy is always the latest build**: every `installer/build.ps1` run without `-Version` bumps the patch version above both `Directory.Build.props` and the installed copy, and the MSI allows same-version upgrades, so installing always replaces the old copy. After changing the app, rebuild and install with `-Install` (`-NoStart` from a Claude session); it fails unless exactly one Explorer+ is installed, at the new version, with the files just published.
- **A GitHub release for every version** (13 Nov 2026, replacing "only on request" from I7): each version committed with a change is published as a release with its MSI, by `installer/build.ps1 -Release -Yes` after the push. Versions that were only built or installed, and never committed, get none.
- **Sizes in the largest sensible unit** (M5): not Explorer's "1,234,567 KB". Below 1,000 KB in whole KB, then MB/GB/TB with two decimals ("12.45 MB", "4.65 GB"), for files and folders alike.
- **Content-aware views** (M2): the view is picked *after* listing, from extension stats we already have, with no file reads:
  - Folders that are mostly images or videos get large thumbnails.
  - Documents and code get details view with small icons.
  - Music gets details view with Length (the #, Title, Contributing artists and Album columns were dropped from it in I3, #22).
  - Pictures get one Date column: Date taken, or Date created when there is none (I3, #23).
  - The rules (the view for each kind and the extensions that make a folder that kind) are editable in Settings ▸ Files and folders. A view picked by hand is remembered per folder and always wins; sorting a folder or changing its columns doesn't fix its view (I3).
- **Quick access in step with Windows** (16 Nov 2026, with the user): Explorer+'s pins and standard folders are the source of truth. Windows' own Quick access (the pinned part of File Explorer's sidebar, which the Open and Save dialogs show too) is kept equal to them. `Services/QuickAccessSync` pins and unpins through the shell (`pintohome` on the folder, `unpinfromhome` on its item inside Quick access, `Core/Shell/WindowsQuickAccess`). It runs at startup, in the background too, after any change to Explorer+'s list, and after any change to Windows' Quick access (a shell notification on that folder), so a pin made in File Explorer is put back. The first run takes Windows' own pins into Explorer+ once (`QuickAccessImported`), so none are lost. Settings ▸ Navigation pane ▸ "Keep Windows' Quick access in step with Explorer+" (on by default). Frequent folders stay each app's own: Windows computes its list itself.
- **Bloat review.** Every item below is a setting and can be flipped:

| Item | Default | Milestone |
|---|---|---|
| Gallery | cut | M1 ✔ |
| Libraries | cut | M1 ✔ |
| Network | keep | M1 ✔ |
| Linux (WSL) | keep, shown only if WSL is installed | M1 ✔ |
| Home: Recommended, Shared | cut | M3  ✔ |
| Home: auto-added frequent folders | back as a setting in I12 (#15), on by default, from Explorer+'s own history | I12 ✔ |
| Home: Recent files | keep | M3  ✔ |
| Menu: Copilot / M365 AI entries | hidden | M2 ✔ |
| Menu: Clipchamp / Photos / Paint / Designer edit entries | hidden | M2 ✔ |
| Menu: Give access to / Cast to device / Include in library | hidden | M2 ✔ |
| Menu: Nearby share | removed | M2 ✔ |
| Menu: Share (share sheet) | keep | — |

Hidden menu entries stay reachable with Shift+right-click, which shows the full, unfiltered menu.

- **Files in use** (17 Nov 2026, I16): when a delete fails because an app has the file open, "Force delete" first asks that app to close (Restart Manager, so it can save) and only ends it after a second confirmation, then deletes again. It shows only after a failed delete, never as a menu entry of its own. There is no "Delete on next restart". Apps of other Windows users, apps running as administrator, services, Windows Explorer and Windows itself are named but never closed (no UAC prompt is offered for them).

## 6. Roadmap

### Now and next, in order

From the 14 Nov 2026 brainstorm, in the order chosen with the user (not GitHub issues). What hurts every day comes first, then the features that reuse the index service, then the ones with their own UI. I15, the performance budget, came first in that list. Speed is the product's main promise, and these features can slow it down, so run `installer/perf.ps1` after any of them that touches the file list, loading or startup. Each spec is only a sketch.

**I17: recent changes view (needs the index service).**
- Everything modified or created in the last N minutes, hours or days, across all drives or below a folder, newest first, as a results tab (like instant search: location, size and date from the index, no disk access). It is the answer to "where did that download go" and "what did that installer touch".
- The index keeps size and modified time for each record, and the USN journal reports every change, so a request that returns the newest hits is enough: a new request type in `IndexProtocol` (`RecentRequest(Scope, Since, Limit, IncludeHidden, IncludeProtected)`, protocol v3) that scans the modified-time array, with the same access checks as a search. The service, the Update button and the version check in Settings handle the new protocol like they did for v2.
- In the app: a location `explorerplus-recent:` (the same kind of app-made location as `explorerplus-search:`), a Home row and a sidebar entry "Recent changes", the palette command "Recent changes…", and a bar with 15 minutes, 1 hour, today, 7 days. Windows' own noise (System32, `$Recycle.Bin`, `AppData\Local\Temp`, the browser caches) is hidden by default, with a switch to show everything.
- Decide with the user: whether created counts as well as modified (the index has only the modified time today; the created time needs another pass over the MFT), the noise filter's default list, and whether the 1,000-result cap is enough here.

**I18: big, old and empty finders (needs the index service).**
- A "Clean up" command (More, the palette, This PC's drive tiles) with three lists over a drive or folder, each opening as a results tab: the largest files (top 1,000 by size), files not modified in N years (and bigger than M MB), and empty folders. All three are queries over the index (size and modified time are there; a folder is empty when it has no children, which needs a child count in the index, a small addition to `VolumeIndex`).
- Reuses the results tab, the sort and group by size or date, and the "N items · X" status bar from the duplicate finder. Deleting from the list goes to the Recycle Bin and removes the rows, as in the duplicate finder; there is no one-click "delete all" at first.
- Decide with the user: whether the folders list counts a folder with only empty folders below it as empty (probably yes), and whether system, hidden and protected locations are skipped (yes, unless the user asks).

**I19: flat view.**
- A "Flat view" toggle (View menu, palette, status bar chip) that shows every file below the current folder as one list, with a Location column, as in Directory Opus. A type filter next to it ("only PDFs", "only pictures") makes it the quickest way to see everything of one kind in a project.
- With the index service running it is a scoped index query (as with a folder search with an empty query), so a big tree lists at once; without it, a streamed walk through `FastFolderEnumerator`, cancelled on navigation like any listing. It is an app-made location like the search tabs, so deletes and renames work on its items and nothing is watched.
- Decide with the user: the item cap (1,000 like search, or more for this view), whether folders are listed too or only files, and whether it remembers per folder that it is on.

**I20: disk usage view (treemap).**
- "Analyze this drive" (a drive tile's menu on This PC, any folder's menu, the palette): a treemap of the folder, each rectangle sized by its total, drawn from the index's folder totals (instant) and from the walk where the index can't answer (network, FAT, removable). Click a rectangle to go into it, a breadcrumb to go back up, a list beside it ordered by size, and the right-click menu of the item under the mouse (open, show in folder, delete to the Recycle Bin).
- Colour by file kind (pictures, videos, music, documents, code, archives, other) with a legend that also filters. The treemap itself is a custom WPF control that draws the squarified layout in a `DrawingVisual` and hit-tests by rectangle, so thousands of cells stay smooth; it limits itself to the largest N children per level and groups the rest as "other".
- Needs a request for the children of a folder with their totals (`FolderSizesRequest` answers by path today, so the app can list a folder and ask for its subfolders in one go; files come from the listing). Decide with the user: a tab or a separate window, and whether a sunburst is wanted as a second chart.

**I21: drop shelf.**
- A small floating tray, always on top, that holds files while you browse. Drag files onto it (from any Explorer+ window or other apps), go to another folder or app, and drag them out again, or use "Move here" and "Copy here" in the shelf's menu to put the whole set in the current folder. It saves the dual pane dance for one-off moves between far-apart folders.
- The shelf keeps paths only (never copies), shows names and icons with a count, and survives a restart of Explorer+ (the paths in settings, items that no longer exist drop out). Items drag out as a normal file drag (`SHCreateDataObject` with the PIDLs), with the shell's drop effects. A shortcut and the palette ("Add to shelf", "Show shelf") add the selection without dragging.
- Decide with the user: whether the shelf appears by itself when a drag starts in Explorer+ (as on macOS) or only from a button, where it docks, and whether moved items are removed from the shelf at once.

### Backlog and postponed

**I14: Save and Open dialogs (backlog; not in active development).**
- Moved to the backlog by the user on 14 Nov 2026, after the options below and a "pick in Explorer+" window (Explorer+ opens over the dialog, then fills the path into it and presses Save) were explained. The user didn't like these approaches, so none of them is planned. Issue #9 stays open. Come back to it only when the user asks.
- [#9](https://github.com/jdc68/explorer-plus/issues/9) Browsers and other apps open Windows' own Save and Open dialogs (the common item dialog, `IFileDialog`), which run inside those apps and use Explorer's code. The default-file-manager registration can't change that (section 4, known limits), and replacing the dialog itself would mean injecting into every process, which is off the table. Options to weigh with the user before building anything:
  - A dialog helper: Explorer+ notices a file dialog in the foreground (UI Automation or a `WinEvent` hook, no injection) and offers its open folders, pins and recent folders; picking one sends the path to the dialog's address field. This is how Listary and similar tools work.
  - A hotkey in the dialog that jumps it to the folder in the active Explorer+ window.
  - "Show in folder" after a download is a different path (`SHOpenFolderAndSelectItems`, or the browser starting `explorer.exe`), handled in I6.

**M7: remote locations and plugins (postponed; not scheduled).** It comes after the queue above, when the user asks.
- SFTP, WebDAV and S3 as locations in the sidebar and address bar, listed and browsed like folders, with copy, move and delete going through the same queue.
- A plugin API: columns, preview handlers, item menu commands and locations from separate assemblies.

### Open checks (built, not tried by hand yet)

Gathered from the finished steps; the step in brackets has the details in HISTORY.md. Strike an item when it has been tried, and turn what fails into a fix or a step.
- **Windows integration.** These only work from an Explorer+ started normally, not from the Claude desktop app (see the MSIX note in CLAUDE.md).
  - Folder links from other apps, Win+E, VS Code "Show in folder" and Office "Open file location", and uninstalling with the default file manager on [M4].
  - The taskbar progress, `Ctrl+N` and a Narrator pass [M4].
  - The new icon in the Start menu and in installed apps [I1].
- **Lists and views:**
  - Drag and drop, marquee, the header column menu, zoom and collapsing groups [M2].
  - The scroll reset in icon views [I2].
  - Grouping by the pictures' Date column, and editing view-rule extensions in Settings [I3].
  - Smooth scrolling with a precision touchpad and with a free-spinning or high-resolution wheel [I10].
- **Files and menus:**
  - Office and PDF preview handlers, and undoing a delete by hand [M2].
  - Starred items and the Starred section, New ▸ templates of each kind, and compress and extract from the menus with progress and Esc [M3].
  - Empty and Restore in the Recycle Bin, Map and Disconnect network drive, the cross-folder menu in Home and search, and Remove from Recent [M3].
- **Drag and drop:**
  - Telegram, a browser, File Explorer and the desktop as drop targets (their labels), and Esc over the window [I5].
  - Drags between two windows and onto the sidebar or a tab, and right-drags [M6].
  - A cut made in File Explorer showing as cut [I2].
- **Power features:**
  - Quick Look with real photos, videos and Office files, and on a second monitor [M5].
  - Batch rename across search results, and the palette with the mouse [M5].
  - The transfers button's menu (Start now, Cancel) [M6].
  - The right pane's column menu and header drags, grouping and view changes in each pane, and Quick Look and the preview pane while switching panes [M6].
  - Index sizes after a service restart on a busy disk, tags on network paths, a duplicate search over a whole drive, sync with elevation prompts or locked files, and a workspace with many tabs on a second monitor [M6].
- **Instant search service:**
  - The Settings buttons (Turn on, Update, Turn off and remove) [M6].
  - Removal when Explorer+ is uninstalled, a second Windows user, and results tabs restored from the session [M6].
  - The app right after the service starts, before sizes are read [I4].
- **Settings:** the Settings window's buttons [I2], and switching Light and Dark while a menu is in use [I1].
- **Toolbar:** the Command palette and Settings buttons in a split window [I23].
- **Files in use:** a real app with unsaved work (Word, Notepad) asked to close by Force delete, a folder open in a terminal, a failed move or copy from the queue, a failed rename, Delete from the native item menu (now Explorer+'s own delete, with undo), and the PDF or Office preview pane holding the file [I16].
- **This PC and the sidebar:**
  - This PC with network drives and locations, a phone, an optical drive, and a USB stick plugged in and pulled while the page is open [I11].
  - Frequent folders: the right-click entries, the Settings checkbox, and a frequent folder on a share that goes offline [I12].
- **Hover preview:** a monitor with another scale, HEIC and RAW through the shell thumbnail, the light theme, and the right pane [I13].

### Loose ends

Known gaps left on purpose. Each one could become a step if the user wants it.
- **Folder sizes:** index sizes don't update while a folder is open, because only the walk's cache raises change events [I4].
- **Releases:**
  - Release notes are the commit subjects. Writing them by hand is still undecided.
  - Submitting the winget manifests to winget-pkgs is still done by hand [I7].
- **Views:** List view takes about 50 ms a frame while scrolling, with smooth scrolling on or off [I10].
- **Performance, left for later in I15:**
  - The first menu after a minute idle takes about 700 ms, because the packaged verbs' hosts start again.
  - Dropping the details view's cell peers would save about 15 ms a keystroke, but screen readers would lose the columns.
  - Lighter rows, or fewer objects per item, for large folders' first rows and their GC pauses.
  - The working set is over its target.
  - Our own instant menu with lazily loaded shell entries (section 1), the way to the menu's 30 ms target.
- **Unknown UI Automation listener:** some program on this machine listens for UI Automation events. It costs about 120 ms at startup and about 40% of each list layout. It isn't HoverPreview; which program it is hasn't been found [I15, I13].

## 7. Architecture

```
src/ExplorerPlus.Interop       hand-written P/Invoke + [ComImport] shell interfaces: ShellInterfaces (IShellItem2,
                               IShellFolder, IContextMenu2/3, IFileOperation …), FileOperationSink, Preview, Search,
                               ShellLink, ShellWindows, DragDrop, Imaging, Volumes, RestartManager
src/ExplorerPlus.Indexing      no WPF, no shell: VolumeIndex (flat per-record arrays, parallel search, folder totals),
                               IndexQuery, VolumeReader (MFT enumeration + USN journal), MftReader (raw $MFT sizes and
                               times), IndexHost (volumes, impersonated search), IndexProtocol, IndexClient
src/ExplorerPlus.IndexService  LocalSystem Windows service (raw advapi32, no NuGet): ServiceRunner, PipeServer,
                               ServiceInstaller
src/ExplorerPlus.Core          no WPF
  Shell/                       Pidl, ShellLocation, FolderEntry, ShellNative, ShellInfoCache, ShellFilter
                               listing: FolderLoader (streaming), FastFolderEnumerator, ShellFolderEnumerator,
                                 HomeFeed, SearchLocation, IndexSearch, TagView, DuplicateFinder, ThisPc
                               actions: FileOperations, FileOpLog, ShellContextMenu, ShellClipboard, ShellDragDrop,
                                 ShellLauncher, LocationActions, Associations, LinkResolver, ShellNewTemplates,
                                 Archives, BatchRename, FolderCompare, WindowsQuickAccess, FileLocks
                               data: PropertyReader, ThumbnailExtractor, PreviewHandlers, FolderSizeService,
                                 DriveService, GitStatus, FolderHistory, SizeFormat, NaturalComparer
  Navigation/                  PathParser, ExplorerCommandLine, FuzzyMatch
  Integration/                 DefaultFileManager, InheritedEnvironment
  Threading/                   StaWorkerPool
src/ExplorerPlus.App           WPF, .NET 9 Fluent theme; Program (single instance before WPF), App
  Views/                       MainWindow (+ .Tabs, .Panes, .Commands, .Columns, .DragDrop, .Perf, .InUse), FileListView,
                               VirtualizingWrapPanel, SmoothScroller, AddressBar, BreadcrumbPanel, PreviewHost,
                               HoverPreview + HoverPopup, QuickLookWindow, BatchRenameWindow, CompareWindow,
                               InUseWindow, SettingsWindow, PromptWindow; Themes/FileList.xaml
  ViewModels/                  MainViewModel, TabViewModel, PaneViewModel, SidebarViewModel, SettingsViewModel,
                               ItemViewModel, Columns, Grouping, ItemComparer, ContentViewRules, ViewMode, PaletteEntry,
                               InUseReport
  Services/                    AppSettings, AppPaths, SingleInstance, Background, FolderViewStore, IconCache,
                               ThumbnailService, PreviewLoader, FolderSizeWatcher, ShellChangeWatcher, UndoService,
                               TransferQueue, CutItems, Stars, Tags, Workspaces, QuickAccess, QuickAccessSync,
                               FrequentFolders, JumpLists, ShellWindowRegistration + ShellWindowSite, IndexServiceSetup,
                               PerfProbe
installer/                     WiX 5 per-user MSI (Package.wxs), build.ps1 (build, install, release), perf.ps1,
                               winget manifest templates
tests/ExplorerPlus.Core.Tests  xUnit on the real shell and temp folders; one file per area (EnumerationTests,
                               FileOperationTests, ViewTests, IndexTests, HoverPreviewTests, ShellWindowTests …)
```

Why WPF on .NET 9:
- Mature list virtualization.
- A Windows 11 Fluent theme with light/dark modes.
- Easy COM interop.

WinUI 3 is the stack Microsoft is still fixing for Explorer, and the Files app shows its cost. C++/Win32 is faster but much slower to build in.

## 8. Performance budget (measured by hand with `installer/perf.ps1`)

| Scenario | Target | Measured 16 Nov 2026 (I15) |
|---|---|---|
| Cold start to usable window | < 400 ms | **1.22 s** to Home's first rows (window 1.07 s), median of 5, file cache warm; **0.63 s** to the first rows when it runs in the background (from the second launch) |
| Open local folder, first rows | < 50 ms | **82 ms** for 10,000 files, full list 107 ms (170 ms for both on 15 Nov); the enumeration alone is 13 ms (test) |
| 100,000-file folder fully listed | < 1.5 s, UI responsive throughout | 0.38 s (first rows 117 ms); longest UI stall **128 ms**, one stall over 50 ms per load (0.56 s and 3–4 stalls of up to 190 ms on 15 Nov) |
| Right-click menu (own) | < 30 ms | **67 ms** median to build on a .txt file, **132 ms** the first time (1.3 s before the warm-up). The build starts on button down, so after a normal click it shows 5–26 ms after release. The shell's own cost varies: 190 ms median that morning |
| Type-to-filter, 10k items | < 16 ms per keystroke | **41 ms** to the frame, the UI busy for **61 ms** median (96 ms worst of 10; 16 ms when nothing matches). On 15 Nov: 78 ms to the frame and about 200 ms busy |
| Scrolling, 10k items (Details) | - | page step 44 ms, a tenth of a page 15 ms, each until the UI is idle (no smooth-scrolling animation) |
| Idle memory, 3 tabs | < 150 MB | **207 MB** working set, 133 MB private (Home, C:\Windows, System32); 1 tab: 202 MB / 128 MB. With the menu warm-up the shell extensions are loaded by then (178 / 116 MB without it; 198 / 135 MB on 15 Nov) |

Machine: Ryzen 7 5800X, 16 logical processors, x64 ReadyToRun build of 0.1.27 with the I15 fixes, scratch files on E:. A later run the same afternoon (16 Nov, 15:43, with the menu, load and start changes above) ran with OBS, FL Studio and Chrome Remote Desktop open (about 29% CPU) and the shell's menu slower for every program (about 180 ms): cold start 1.17 s (window 1.04 s), 10,000 files 117 ms, 100,000 files 0.38 s with a 163 ms stall, menu 188 ms median (720 ms the first time, after a minute idle), filter 48 ms to the frame and 60 ms busy, memory 201 / 119 MB with 3 tabs. Compare runs only on a quiet machine. Measured with `installer/perf.ps1` (by hand; the app measures itself through `Services/PerfProbe` when `EXPLORERPLUS_PERF` is set, results in `artifacts/perf/`). Bold rows are over target. "Busy" is the time until the UI thread is idle again; "to the frame" is the first frame with the new rows.

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
