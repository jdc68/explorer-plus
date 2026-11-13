# Explorer+

A fast, keyboard-friendly replacement for File Explorer on Windows 11. It is built on the real Windows shell, so This PC, the Recycle Bin, network locations, phones, zip folders and shell extensions all work as they do in Explorer. Folders open without the "Working on it…" wait, and there is no OneDrive or Copilot clutter.

Requires Windows 10 2004 or later (made for Windows 11) and the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

## Install

There is no published release yet. Build the installer yourself:

```powershell
powershell -File installer/build.ps1
```

That writes a per-user MSI to `artifacts/installer`. It installs to `%LocalAppData%\Programs\Explorer+` without admin rights and adds a Start menu shortcut. Uninstall it from Settings ▸ Apps.

To run from source instead: `dotnet run --project src/ExplorerPlus.App`.

## Features

- **Tabs and windows**: tabs in the title bar; drag a tab to reorder it, onto another window, or off the window to make a new one. Workspaces save a window's tabs under a name (More ▸ Workspaces).
- **Dual pane**: `Ctrl+\` splits the current tab. Each side has its own folder and view.
- **Views**: eight layouts, from details to extra large icons, with grouping, any shell property as a column, and per-folder view memory. Folders full of pictures or videos open as thumbnails, and documents as details.
- **Home**: Quick access, Starred items (`Ctrl+D`) and recent files.
- **Folder sizes** in the Size column, filled in the background.
- **Instant search** over every NTFS drive, like *Everything* (see below). Windows Search with `kind:`, `date:` and `size:` also works.
- **Quick Look**: `Space` previews the selected file. There is also a preview pane (`Alt+P`) and a details pane (`Alt+Shift+P`).
- **Batch rename** with find and replace, regex, numbering and a live preview. One `Ctrl+Z` undoes the whole batch.
- **Undo and redo** for rename, new, copy, move and delete.
- **Transfer queue**: copies and moves run one at a time, with Windows' own progress and conflict dialogs.
- **Archives**: compress to ZIP, 7z or TAR.GZ, and extract here.
- **Tags**, a **Git status** column, a **duplicate finder** and **folder compare and sync**.
- **Clean context menus**: Copilot, Clipchamp, "Give access to" and similar entries are hidden. Hold `Shift` while right-clicking for the full menu. The list is editable in Settings.

## Shortcuts

| Keys | Action |
|---|---|
| `Ctrl+K` | Command palette: every command, open tabs and recent places |
| `Ctrl+T` / `Ctrl+W` / `Ctrl+Shift+T` | New tab / close tab / reopen closed tab |
| `Ctrl+N` | New window |
| `Ctrl+\` | Split the tab into two panes |
| `Tab` | Switch between the panes |
| `Alt+F5` / `Alt+F6` | Copy / move the selection to the other pane |
| `Space` | Quick Look |
| `F2` | Rename (batch rename when several items are selected) |
| `Ctrl+D` | Star or unstar |
| `Ctrl+Shift+F` | Search all drives |
| `Ctrl+L` | Edit the address |
| `Ctrl+Shift+1` … `8` | Switch the view |
| `Ctrl+Shift+C` | Copy as path |
| ``Ctrl+Shift+` `` | Open a terminal here |
| `Ctrl+,` | Settings |

Explorer's own shortcuts (`Alt+Left`, `Alt+Up`, `F5`, `Ctrl+Shift+N`, `Alt+Enter`, and so on) work too.

## Tips

- Start typing in a folder to filter it instantly. Press `Enter` in the search box to search the folder and everything below it.
- The address bar accepts `shell:` paths, `%ENV%` variables, and `cmd`, `powershell` or `wt` to open a terminal in the current folder.
- Instant search syntax: words must all match (`report 2026`), `*.pdf` matches the whole name, a word with `\` matches the path, `"quoted text"` keeps spaces and `!word` excludes.
- Pick a view by hand and Explorer+ remembers it for that folder. Settings ▸ Files and folders can forget them all.

## Windows integration

- **Default file manager** (Settings ▸ Windows integration, off by default): folder links from other apps and `Win+E` open in Explorer+. It is set per user, needs no admin rights, and can be undone with one click or by uninstalling.
- **Background mode** (on by default): closing the last window keeps Explorer+ running without a window, so the next folder opens in about 0.2 s. It can also start at sign-in. More ▸ Exit really quits.
- **Instant search service** (Settings ▸ Instant search, off by default): a small Windows service reads each NTFS drive's file table and follows its change journal. Turning it on asks for admin rights once. Searches only return what the signed-in user is allowed to see. "Turn off and remove" deletes it.
- **Command line**: `ExplorerPlus.exe` accepts the same arguments as `explorer.exe` (`/select,file`, `/e,`, `/n`, `/root,`, `shell:` and CLSID paths). A second launch opens in the running instance.
- **Taskbar**: a jump list with pinned and recent folders, and progress on the taskbar button during file operations.

### Known limits

- The Open and Save dialogs inside other apps, the desktop and the taskbar stay File Explorer's, because they run inside those apps.
- Some apps start `explorer.exe` directly for "Show in folder", which skips the default file manager setting.
- Instant search covers NTFS drives only. Other drives use Windows Search.

## Settings and data

Settings are in `%AppData%\ExplorerPlus\settings.json`, and folder views are in `views.json` next to it. Settings ▸ About shows the data folder.

## Building

```bash
dotnet build ExplorerPlus.sln
```

```bash
dotnet test tests/ExplorerPlus.Core.Tests
```

[PLAN.md](PLAN.md) holds the design decisions, milestones and what is planned next.
