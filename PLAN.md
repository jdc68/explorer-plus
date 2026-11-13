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
  - `SHOpenFolderAndSelectItems` ("Show in folder" in some apps) and apps that start `explorer.exe` directly may bypass the verb override. Not testable from a Claude session (see M4).

## 5. Decisions taken with the user

- **Duplicate drives.** Hot-plug SATA drives (D:, E: here) show twice in Explorer: under This PC and again as top-level "removable" entries. Explorer+ has one top-level **Drives** section with free-space bars, each volume listed **once** (deduplicated by volume GUID).
- **No OneDrive integration anywhere:** no tree node, status column, overlays, "Free up space" / "Always keep", or OneDrive menu entries of our own.
- **Favourites become local "Starred" items** (M3): files and folders, a sidebar section and a Home row, toggled with a star or `Ctrl+D`, stored locally.
- **Folder sizes always visible** (M2): computed in the background for visible folders at low I/O priority, cached, and invalidated by change notifications. A later milestone moves this to the MFT/USN index.
- **Closing a window closes its tabs** (13 Nov 2026): Explorer+ keeps running in the background so it opens fast, but the next window starts at the start location instead of reopening last time's tabs. "Reopen the tabs from last time" is opt-in (off by default; settings files from older versions are switched off once through `AppSettings.Format`).
- **Works with HoverPreview** (the user's own hover-to-preview tool, `E:\Programming\Previewer`): file-list items expose their full path as the UI Automation `AutomationId`, which HoverPreview reads instead of looking up the folder through `IShellWindows` (Explorer+ isn't in that list). Keep that `AutomationId` on both item styles.
- **The installed copy is always the latest build**: every `installer/build.ps1` run without `-Version` bumps the patch version above both `Directory.Build.props` and the installed copy, and the MSI allows same-version upgrades, so installing always replaces the old copy. After changing the app, rebuild and install with `-Install` (`-NoStart` from a Claude session); it fails unless exactly one Explorer+ is installed, at the new version, with the files just published.
- **Sizes in the largest sensible unit** (M5): not Explorer's "1,234,567 KB". Below 1,000 KB in whole KB, then MB/GB/TB with two decimals ("12.45 MB", "4.65 GB"), for files and folders alike.
- **Content-aware views** (M2): the view is picked *after* listing, from extension stats we already have, with no file reads:
  - Folders that are mostly images or videos get large thumbnails.
  - Documents and code get details view with small icons.
  - Music gets details view with Length (the #, Title, Contributing artists and Album columns were dropped from it in I3, #22).
  - Pictures get one Date column: Date taken, or Date created when there is none (I3, #23).
  - The rules are editable, and a manual change is remembered per folder and always wins.
- **Bloat review.** Every item below is a setting and can be flipped:

| Item | Default | Milestone |
|---|---|---|
| Gallery | cut | M1 ✔ |
| Libraries | cut | M1 ✔ |
| Network | keep | M1 ✔ |
| Linux (WSL) | keep, shown only if WSL is installed | M1 ✔ |
| Home: Recommended, Shared | cut | M3  ✔ |
| Home: auto-added frequent folders | cut (manual pins kept); back as a setting in I12 (#15) | M3  ✔ |
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
- Default file manager (Settings ▸ Windows integration), per user in HKCU, no elevation: `Directory` and `Drive` get a default verb `ExplorerPlus.open`, and Home's `opennewwindow` verb (Win+E) runs Explorer+ with an empty `DelegateExecute`. Values already in HKCU are backed up under `HKCU\Software\ExplorerPlus\DefaultFileManager` and put back on revert; keys we created are removed. `ExplorerPlus.exe --register-default` / `--unregister-default` do the same without a window. On uninstall the MSI runs the second, after deleting the `ExplorerPlus.open` verb keys and the Win+E command key itself, so folders still fall back to File Explorer if that step fails: a default verb that names a missing key is ignored (checked by hand). Settings shows when the registration points at another copy of the exe and offers "Use this copy".
- Jump list: New window, the sidebar pins and the last 10 folders opened (stored in `settings.json`, cleared from Settings). An explicit AppUserModelID (`ExplorerPlus`, or `ExplorerPlus.<profile>` for dev profiles) ties the taskbar button, the jump list and the Start menu shortcut together.
- Taskbar progress for copy, move, delete (`IFileOperationProgressSink.UpdateProgress`) and compress/extract.
- Tabs in the title bar (`WindowChrome` with the DWM caption buttons, Mica kept). Tabs drag to reorder, onto another window's strip to move there (history, view and items go along), or off every window to become a new window at the drop point; a window whose last tab leaves closes. Esc cancels.
- Settings window (More ▸ Settings, `Ctrl+,`): theme, start location, tab behaviour, visibility options, content-aware views on/off and "forget folder views", navigation-pane sections, the context-menu filter list, default file manager, jump list, version and data folder.
- Accessibility: UI Automation names on every icon button, the tabs, list rows, sidebar items, breadcrumb and search box; `F6` / `Shift+F6` cycle address bar, search, navigation pane and file list, as in Explorer.
- Background mode (Settings ▸ General, on by default): closing the last window leaves Explorer+ running with no window, its folder-size timer paused and memory trimmed, so folder links, "Show in folder" and Win+E open a window in about 0.2 to 0.3 s instead of a 1.3 s cold start. "Start in the background when I sign in" adds an `HKCU\...\Run` entry (`--background`), and that start builds one hidden window ahead of time so the first open after sign-in takes about 0.4 s. More ▸ Exit really quits. A hidden top-level window answers sign-out and Restart Manager. The MSI quits a running Explorer+ (`--quit`) before removing files, removes the sign-in entry on uninstall (`--uninstall`), and starts Explorer+ in the background after installing. A second launch also hands its arguments over before WPF loads (`Program.Main`), which cut the handoff from 0.4–1.0 s to about 0.2 s.
- App icon. Per-user MSI (WiX 5, `installer/`), installed to `%LocalAppData%\Programs\Explorer+` without admin rights, and winget manifests (`installer/winget`, filled in by `installer/build.ps1`).

Fixed along the way: menus and tooltips opened from icon buttons (More, Back/Forward history) showed boxes because they inherited the icon font; tab tooltips showed CLSIDs for virtual folders; a second window opened exactly on top of the first when no placement was saved (it now cascades from the last window).

Verified by screenshots of a dev build in an isolated profile: tabs in the title bar (normal and maximized), the Settings window, menu text after the font fix. Driven with real mouse input: tab reorder, moving a tab onto another window's strip, tearing a tab off into a new window. UI Automation names checked with an automation tree dump. Covered by tests: default-file-manager registration, re-pointing and full revert (in a scratch registry key). The MSI builds and its tables were checked with `wix msi decompile`. **Tried live on the user's machine (9 Nov 2026), with a caveat.** The MSI installs (8 files, Start menu shortcut with the `ExplorerPlus` AppUserModelID, listed in installed apps) and uninstalls cleanly, and the jump list file had Recent folders and the New window task. Everything about the default file manager turned out not to be testable from a Claude session: the Claude desktop app is an MSIX package, so HKCU writes from the processes it starts (shells, and any app launched from them) go into a per-package registry overlay. Those processes see the registration, so folder and drive links opened from them landed in Explorer+. Explorer, Windows Installer and other apps never see it: Win+E kept opening File Explorer, and the uninstall step found no backup to undo. The MSI now also deletes the verb keys itself, as a safety net for when that step fails. `explorer.exe <folder>`, `explorer.exe /select,` and `SHOpenFolderAndSelectItems` opened File Explorer, but since Explorer never saw the registration, that says nothing either way.

**Still to try by hand, from Settings in an Explorer+ started normally (not from Claude):** folder links from other apps, Win+E, VS Code / browser "Show in folder" and Office "Open file location", then uninstalling with the default turned on. Also the taskbar progress, `Ctrl+N` (missed once under automation, worked on retry) and a Narrator pass.

**M5: power features, first part (done; parts need hands-on testing, see below).**
- Command palette (`Ctrl+K`, also `Ctrl+Shift+P`, More ▸ Command palette): every command with its shortcut, plus places to jump to (open tabs, the sidebar's entries, recent folders). Fuzzy matching (`Core/Navigation/FuzzyMatch`): prefix, then word start, then letters in order; a match on a place's path ranks below a match on its name. It's a popup, so it also draws over a native preview handler.
- Quick Look on `Space`: a window over most of the main window with the same previews as the preview pane (images, text, preview handlers, a big thumbnail otherwise; folders show their thumbnail) and the name, type, size and date underneath. It opens without taking focus, so the arrow keys keep moving through the list and the preview follows; `Space` or `Esc` closes it. A space typed within a second of other letters still goes to type-ahead. `Ctrl+Space` still toggles the focused item's selection.
- Batch rename (`F2` with several items selected, `Ctrl+Shift+F2`, the item menu, More): find and replace (plain or regex, match case), a name pattern with `{name}`, `{n}` / `{n:000}`, `{date}` / `{date:yyyyMMdd}`, `{ext}`, `{parent}`, numbering start and step, case, and whether the extension is included. A live preview lists every new name and why an item would be skipped (empty, invalid characters, reserved names, two items with one name, name already taken). It runs as one `IFileOperation`; when a new name is another selected item's current name (swaps, numbers shifted by one), everything goes through a temporary name first, and anything left on one is put back if the second step fails. One `Ctrl+Z` undoes the whole batch. Stars follow the new names.
- Size column: whole KB below 1,000 KB, then MB, GB, TB with two decimals ("12.45 MB", "4.65 GB"), 1024-based as in Windows (`Core/Shell/SizeFormat`). The status bar and details pane use the same, with bytes below 1 KB.

Verified on a dev build in an isolated profile, driven with real key input: the size column (a 23.84 MB folder, 12.45 MB, 4.65 GB, 293 KB files), the palette opening and filtering, Quick Look on an image and a PDF with arrow keys moving through the list and Space closing it, a batch rename of four files to "Trip 01…04" and its undo. Covered by tests: size formatting, every batch-rename rule and conflict, applying a shifted-number rename through the shell and reverting it, case-only renames, fuzzy ranking.

Findings worth keeping:
- On the dev machine something swallows `Ctrl+Shift+P` system-wide before any app sees it (it is not a `RegisterHotKey` hotkey), so the palette's main shortcut is `Ctrl+K`.
- `SendKeys` (Windows PowerShell) loses Ctrl+letter chords on this machine; automation uses `keybd_event`, and only after checking that the dev window really is in the foreground.

**Not yet tried by hand:** Quick Look with real photos, videos and Office files, its window on a second monitor, batch rename across search results (items in several folders), the palette with the mouse.

**M6: power features, second part (done).**
- Dual pane, per tab: `Ctrl+\` (the command bar button, View ▸ Dual pane, the palette) splits the current tab, and the right pane starts in the same folder. Each pane has its own history, layout, columns, sort and selection; the tab strip shows a split marker, and a split tab moves to another window whole. The active pane has an accent line along its top, and the address bar, search box, status bar, sidebar and preview pane follow it. `Tab` switches panes, as in two-pane file managers. `Alt+F5` copies and `Alt+F6` moves the selection into the other pane's folder (also on the item menu and in the palette), and "Open in other pane" opens a folder there. Drags between the panes work like any drag.
- Transfer queue: paste, drags that start in Explorer+ (between panes, windows, tabs and the sidebar) and copies to the other pane run one at a time, app-wide. Each still uses `IFileOperation` with Windows' own progress, conflict and elevation dialogs, and each is one undo step. A status-bar button shows "1 transfer running, 2 queued"; queued jobs can be started now or cancelled. Settings ▸ Files and folders ▸ "Queue copies and moves" (on by default) turns the queue off. Drops from other apps are left to the shell's drop target as before, because some sources (archivers, browsers) delete the files they offered as soon as the drop returns. For a queued move drop, the source is told nothing happened (`DROPEFFECT_NONE`), so it never deletes anything itself.

- Instant search (decided with the user: **an index service only**. No admin rights for the app itself, and no instant search without the service). Settings ▸ Instant search ▸ "Turn on instant search" runs `ExplorerPlus.IndexService.exe --install` elevated (one UAC prompt). It copies the service's files to `%ProgramFiles%\Explorer+ Index` (Explorer+ itself lives in the user's writable AppData, and a LocalSystem service must never run a file a normal process could replace), creates the `ExplorerPlus.Index` service (LocalSystem, automatic start, restart on failure) and starts it. The service reads every fixed NTFS volume's MFT with `FSCTL_ENUM_USN_DATA`, one reader per volume GUID, then follows the USN journal (polled every 0.5 s; it scans again if the journal was reset or wrapped). The index is flat arrays per MFT record (parent, sequence, attributes, a slice of one name pool), about 50 bytes per item including the name. Searches run in parallel: 5 to 40 ms for 2 million names in a synthetic benchmark, about 90 ms for a one-letter query. Explorer+ asks over `\\.\pipe\ExplorerPlus.Index`, which signed-in local users can open but the network can't (only SYSTEM may create server instances). Each search runs impersonating the caller: the scope is opened, and every result's parent folder is checked, with the caller's own rights, so a user only sees what they may list. Query syntax: words are ANDed and match anywhere in the name, `*` and `?` match the whole name, a word with `\` matches the full path, `"quoted text"` keeps spaces, `!word` excludes. The best matches come first (the whole name, then names that start with the word, folders before files, shorter names). At most 1,000 are listed (statting 5,000 hits on a spinning disk took over 10 s), and the status bar says when there were more ("1,000 items (the best of 68,072 matches)"). Hidden and protected items follow the "show hidden" settings, including everything inside hidden folders. The Recycle Bin and System Volume Information are never searched. In the app, Enter in the search box uses the index when the service runs and the folder is on an indexed drive. Queries in Windows Search syntax (`kind:`, `date:`) still go to Windows Search. On instant results the box holds the query, and typing searches again 150 ms after the last key, in place (no history step). Results tabs come back with the session (`explorerplus-search:` URLs). "Turn off and remove" (and uninstalling Explorer+, with a UAC prompt) stops and deletes the service and its folder. After an Explorer+ update, Settings offers "Update the service" when the service is older.

Fixed along the way: folder thumbnails drew as a white or black square, or as a tiny folder in the middle (`app`, `Games`, `Program Files`, `Users`, `XboxGames` on C:\). `IShellItemImageFactory` returns some images with straight rather than premultiplied alpha and white in the transparent pixels, and Windows' thumbnail cache holds broken folder thumbnails (fully opaque on black, or a few pixels wide). `ThumbnailExtractor` now premultiplies when needed, and for plain folders (not zip files) it falls back to the folder's icon when the thumbnail has no transparent outline or fills less than half the canvas.

Instant search, verified on this machine (the service installed with UAC, then updated twice): the three NTFS drives read in 11 s, 31 s and 45 s on first start (C:\ 2.6 million items, about 181 MB; D:\ and E:\ are slower disks), and in 2 to 6 s after a restart. Searches over the pipe take 15 to 110 ms (name words, `*.sln`, path words, a folder scope), and broad ones about 0.7 s. A file created, renamed and deleted shows up in search within a second each time (USN journal). In C:\Users only the profiles the user may open come back. In the app (dev build): Enter in a folder opens instant results with location, size and date, typing searches again in place, a This PC search covers every drive, and the status bar shows "the best of 68,072 matches". Found and fixed: `BY_HANDLE_FILE_INFORMATION` needed `Pack = 4` (its FILETIMEs), or the root folder id came out wrong and every drive failed. The first broad search was slow on spinning disks (each result's folder opened as the user to check access, and each hit statted by the app): access answers are now cached per user and folder for 5 minutes, slow searches are logged, and the app lists at most 1,000 results. Covered by tests (`IndexTests`): the index, query language, ranking, scope, hidden folders, access filtering, name-pool compaction, the protocol and the native struct layout. **Not yet tried by hand:** the Settings buttons themselves (Turn on, Update, Turn off and remove; the same `--install` / `--uninstall` were run directly), removal on uninstalling Explorer+, a second Windows user, results tabs restored from the session.

Verified on a dev build in an isolated profile, driven with real key and mouse input: C:\ in large and medium icons with the fixed folders, splitting a tab, the right pane navigating on its own, `Tab` between panes, `Alt+F5` copy and `Alt+F6` move with `Ctrl+Z` undo, a mouse drag of a file from the left pane to the right one (a queued move) and its undo, and two large copies queued behind each other ("1 transfer running, 2 queued"). Covered by tests: alpha premultiplying and the folder-thumbnail check.

**Not yet tried by hand:** the transfers button's menu (Start now, Cancel), drags between two windows and onto the sidebar or a tab, right-drags (they stay with the shell and show its menu), the column menu and header drags in the right pane, grouping and view changes in each pane, Quick Look and the preview pane while switching panes.

**M6, finished (13 Nov 2026).** SFTP/WebDAV/S3 locations and the plugin API moved to M7 (decided with the user).
- Instant folder sizes from the index. After the name scan, the service reads each volume's `$MFT` itself (`MftReader`: record 0's data runs, update-sequence fixups, the unnamed `$DATA` size and the `$STANDARD_INFORMATION` modified time, credited to the base record), at background I/O priority: C:\ 6 s, D:\ 16 s, E:\ 42 s on this machine. Every folder holds the total of everything below it, kept right as the USN journal reports changes (files whose data or times changed are reopened by file id for their new size). The app asks for all visible folders in one request (protocol v2, `FolderSizesRequest`, at most 10,000 paths) and only measures what the index can't answer (network, FAT, removable). Checked against a real walk to the byte on `C:\Program Files\dotnet` and Documents. A file with several hard links counts once, in the folder of the name the journal reports, so totals for folders with hard links can differ from a walk (on this machine `D:\Windows`, msys64 and the installer output folder).
- Search hits carry size and modified time from the index, so results show them without touching the disk. "Search all drives" (`Ctrl+Shift+F`, the palette) opens a This PC search.
- Workspaces (More ▸ Workspaces, the palette): save the tabs of a window, with their right panes, under a name; opening one restores them in a new window. Saving under an existing name updates it.
- Git status column (category "Explorer+" in the column picker): `git status --porcelain=v1 -z --ignored=matching` once per repository, cached for 3 s and dropped on any change notification. Files show Modified, Staged, Added, Renamed, Deleted, Untracked, Ignored or Conflict; folders show the most pressing state inside them. Nothing shows outside a repository or without git on PATH.
- Tags: tag files and folders from the item menu (Tags ▸), More ▸ Tags or the palette ("Tag selected items…", "Tag selected items: X"). Tags live in settings, follow renames made in Explorer+ (including a folder's contents), show in a Tags column, and get a sidebar section; a tag opens a view of its items (`explorerplus-tag:` URLs). Deleting a tag never touches the items. The shell's own "Tags" (`System.Keywords`) column is now called Keywords.
- Duplicate finder (More, the palette; the selected folder or the current one): files are grouped by size, then by a SHA-256 of their first 64 KB, then of the whole file. Hard links to one file count once, junctions aren't followed, and cloud placeholders are skipped so nothing gets downloaded. Results stream into a tab grouped by set (biggest files first), with "N files in M sets · X in extra copies" in the status bar. Deleting a copy removes it from the view, and a set with one file left goes away.
- Folder compare & sync (More, the palette): the two panes, or the current folder and a selected one, or any two folders. Shows what is only on one side, newer on one side, or different (same date, different size; "Compare contents" also reads files of equal size). Sync left to right, right to left (optionally removing what is only in the target) or both ways (newer wins, clashes left alone). It runs as one `IFileOperation`: what gets replaced or removed goes to the Recycle Bin first, then each copy lands in its folder; one `Ctrl+Z` undoes it all, `Ctrl+Y` redoes it.

Fixed along the way:
- Settings were never saved on a fresh profile: the window position starts as `NaN`, which `System.Text.Json` refuses to write, so every save failed silently until the first window closed. Named floating-point values are now allowed.
- Undo of a delete never restored anything: the shell reports a recycled item as its `$R…` file on disk, which has no Restore verb. Undo now looks up the Recycle Bin's own item for it (`RecycleBin.BinItemsFor`).
- A window opening straight into an icon view could stay blank: the items arrived before the wrap panel was told, and nothing measured it again. The panel now watches its owner's items and measures again once loaded.

Verified on a dev build in an isolated profile, driven with real mouse and key input: instant folder sizes on C:\, E:\ and Program Files, `Ctrl+Shift+F` results with size, date and location, saving, updating and opening a workspace with both panes, the Git status column in this repository, tagging from the item menu and More ▸ Tags ▸ New tag, the sidebar tag view and untagging from it, finding duplicates and deleting copies from the results, comparing two folders, a left-to-right and a both-ways sync, and their undo and redo. Covered by tests: MFT record parsing, folder totals following changes, the v2 protocol, git status parsing and folder roll-up, the duplicate finder, compare states, sync plans, a sync through the real shell, and restoring a recycled file. **Not yet tried by hand:** the index sizes after a service restart on a busy disk, tags on network paths, a duplicate search over a whole drive, sync with elevation prompts or locked files, a workspace with many tabs on a second monitor.

**Next: GitHub issues (planned 13 Nov 2026; #13 to #15 added the same day, #16 to #25 later that day, after I1).** Work from the [issue list](https://github.com/jdc68/explorer-plus/issues), in this order. Quick fixes share one step; bigger problems get a step each. Close each issue when its step is verified. When #16 to #25 came in, the steps after I1 were renumbered: a second batch of quick fixes became I2, the old I2 to I10 moved down, and new steps were added for "Show in folder" from other apps, smooth scrolling and the built-in hover preview.

**I1: quick fixes.**
- [#3](https://github.com/jdc68/explorer-plus/issues/3) Home as the start page. `AppSettings.StartLocation` already defaults to Home, but settings files from before M3 kept This PC. Bump `AppSettings.Format` to 2 and switch `StartLocation` to Home once; Settings still lets the user change it afterwards.
- [#6](https://github.com/jdc68/explorer-plus/issues/6) Pin to Quick access. The native "Pin to Quick access" verb (`pintohome`) writes to File Explorer's Quick access, so nothing changes in Explorer+. Intercept it (as `FolderVerbs` does for other verbs) and route it to our own pins (`AppSettings.PinnedFolders`), so the two lists stay independent. Merge it with our "Pin to sidebar" entry under one name. Add "Unpin from Quick access" everywhere a pin shows (item menu, sidebar, Home), including the standard folders (Desktop, Downloads…), which need a "hidden standard folders" list in settings.
- [#5](https://github.com/jdc68/explorer-plus/issues/5), first part: the gaps between items in the More menu. The redesign of every popup menu is I9.
- [#12](https://github.com/jdc68/explorer-plus/issues/12) Windows stacking on the taskbar. Every window shares the process AppUserModelID (`JumpLists.AppId`), so they should already group. Check on the installed copy whether it reproduces, and whether the Start menu shortcut, a pinned taskbar icon and a dev profile (`ExplorerPlus.<profile>`) end up with different IDs. Close it if it is only the taskbar's "combine buttons" setting.
- [#11](https://github.com/jdc68/explorer-plus/issues/11) New icon: the blue folder with the white clip from the issue. Build `Assets/ExplorerPlus.ico` with 16, 20, 24, 32, 40, 48, 64 and 256 px frames, simplify the clip at small sizes, and check it in the taskbar, title bar, Start menu, jump list and installed apps.
- [#10](https://github.com/jdc68/explorer-plus/issues/10) README: what Explorer+ is, install (MSI, winget), the main features with their shortcuts, tips, Windows integration (default file manager, background mode, instant search service) and its known limits. Short, so the useful parts are easy to find; PLAN.md stays the detailed record.

I1 done (13 Nov 2026, 0.1.9):
- Settings format 2 switches `StartLocation` to Home once.
- Windows' `pintohome` / `unpinfromhome` verbs are always hidden, even with Shift. "Pin to Quick access" / "Unpin from Quick access" (item menu, sidebar, Home's cross-folder menu) use Explorer+'s own pins. Unpinning a standard folder adds it to `HiddenQuickAccess`, and pinning it again removes it from there.
- The More menu was the classic WPF menu, not Fluent's. `BasedOn="{StaticResource {x:Type ContextMenu}}"` in App.xaml runs before the Fluent theme is loaded, so it found the classic style: a light background with dark-theme separators and grey text on it, which read as gaps. The ContextMenu and ToolTip font styles are now built in code after `ThemeMode` is set (`App.UseTextFontInPopups`), based on Fluent's own. Every `MenuEntry` gets an icon slot, so entries line up.
- New icon (all sizes 16 to 256), and the README.
- #12 doesn't reproduce: two windows of one process share a taskbar button, and the pinned taskbar shortcut and the Start menu shortcut both carry the `ExplorerPlus` AppUserModelID. Left open for the user to say how they saw it.

Verified on a dev build in an isolated profile: an old-format settings file opened on Home and was rewritten as format 2; the More menu in Fluent dark style; Windows' pin entry gone from the native menus; unpinning Videos (from Home's menu) and pinning `.chocolatey` (from a folder's menu), both stored and shown in the sidebar and Home at once. Not tried by hand: the new icon in the Start menu and installed apps after the install, and switching Light/Dark in Settings while a menu style is in use.

**I2: quick fixes, second batch.**
- [#19](https://github.com/jdc68/explorer-plus/issues/19) A newly opened folder keeps the previous folder's scroll position. Scroll to the top on every new navigation (in both panes and every view mode); a refresh or a live update keeps the current one.
- [#20](https://github.com/jdc68/explorer-plus/issues/20) The Settings window gets only a close button. Minimizing it made it vanish, and More ▸ Settings then only activated the hidden window. Drop minimize and maximize, and when Settings is asked for while open, restore and activate it.
- [#18](https://github.com/jdc68/explorer-plus/issues/18) Cut items look cut: after `Ctrl+X` (or Cut in a menu) the items are drawn half transparent, as in Explorer, until they are pasted, the clipboard changes, or the cut is cancelled. Follow the clipboard (`AddClipboardFormatListener`) so a cut made or replaced elsewhere also shows, in every tab and window.
- [#17](https://github.com/jdc68/explorer-plus/issues/17) Item checkboxes show only on the hovered item and on selected items (checked), as in Explorer, instead of on every row and tile.
- [#12](https://github.com/jdc68/explorer-plus/issues/12) stays open until the user says how the windows ended up apart (see I1).

I2 done (13 Nov 2026):
- #19: the tab already asked for a scroll to the top on every new folder, but it did nothing. Once the old items are cleared the `ScrollViewer` reports offset 0 and skips `ScrollToVerticalOffset(0)`, while the `VirtualizingStackPanel` still holds the old offset and goes back to it (clamped to the new folder's end) when the new items arrive. `ResetScroll` now sets the items panel's own `IScrollInfo` offsets to 0, before the old items are cleared and again when the new ones show. Back and Up keep selecting the folder you came from and scroll to it.
- #20: Settings has no minimize or maximize button (`WS_MINIMIZEBOX` / `WS_MAXIMIZEBOX` removed, still resizable), and asking for it while it is open restores and activates it.
- #18: `Services/CutItems` follows the clipboard (`AddClipboardFormatListener` on a message-only window). When it holds files with a move drop effect, those items are drawn at the hidden-item opacity in every tab and window, also when the cut came from File Explorer; a copy, a paste or anything else on the clipboard clears it.
- #17: item checkboxes (when turned on in Settings) show on the hovered item and on selected items only, and keep their space so names don't move.

Verified on a dev build in an isolated profile, driven with real mouse input: scrolling down a 150-folder test folder and opening a folder near the end now lands at the top (it landed at the bottom before the fix); a cut file fades, and cutting another file un-fades the first; checkboxes on the selected rows and the hovered row only. Not tried by hand: the Settings window's buttons (the session had to stop driving the desktop), the scroll reset in icon views, a cut made in File Explorer.

**I3: views that don't draw, and smarter view picking.**
- [#4](https://github.com/jdc68/explorer-plus/issues/4) The Videos folder stays empty until the window is resized; a context menu or other actions don't help. Likely the same family as the M6 fix (items arriving before the wrap panel knows them), this time on a view switch inside an open window: content-aware rules move Videos to large icons after listing. Reproduce in a dev profile, find what skips the measure, and add a regression check.
- [#8](https://github.com/jdc68/explorer-plus/issues/8) Smart items display. Content-aware views exist since M2 (`ContentViewRules`, 60% threshold), so first find out why they didn't kick in for the user: a hand-set view in `views.json` winning, the setting off, folders counted against the threshold, or the view picked but not drawn (#4). Then tune: videos and pictures to medium or large icons, documents and code to details, music to details, mixed folders to details. Make the rules visible and editable in Settings, as section 5 promises.
- [#22](https://github.com/jdc68/explorer-plus/issues/22) Music folders lose the #, Title, Contributing artists and Album columns (decided by the user; the screenshot shows voice recordings, where they are all empty). Music keeps Name, Length and the usual date, type and size columns; the removed ones stay in the column picker. Section 5 is updated to match.
- [#23](https://github.com/jdc68/explorer-plus/issues/23) Picture folders get one "Date" column: Date taken when the photo has it, Date created otherwise, sorted and grouped by that value. It replaces the separate Date taken column in the picture view; the other dates stay in the column picker.

**I4: folder sizes that don't show.**
- [#1](https://github.com/jdc68/explorer-plus/issues/1) Some non-empty folders show no size. Find which path misses them: index answers (`FolderSizesRequest`), the fallback walk (`FolderSizeService`), or the lazy loading for visible items (`FolderSizeWatcher`). Suspects: folders the index doesn't cover or answers with nothing (reparse points, hard links, access denied, folders created after the scan), answers that arrive after the row was recycled by virtualization, and errors swallowed on the way. Log every folder that ends with no size and why, fix each cause, and add tests where the index or walk is at fault.

**I5: the drag image that stays on screen.**
- [#14](https://github.com/jdc68/explorer-plus/issues/14) After a file is dragged from Explorer+ into another app (Telegram in the screenshot), the drag image stays on screen after the drop, still labelled "Move to kat_song". That label is the drop description from Explorer+'s own window, so the drop-target helper there was likely entered and never left: `MainWindow.DragDrop` calls `IDropTargetHelper.DragEnter` once (`_helperEntered`) and only calls `DragLeave` on what `OnWindowDragLeave` counts as a real exit from the window, and the hovered folder's shell drop target gets its `Leave` the same way. Reproduce with Telegram, a browser and another Explorer+ window as targets, make sure every enter has a matching leave or drop (also when the pointer leaves fast or over a child window), and clear the drop description when the drag leaves. Check that dragging to File Explorer and to the desktop still shows the right labels.

**I6: "Show in folder" from other apps (slow, doubled, no selection).**
- [#16](https://github.com/jdc68/explorer-plus/issues/16) IntelliJ's "Open in Explorer" opened Explorer+ at once, then File Explorer on the same folder about 10 s later.
- [#21](https://github.com/jdc68/explorer-plus/issues/21) A browser's "Open containing folder" took 10 to 15 s to show Explorer+, and the downloaded file wasn't selected.
- So the default file manager registration does reach Explorer+ from these apps (the first real report of it, see M4), but the callers expect more. Both most likely go through `SHOpenFolderAndSelectItems` (browsers call it directly, IntelliJ runs `explorer.exe /select,`): the shell opens the folder through its default verb, then waits for a shell window showing that folder to select the item in. Explorer+ windows aren't in the shell's window list (`IShellWindows`), so the wait runs into a timeout and Explorer opens its own window, and nothing gets selected.
- Find out first, from an Explorer+ started normally (not from Claude, see M4 and the MSIX note in CLAUDE.md), with the user's help: which call each app makes (Process Monitor on the command lines), where the 10 s go, and whether File Explorer opens in both cases.
- Then fix it, best option first: register each Explorer+ window in `IShellWindows` (`RegisterPending` / `Register`) with an object that answers `IServiceProvider` → `IShellBrowser` / `IFolderView` for its current folder, so the shell finds a window for the folder and selects the item in it, as it does with Explorer. Check that HoverPreview still sees Explorer+ windows as its own kind (it goes by process first). Fallbacks: a `/select,` aware verb command so the selection travels on the command line, or a short window after a verb launch in which Explorer+ selects the newest item of the folder.
- Done means: both apps open one Explorer+ window within half a second (background mode on), with the file selected, and File Explorer never opens.

**I7: GitHub releases.**
- [#13](https://github.com/jdc68/explorer-plus/issues/13) Publish each chosen version as a GitHub release with the MSI attached. Add a `-Release` switch to `installer/build.ps1`: after a successful build, tag the commit `v<version>`, run `gh release create` with the MSI and short notes (the commits since the last tag), then pass the release asset's URL as `-InstallerUrl` so the winget manifests are complete. Every `build.ps1` run bumps the patch version, so not every build becomes a release: only on request, and each one asks the user first, since a release is public. The README's Install section then points to the latest release. Decide with the user: who writes the release notes, and whether the winget manifests get submitted too.

I7 done (13 Nov 2026, ahead of I3 to I6 at the user's request): `installer/build.ps1 -Release [-Notes "..."] [-Yes]`. Before anything changes it checks that gh is signed in, the working tree is clean and HEAD is on a branch, then shows the tag and the notes and asks (in a non-interactive shell the answer is no, and nothing is changed). The notes are the `-Notes` text, then the commit subjects since the last `v*` tag, then an install line. After the build it commits the version bump ("Release 0.1.11"), pushes the branch, pushes an annotated `v<version>` tag and runs `gh release create --verify-tag` with the MSI. The winget manifests get the release asset's URL, so they are complete, but submitting them to winget-pkgs is still by hand. The README's Install section points to the latest release. Still open: the first real release (the user says when), and whether notes should be written by hand rather than taken from commit subjects.

**I8: search while typing.**
- [#7](https://github.com/jdc68/explorer-plus/issues/7) Typing in the search box filters the current folder, and only Enter starts a search. Make typing search too: keep the instant filter for the current folder, and after a short pause (about 300 ms) with no key, open the search (instant search when the service covers the folder, Windows Search otherwise). Instant-search tabs already re-search 150 ms after the last key. Decide with the user: whether the results replace the current tab or open as a results view that Esc or clearing the box leaves, a minimum query length, and how Windows Search's slower results avoid running on every pause.

**I9: themed menus and popups.**
- [#5](https://github.com/jdc68/explorer-plus/issues/5), second part: every WPF menu and popup (More, column header menu, history, Tags, the transfers button, our own cross-folder item menu, tab menu) gets one designed style: Fluent spacing, icons in one column, shortcuts right-aligned, separators, hover and pressed states, rounded corners and shadow, and colours that follow light, dark and accent changes live. The native shell context menus stay Windows' own. Check the other `BasedOn="{StaticResource {x:Type Button}}"` styles in App.xaml (`IconButton`, `CommandButton`) too: like the ContextMenu style fixed in I1, they probably build on the classic style instead of Fluent's.

**I10: smooth scrolling.**
- [#24](https://github.com/jdc68/explorer-plus/issues/24) Smooth scrolling in the file list, as a setting (Settings ▸ General ▸ "Smooth scrolling", on by default). Mouse wheel and Page Up / Page Down ease to the target offset over about 150 ms instead of jumping, and wheel ticks that arrive while it moves extend the target. Scroll by pixel in the details view (`VirtualizingPanel.ScrollUnit="Pixel"`, keeping virtualization and recycling) and in `VirtualizingWrapPanel`; a touchpad's precise scrolling stays 1:1. Check frame times on a 100,000-item folder (section 8) so the animation never makes scrolling slower. Off gives today's scrolling back.

**I11: a better This PC page.**
- [#2](https://github.com/jdc68/explorer-plus/issues/2) Drives as tiles: icon, label and letter, a usage bar (accent colour, red past 90% full), and "X free of Y". Group drives, network locations and devices, keep one entry per volume (section 5), and keep the details view with Total size / Free space as a choice. The bar should also follow free-space changes while the page is open.

**I12: frequent folders in the sidebar.**
- [#15](https://github.com/jdc68/explorer-plus/issues/15) Show frequently opened folders under Quick access, as File Explorer does. Not built yet: section 5 cut "auto-added frequent folders" (M3), and this issue brings them back, so they become a setting (Settings ▸ Navigation pane and Home ▸ "Show frequent folders"). Explorer+ keeps its own history, separate from File Explorer's, as with the pins (#6): a visit count and last visit per folder, ranked by how often and how recently (today's `RecentFolders` keeps only the last 10, for the jump list). Show the top few below the pins, with "Pin to Quick access" and "Remove from frequent" on right-click, and the same list in Home's Quick access section. Leave out the Recycle Bin, temp folders, search and tag views, and drives that are offline. Decide with the user: on or off by default, how many to show, and whether to start from File Explorer's own frequent list (readable through the Quick access shell folder) or empty.

**I13: hover preview built in.**
- [#25](https://github.com/jdc68/explorer-plus/issues/25) HoverPreview (`E:\Programming\Previewer`, a separate C++ exe) becomes a feature of Explorer+, with nothing extra to install, and a setting to turn it off (Settings ▸ Preview ▸ "Preview on hover"). Inside Explorer+ it needs none of HoverPreview's machinery: the list knows the item under the mouse, so no UI Automation polling and no `IShellWindows` lookup.
- Same behaviour as the tool: rest the mouse on a photo, video, song or text file and a popup opens next to the cursor after the hover delay (Fast 0.15 s, Normal 0.3 s, Relaxed 0.7 s). Moving away, clicking, a key or Esc closes it, and the next one opens almost at once while gliding over the list. Photos scaled to fit and rotated from EXIF, with a checkerboard under transparency, and animated GIFs playing. Videos play muted or with sound (a setting) and loop, with a progress bar. Music plays with a card showing the album art. Text shows the first lines (64 KB at most) with encoding and line count. Settings: on/off, delay, size (Small, Medium, Large), sound in videos.
- Reuse what Explorer+ already has (the preview pane's image and text loaders, `PreviewHandlers`, the `Preview` STA pool), and play media with Media Foundation (`MediaElement` or a small MF player), created per preview and released afterwards, so idle memory stays as it is. The item's own tooltip stays closed while a preview shows.
- Decide with the user: whether Explorer+ should also preview over File Explorer and the desktop while it runs in the background (that would replace the tool completely, and needs its UI Automation polling), and how the two avoid double popups while both are installed (Explorer+ could tell HoverPreview to skip its windows, or the tool's next version could step aside when the built-in one is on). Keep the `AutomationId` on items either way (section 5).

**I14: Save and Open dialogs (research first).**
- [#9](https://github.com/jdc68/explorer-plus/issues/9) Browsers and other apps open Windows' own Save and Open dialogs (the common item dialog, `IFileDialog`), which run inside those apps and use Explorer's code. The default-file-manager registration can't change that (section 4, known limits), and replacing the dialog itself would mean injecting into every process, which is off the table. Options to weigh with the user before building anything:
  - A dialog helper: Explorer+ notices a file dialog in the foreground (UI Automation or a `WinEvent` hook, no injection) and offers its open folders, pins and recent folders; picking one sends the path to the dialog's address field. This is how Listary and similar tools work.
  - A hotkey in the dialog that jumps it to the folder in the active Explorer+ window.
  - "Show in folder" after a download is a different path (`SHOpenFolderAndSelectItems`, or the browser starting `explorer.exe`), handled in I6.

**M7: remote locations and plugins (postponed; future plan, not scheduled).** Comes after the issue steps above.
- SFTP, WebDAV and S3 as locations in the sidebar and address bar, listed and browsed like folders, with copy, move and delete going through the same queue.
- A plugin API: columns, preview handlers, item menu commands and locations from separate assemblies.

## 7. Architecture

```
src/ExplorerPlus.Interop   hand-written P/Invoke + [ComImport] shell interfaces (IShellItem2, IShellFolder,
                           IContextMenu2/3, IFileOperation, IShellLinkW …)
src/ExplorerPlus.Indexing  no WPF, no shell: VolumeIndex (flat per-record arrays, parallel search, folder totals), IndexQuery,
                           VolumeReader (MFT enumeration + USN journal), MftReader (raw $MFT sizes and times), IndexHost (volumes, impersonated search), IndexProtocol, IndexClient
src/ExplorerPlus.IndexService  LocalSystem Windows service (raw advapi32, no NuGet): ServiceRunner, PipeServer, ServiceInstaller
src/ExplorerPlus.Core      no WPF: Pidl, ShellLocation, FolderEntry, FastFolderEnumerator, ShellFolderEnumerator,
                           FolderLoader (streaming), StaWorkerPool, ShellInfoCache, FileOperations, ShellContextMenu,
                           ShellClipboard, ShellLauncher, DriveService, ShellFilter, PathParser, ExplorerCommandLine
                           M2: FolderSizeService, ThumbnailExtractor, PropertyReader, PreviewHandlers, FileOpLog
                           M3: HomeFeed, SearchLocation, ShellNewTemplates, Archives, RecycleBin, NetworkDrives
                           M4: Integration/DefaultFileManager
                           M5: BatchRename, SizeFormat, Navigation/FuzzyMatch
                           M6: IndexSearch (instant-search location and loader), GitStatus, TagView, DuplicateFinder,
                               FolderCompare (compare and sync plans)
src/ExplorerPlus.App       WPF, .NET 9 Fluent theme: MainWindow (+ .DragDrop, .Columns), AddressBar, Themes/FileList.xaml,
                           MainViewModel / TabViewModel / SidebarViewModel / PaneViewModel, VirtualizingWrapPanel,
                           PreviewHost, IconCache, ThumbnailService, FolderViewStore, UndoService, ShellChangeWatcher,
                           SingleInstance, AppSettings (%AppData%\ExplorerPlus\settings.json), Stars, QuickAccess
                           M4: MainWindow.Tabs (tab drag, title bar), SettingsWindow / SettingsViewModel, JumpLists
                           M5: MainWindow.Commands (palette, Quick Look, batch rename), QuickLookWindow, BatchRenameWindow
                           M6: MainWindow.Panes (dual pane, transfers button), TransferQueue, IndexServiceSetup,
                               Workspaces, Tags, PromptWindow, CompareWindow
installer/                 WiX 5 per-user MSI (Package.wxs), build.ps1, winget manifest templates
tests/ExplorerPlus.Core.Tests   xUnit: enumerator parity, PathParser, command line, drives, file operations,
                           archives, Home, search, templates, default file manager registration,
                           batch rename, size format, fuzzy match, thumbnail alpha, instant-search index, MFT records,
                           git status, duplicates, folder compare and sync, Recycle Bin restore
```

Why WPF on .NET 9:
- Mature list virtualization.
- A Windows 11 Fluent theme with light/dark modes.
- Easy COM interop.

WinUI 3 is the stack Microsoft is still fixing for Explorer, and the Files app shows its cost. C++/Win32 is faster but much slower to build in.

## 8. Performance budget (tested every milestone)

| Scenario | Target | M1 measured |
|---|---|---|
| Cold start to usable window | < 400 ms | 1.0–1.4 s cold (M4, R2R); 0.2–0.3 s when running in the background, 0.4 s for the first window after a background start |
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
