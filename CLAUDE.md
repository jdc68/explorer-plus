# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Explorer+ is a Windows 11 File Explorer replacement (WPF on .NET 9, Windows only). [PLAN.md](PLAN.md) is the source of truth for goals, decisions agreed with the user, milestone status (M1 to M6 done; next are the steps I2 to I14 built from the GitHub issues (I1 is done); M7, remote locations and plugins, is postponed) and the performance budget. Read it before adding features. Its section 5 lists choices that must not be undone (no OneDrive integration, drives deduplicated by volume GUID, hidden context-menu entries, content-aware views).

## Commands

Target framework `net9.0-windows10.0.22621.0` is set for every project in [Directory.Build.props](Directory.Build.props). Nullable is on, unsafe code is allowed, and the warnings `WPF0001` and `CA1416` are suppressed solution-wide.

```bash
dotnet build ExplorerPlus.sln
```

```bash
dotnet test tests/ExplorerPlus.Core.Tests
```

```bash
dotnet test tests/ExplorerPlus.Core.Tests --filter "FullyQualifiedName~FileOperationTests.Copy_log_reports"
```

```bash
dotnet run --project src/ExplorerPlus.App
```

```bash
powershell -File installer/build.ps1
```

- `installer/build.ps1` publishes a framework-dependent ReadyToRun build to `artifacts/publish/<arch>`, builds the per-user MSI (WiX 5 from NuGet, not part of the solution) into `artifacts/installer`, and writes winget manifests to `artifacts/winget/<version>`. Pass `-InstallerUrl` with the MSI's public URL before submitting the manifests.
- Without `-Version`, `build.ps1` bumps the patch version (above both `Directory.Build.props` and the installed copy) and writes it back to `Directory.Build.props`. `-Install` then installs the MSI and fails unless exactly one Explorer+ is installed, at that version, with the files just published.
- `-Release` publishes the build as a public GitHub release (#13): on a clean working tree it shows the version and notes (`-Notes` text, then the commit subjects since the last `v*` tag) and asks, then commits the version bump as "Release <version>", pushes the branch, tags and pushes `v<version>`, and runs `gh release create` with the MSI; the winget manifests get the asset URL. Releases are public, so only on the user's request, and pass `-Yes` only after they said yes in chat. Windows PowerShell turns redirected native stderr into terminating errors under `$ErrorActionPreference = "Stop"`, so the script leaves git's and gh's stderr unredirected.
- **After every change to the app, finish with `powershell -File installer/build.ps1 -Install -NoStart`** so the installed copy is the latest build. It quits the running Explorer+ to replace its files. `-NoStart` keeps the MSI from starting Explorer+ again from inside the Claude app's sandbox (see the MSIX note below); tell the user it starts again from the Start menu or at the next sign-in.

- The tests use the real shell (`IFileOperation`, PIDLs, STA threads) on real temp folders, so they need an interactive Windows session.
- A running `ExplorerPlus.exe` locks the DLLs in `src/ExplorerPlus.App/bin`, and the build then fails with MSB3026 copy retries. Close the app first, or run a dev build from a copied folder (`artifacts/dev/` is used for that and is gitignored).
- Never turn on the default file manager (Settings, `--register-default`) on the user's machine to test something without asking: it changes how Windows opens every folder. `DefaultFileManagerTests` use a scratch key under `HKCU\Software\ExplorerPlusTests`.
- The Claude desktop app is an MSIX package: HKCU writes from the shells it starts, and from anything launched from them (including Explorer+), land in a per-package registry overlay that Explorer, Windows Installer and other apps never see. Shell-integration checks (default file manager, Win+E, "Show in folder") can't be done from here; the user has to run them from an Explorer+ started normally.
- Set `EXPLORERPLUS_PROFILE=<name>` to run a dev build next to the user's everyday instance. It gets its own settings folder (`%AppData%\ExplorerPlus\profiles\<name>`) and its own single-instance mutex and pipe, so it neither forwards its arguments to the running instance nor touches its settings. Use it whenever you launch the app to check a change.
- When driving a dev build with synthetic keys, the Claude app usually keeps the foreground: check that the Explorer+ window is in the foreground before every key you send, or the keys land in the Claude app. Use `keybd_event`, not `SendKeys` (it loses Ctrl+letter chords on this machine), and capture right after the keys, because popups close when Explorer+ loses the foreground. `Ctrl+Shift+P` never reaches any app on the user's machine.
- `logs/` (gitignored) holds build and test output from earlier sessions.

## Architecture

Layers, with dependencies pointing down only (App → Core → Indexing → Interop; the service uses Indexing and Interop):

- **`ExplorerPlus.Interop`**: hand-written P/Invoke and `[ComImport]` shell interfaces (`IShellItem2`, `IShellFolder`, `IContextMenu2/3`, `IFileOperation`, preview handlers, and so on). No NuGet interop packages. Add new native signatures here.
- **`ExplorerPlus.Indexing`**: the instant-search index (MFT enumeration, USN journal, in-memory index and search, pipe protocol and client). No shell or WPF. Used by the service and by Core.
- **`ExplorerPlus.IndexService`**: the LocalSystem Windows service that hosts the index and serves `\\.\pipe\ExplorerPlus.Index`; also its own installer (`--install` / `--uninstall`, run elevated from Settings), which copies it to `%ProgramFiles%\Explorer+ Index`. `--console` runs it in an elevated process for debugging. It ships next to `ExplorerPlus.exe` (the App references the project). Its log is `service.log` in its Program Files folder.
- **`ExplorerPlus.Core`**: shell logic with no WPF references. It is testable on its own and `InternalsVisibleTo` the test project.
- **`ExplorerPlus.App`** (assembly `ExplorerPlus.exe`): WPF with the .NET 9 Fluent theme, MVVM through a small `Infrastructure/Observable.cs` (no MVVM framework). `MainWindow` is split into partial files (`.DragDrop.cs`, `.Columns.cs`, `.Tabs.cs`, `.Commands.cs` for the command palette, Quick Look and batch rename, `.Panes.cs` for dual pane and the transfers button). Dual pane: a tab can be split, and its right pane is `TabViewModel.Partner`. `MainViewModel.CurrentTab` is the tab-strip tab, `SelectedTab` is the active pane's view (what commands act on), `RightTab` / `IsRightActive` / `OtherPane` describe the split. In `MainWindow`, `FileList` is the active pane's list; each `FilePane` has its own `ListView`, `GridView` (the `DetailsGridView` resource is `x:Shared="False"`), column cache and layout state, and a tab's own events (selection after a load, rename after New) go to the pane that shows it. The Fluent theme has no GridView styles: a `ListView` with a `GridView` needs `FileListStyle` and `FileRowBaseStyle` from `Themes/FileList.xaml`, or it prints each item's `ToString()`. The tab strip is in the title bar (`WindowChrome`); clickable elements there need `WindowChrome.IsHitTestVisibleInChrome`. A `TabViewModel` has no window ties, so tab drag moves the object between windows (`MainViewModel.DetachTab` / `AttachTab`).

### The shell namespace is the data model

Locations are `ShellLocation` / `Pidl`, not plain paths, so virtual folders (This PC, Recycle Bin, Network, MTP, zip, WSL, Control Panel) work the same way as file-system folders. Listing is a hybrid in `FolderLoader.LoadAsync`:

- File-system folders go through `FastFolderEnumerator` (`FindFirstFileExW`) on a long-running thread-pool task.
- Virtual folders go through `ShellFolderEnumerator` (`IShellFolder::EnumObjects`) on an STA worker.
- Home goes through `HomeFeed` (its options are injected by the app through `FolderLoader.HomeOptions`).
- Instant-search results (`ShellLocation.IsIndexSearch`, `explorerplus-search:` URLs) go through `IndexSearch`, which asks the index service and stats the hits. Enter in the search box picks it over Windows Search (`SearchLocation`) when the service runs and covers the folder; on those tabs `FilterText` is the query and typing re-searches with `NavMode.Replace`.
- Tag views (`explorerplus-tag:`, `TagView`) and duplicate results (`explorerplus-dupes:`, `DuplicateFinder`) are app-made locations too: their `Pidl` is This PC, `ParsingName` is the URL, and the tab treats them as searches (no folder watch; deletes anywhere remove their items).
- All of them stream `FolderEntry` batches through a bounded channel. The first batch is small (300 items or 30 ms) so rows show at once; later batches are large.

`TabViewModel.LoadAsync` consumes that stream and cancels the previous load through `_loadCts` on every navigation. After listing, `ContentViewRules` picks a view from extension stats unless `FolderViewStore` (`views.json`) holds a view the user set by hand for that folder, which always wins. Live updates come from `ShellChangeWatcher` (`SHChangeNotifyRegister`), debounced into a refresh.

### Threading rules

- The UI thread never touches the disk and never makes shell COM calls.
- Shell COM work runs on the named STA pools in `Core/Threading/StaWorkerPool.cs`: `Enumeration`, `Icons`, `General`, `Properties`, and `Preview`. `Preview` has a single thread on purpose, because a preview handler must be created, used and released on the same thread.
- Long operations that show their own shell UI (copy or delete dialogs) use `StaWorkerPool.RunDedicated`.
- Pass the `CancellationToken` through. Work is cancelled on navigation.
- Icons, thumbnails, property columns and folder sizes load lazily and only for visible items, through the App services (`IconCache`, `ThumbnailService`, `FolderSizeWatcher` / `FolderSizeService`).

### File operations and undo

All mutations go through `Core/Shell/FileOperations` (`IFileOperation`), so elevation, conflict dialogs and the Recycle Bin behave as in Explorer. A `FileOpLog` (fed by `IFileOperationProgressSink` in `Interop/FileOperationSink.cs`) records the real result paths, and `UndoService` builds undo and redo from it. When you add an operation, log it so it can be undone; `FileOperationTests` covers the log. Copies and moves from paste, from drags that start in Explorer+ and from the other pane go through `MainViewModel.TransferAsync` and the app-wide `Services/TransferQueue` (one at a time, Settings ▸ Queue copies and moves). Drops from other apps stay with the shell's own drop target, because some sources delete their temporary files as soon as the drop returns.

### App startup and Windows integration

The entry point is `Program.Main` (`App.xaml` is built as a Page, so WPF generates no `Main`). It takes the single-instance mutex first: a second launch sends its arguments NUL-separated over a named pipe and exits before WPF, `App.xaml` or the theme load, which keeps folder links and "Show in folder" at about 0.2 s when Explorer+ is running. `--background` (no window; the sign-in entry and the MSI use it) exits at once if Explorer+ already runs, and `--quit` asks the running instance to exit and waits for it. `App.OnStartup` then handles `--register-default` / `--unregister-default` / `--uninstall` (switch the default file manager, `Core/Integration/DefaultFileManager`, and exit; `--uninstall`, run by the MSI, also removes the sign-in entry and, with a UAC prompt, the index service), sets the AppUserModelID shared with the Start menu shortcut and the jump list (`JumpLists.AppId`), and sets error mode (no "no disk in drive" dialogs), loads `AppSettings` (`%AppData%\ExplorerPlus\settings.json`, or the profile folder), and parses an `explorer.exe`-compatible command line (`ExplorerCommandLine`: `/select,`, `/e,`, `/n`, `/root,`, `shell:`, CLSIDs). Unhandled UI exceptions are appended to `errors.log` in the data folder and shown as a notice instead of crashing. The last window to close saves the session tabs and flushes the OLE clipboard so copied files stay pasteable; then, unless background mode is off or More ▸ Exit was used (`App.ExitAll`), the process stays alive with no window (`App.GoIdle`, `Services/Background`). So `App.MainWindows` can be empty while the app runs, and services must not assume a window exists.

### Hiding bloat

Two separate mechanisms hide things:

- **Native context menus** (`ShellContextMenu`, `IContextMenu`): `MainWindow.MenuFilter` passes `AppSettings.IsMenuItemHidden` as the `hide` callback. Holding Shift passes no filter and adds extended verbs, so the full menu shows.
- **Namespace items** (OneDrive, Gallery, Libraries): `ShellFilter.IsBlocked` hides them from listings and the sidebar.
