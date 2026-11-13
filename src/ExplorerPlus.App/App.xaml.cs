using System.IO;
using System.Windows;
using ExplorerPlus.App.Services;
using ExplorerPlus.App.Views;
using ExplorerPlus.Core.Integration;
using ExplorerPlus.Core.Navigation;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Indexing;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App;

public partial class App : Application
{
    private readonly SingleInstance? _instance;
    private static bool _exiting;

    /// <param name="instance">The single-instance lock, already taken by <see cref="Program.Main"/>; null for the registration switches.</param>
    public App(SingleInstance? instance) => _instance = instance;

    public static IEnumerable<MainWindow> MainWindows => Current.Windows.OfType<MainWindow>();

    public static bool IsRegistrationSwitch(string[] args) =>
        args is [var only] && only.ToLowerInvariant() is "--register-default" or "--unregister-default" or "--uninstall";

    protected override void OnStartup(StartupEventArgs e)
    {
        // Run by the installer (and usable from a script): switch the default file manager without opening a window.
        // --uninstall also removes the sign-in entry.
        if (_instance is null) // Program.Main takes no lock only for these switches
        {
            var only = e.Args[0].ToLowerInvariant();
            var code = 0;
            try
            {
                var manager = DefaultFileManager.ForCurrentUser();
                if (only == "--register-default") manager.Register(Environment.ProcessPath!);
                else manager.Unregister();
                if (only == "--uninstall")
                {
                    Background.RemoveStartupEntry();
                    IndexServiceSetup.RemoveOnUninstall();
                }
            }
            catch { code = 1; }
            Shutdown(code);
            return;
        }

        // Before any window: groups the taskbar button and the jump list with the Start menu shortcut.
        Shell32.SetCurrentProcessExplicitAppUserModelID(JumpLists.AppId);

        base.OnStartup(e);

        // Never show "There is no disk in the drive" style system dialogs while enumerating.
        Kernel32.SetErrorMode(Kernel32.SEM_FAILCRITICALERRORS | Kernel32.SEM_NOOPENFILEERRORBOX);
        try
        {
            UxTheme.SetPreferredAppMode(UxTheme.AllowDark);
            UxTheme.FlushMenuThemes();
        }
        catch { /* dark native menus are cosmetic */ }

        Shell32.FileIconInit(true);
        AppSettings.Load();
        ApplyTheme();
        FolderLoader.HomeOptions = QuickAccess.HomeOptions;
        TagView.Paths = tag => Current.Dispatcher.Invoke(() => Tags.PathsWith(tag));
        IndexSearch.Options = () => new IndexSearchOptions(AppSettings.Current.ShowHiddenFiles, AppSettings.Current.ShowProtectedOsFiles);
        _ = ShellNewTemplates.GetAsync(); // read the New ▸ list in the background so the menu opens instantly
        FolderSizeWatcher.Start();
        ThumbnailService.Start();
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                File.AppendAllText(Path.Combine(AppPaths.DataFolder, "errors.log"), $"{DateTime.Now:O} {args.Exception}{Environment.NewLine}");
            }
            catch (IOException) { }
            foreach (var w in MainWindows) w.ViewModel?.ShowNotice("Something went wrong: " + args.Exception.Message);
        };

        _instance.Listen(args => Dispatcher.BeginInvoke(() => HandleCommandLine(args, fromOtherInstance: true)));
        Background.Listen(ExitAll);
        CutItems.Start();
        HandleCommandLine(e.Args, fromOtherInstance: false);
        JumpLists.Schedule();
        Task.Run(Background.SyncStartupEntry); // keeps the sign-in entry pointing at this copy
    }

    /// <summary>Closes every window and ends the process, also when Explorer+ would otherwise stay in the background.</summary>
    public static void ExitAll()
    {
        _exiting = true;
        var windows = MainWindows.ToList();
        if (windows.Count == 0) Current.Shutdown();
        foreach (var w in windows) w.Close(); // the last one to close saves the session and shuts down
    }

    /// <summary>No window left, staying in the background: stop timers and give memory back.</summary>
    private static void GoIdle()
    {
        FolderSizeWatcher.SetActive(false);
        Current.Dispatcher.BeginInvoke(Background.Trim, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    public static void ApplyTheme()
    {
        Current.ThemeMode = AppSettings.Current.Theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        UseTextFontInPopups();
    }

    // Menus and tooltips of icon buttons would inherit the icon font and show boxes. Built here, not in App.xaml:
    // there BasedOn finds the classic style, because the Fluent theme isn't loaded yet
    private static void UseTextFontInPopups()
    {
        foreach (var type in new[] { typeof(System.Windows.Controls.ContextMenu), typeof(System.Windows.Controls.ToolTip) })
        {
            Current.Resources.Remove(type);
            var style = new Style(type, Current.TryFindResource(type) as Style);
            style.Setters.Add(new Setter(System.Windows.Controls.Control.FontFamilyProperty, SystemFonts.MessageFontFamily));
            Current.Resources[type] = style;
        }
    }

    private static void HandleCommandLine(string[] args, bool fromOtherInstance)
    {
        if (Program.IsOnly(args, Program.QuitArg))
        {
            ExitAll();
            return;
        }
        if (Program.IsOnly(args, Program.BackgroundArg))
        {
            if (MainWindows.Any()) return;
            if (!fromOtherInstance && !AppSettings.Current.KeepRunningInBackground)
            {
                Current.Shutdown(); // started by the installer or a stale sign-in entry, but turned off in Settings
                return;
            }
            if (!fromOtherInstance)
                Current.Dispatcher.BeginInvoke(Views.MainWindow.Prewarm, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            GoIdle();
            return;
        }

        var cmd = ExplorerCommandLine.Parse(args);
        var settings = AppSettings.Current;
        var targets = cmd.Paths.ToList();
        string? select = null;
        if (cmd.SelectPath is { } sel)
        {
            var parent = System.IO.Path.GetDirectoryName(sel.TrimEnd('\\'));
            if (parent is not null) { targets.Insert(0, parent); select = sel; }
        }

        var existing = MainWindows.LastOrDefault();
        if (fromOtherInstance && existing is not null && !cmd.NewWindow && settings.OpenFoldersInNewTab)
        {
            existing.BringToFront();
            if (targets.Count == 0) targets.Add(settings.StartLocation);
            foreach (var t in targets) _ = existing.ViewModel.OpenTabAsync(t, activate: true, selectPath: t == targets[0] ? select : null);
            return;
        }

        // With "Reopen the tabs from last time" on (off by default), a plain start reopens them, also when it wakes up
        // Explorer+ running in the background. Otherwise a closed window's tabs stay closed.
        var freshStart = !fromOtherInstance || existing is null && !cmd.NewWindow;
        if (targets.Count == 0 && freshStart && settings.RestoreSession && settings.LastSession.Count > 0)
            targets.AddRange(settings.LastSession);
        if (targets.Count == 0) targets.Add(settings.StartLocation);

        var window = new MainWindow(targets, select);
        window.Show();
        window.BringToFront();
    }

    public static void OpenWorkspace(Workspace workspace)
    {
        var w = new MainWindow(workspace);
        w.Show();
        w.BringToFront();
    }

    public static void OpenWindow(string parsingName)
    {
        var w = new MainWindow([parsingName], null);
        w.Show();
        w.BringToFront();
    }

    /// <summary>Called by a window when it closes; the last one saves the session and exits or goes idle.</summary>
    public static void OnWindowClosed(MainWindow window)
    {
        if (MainWindows.Any(w => w != window && w.IsLoaded)) return;
        var settings = AppSettings.Current;
        settings.LastSession = window.ViewModel.SessionTabs.ToList();
        settings.Save();
        ShellClipboard.OleFlushClipboard(); // keep copied files pasteable after we exit
        if (_exiting || !settings.KeepRunningInBackground) Current.Shutdown();
        else GoIdle();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
