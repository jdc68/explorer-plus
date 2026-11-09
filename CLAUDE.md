# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Explorer+ is a Windows 11 File Explorer replacement (WPF on .NET 9, Windows only). [PLAN.md](PLAN.md) is the source of truth for goals, decisions agreed with the user, milestone status (M1 to M4 done, M5 next) and the performance budget. Read it before adding features. Its section 5 lists choices that must not be undone (no OneDrive integration, drives deduplicated by volume GUID, hidden context-menu entries, content-aware views).

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

- The tests use the real shell (`IFileOperation`, PIDLs, STA threads) on real temp folders, so they need an interactive Windows session.
- A running `ExplorerPlus.exe` locks the DLLs in `src/ExplorerPlus.App/bin`, and the build then fails with MSB3026 copy retries. Close the app first, or run a dev build from a copied folder (`artifacts/dev/` is used for that and is gitignored).
- Never turn on the default file manager (Settings, `--register-default`) on the user's machine to test something without asking: it changes how Windows opens every folder. `DefaultFileManagerTests` use a scratch key under `HKCU\Software\ExplorerPlusTests`.
- Set `EXPLORERPLUS_PROFILE=<name>` to run a dev build next to the user's everyday instance. It gets its own settings folder (`%AppData%\ExplorerPlus\profiles\<name>`) and its own single-instance mutex and pipe, so it neither forwards its arguments to the running instance nor touches its settings. Use it whenever you launch the app to check a change.
- `logs/` (gitignored) holds build and test output from earlier sessions.

## Architecture

Three layers, with dependencies pointing down only:

- **`ExplorerPlus.Interop`**: hand-written P/Invoke and `[ComImport]` shell interfaces (`IShellItem2`, `IShellFolder`, `IContextMenu2/3`, `IFileOperation`, preview handlers, and so on). No NuGet interop packages. Add new native signatures here.
- **`ExplorerPlus.Core`**: shell logic with no WPF references. It is testable on its own and `InternalsVisibleTo` the test project.
- **`ExplorerPlus.App`** (assembly `ExplorerPlus.exe`): WPF with the .NET 9 Fluent theme, MVVM through a small `Infrastructure/Observable.cs` (no MVVM framework). `MainWindow` is split into partial files (`.DragDrop.cs`, `.Columns.cs`, `.Tabs.cs`). The tab strip is in the title bar (`WindowChrome`); clickable elements there need `WindowChrome.IsHitTestVisibleInChrome`. A `TabViewModel` has no window ties, so tab drag moves the object between windows (`MainViewModel.DetachTab` / `AttachTab`).

### The shell namespace is the data model

Locations are `ShellLocation` / `Pidl`, not plain paths, so virtual folders (This PC, Recycle Bin, Network, MTP, zip, WSL, Control Panel) work the same way as file-system folders. Listing is a hybrid in `FolderLoader.LoadAsync`:

- File-system folders go through `FastFolderEnumerator` (`FindFirstFileExW`) on a long-running thread-pool task.
- Virtual folders go through `ShellFolderEnumerator` (`IShellFolder::EnumObjects`) on an STA worker.
- Home goes through `HomeFeed` (its options are injected by the app through `FolderLoader.HomeOptions`).
- All of them stream `FolderEntry` batches through a bounded channel. The first batch is small (300 items or 30 ms) so rows show at once; later batches are large.

`TabViewModel.LoadAsync` consumes that stream and cancels the previous load through `_loadCts` on every navigation. After listing, `ContentViewRules` picks a view from extension stats unless `FolderViewStore` (`views.json`) holds a view the user set by hand for that folder, which always wins. Live updates come from `ShellChangeWatcher` (`SHChangeNotifyRegister`), debounced into a refresh.

### Threading rules

- The UI thread never touches the disk and never makes shell COM calls.
- Shell COM work runs on the named STA pools in `Core/Threading/StaWorkerPool.cs`: `Enumeration`, `Icons`, `General`, `Properties`, and `Preview`. `Preview` has a single thread on purpose, because a preview handler must be created, used and released on the same thread.
- Long operations that show their own shell UI (copy or delete dialogs) use `StaWorkerPool.RunDedicated`.
- Pass the `CancellationToken` through. Work is cancelled on navigation.
- Icons, thumbnails, property columns and folder sizes load lazily and only for visible items, through the App services (`IconCache`, `ThumbnailService`, `FolderSizeWatcher` / `FolderSizeService`).

### File operations and undo

All mutations go through `Core/Shell/FileOperations` (`IFileOperation`), so elevation, conflict dialogs and the Recycle Bin behave as in Explorer. A `FileOpLog` (fed by `IFileOperationProgressSink` in `Interop/FileOperationSink.cs`) records the real result paths, and `UndoService` builds undo and redo from it. When you add an operation, log it so it can be undone; `FileOperationTests` covers the log.

### App startup and Windows integration

`App.OnStartup` first handles `--register-default` / `--unregister-default` (switch the default file manager, `Core/Integration/DefaultFileManager`, and exit; the MSI runs the second on uninstall), then sets the AppUserModelID shared with the Start menu shortcut and the jump list (`JumpLists.AppId`), then handles single instance: a second launch sends its arguments as JSON over a named pipe and exits. It then sets error mode (no "no disk in drive" dialogs), loads `AppSettings` (`%AppData%\ExplorerPlus\settings.json`, or the profile folder), and parses an `explorer.exe`-compatible command line (`ExplorerCommandLine`: `/select,`, `/e,`, `/n`, `/root,`, `shell:`, CLSIDs). Unhandled UI exceptions are appended to `errors.log` in the data folder and shown as a notice instead of crashing. The last window to close saves the session tabs and flushes the OLE clipboard so copied files stay pasteable.

### Hiding bloat

Two separate mechanisms hide things:

- **Native context menus** (`ShellContextMenu`, `IContextMenu`): `MainWindow.MenuFilter` passes `AppSettings.IsMenuItemHidden` as the `hide` callback. Holding Shift passes no filter and adds extended verbs, so the full menu shows.
- **Namespace items** (OneDrive, Gallery, Libraries): `ShellFilter.IsBlocked` hides them from listings and the sidebar.
