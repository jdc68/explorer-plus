using System.IO;
using System.Windows;
using ExplorerPlus.App.Services;
using ExplorerPlus.App.Views;
using ExplorerPlus.Core.Integration;
using ExplorerPlus.Core.Navigation;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App;

public partial class App : Application
{
    private SingleInstance? _instance;

    public static IEnumerable<MainWindow> MainWindows => Current.Windows.OfType<MainWindow>();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Run by the installer (and usable from a script): switch the default file manager without opening a window.
        if (e.Args is [var only] && only.ToLowerInvariant() is "--register-default" or "--unregister-default")
        {
            var code = 0;
            try
            {
                var manager = DefaultFileManager.ForCurrentUser();
                if (only.Equals("--register-default", StringComparison.OrdinalIgnoreCase)) manager.Register(Environment.ProcessPath!);
                else manager.Unregister();
            }
            catch { code = 1; }
            Shutdown(code);
            return;
        }

        // Before any window: groups the taskbar button and the jump list with the Start menu shortcut.
        Shell32.SetCurrentProcessExplicitAppUserModelID(JumpLists.AppId);

        _instance = new SingleInstance();
        if (!_instance.IsPrimary && _instance.TrySendToPrimary(e.Args))
        {
            Shutdown();
            return;
        }

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
        HandleCommandLine(e.Args, fromOtherInstance: false);
        JumpLists.Schedule();
    }

    public static void ApplyTheme() =>
        Current.ThemeMode = AppSettings.Current.Theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

    private static void HandleCommandLine(string[] args, bool fromOtherInstance)
    {
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

        if (targets.Count == 0 && !fromOtherInstance && settings.RestoreSession && settings.LastSession.Count > 0)
            targets.AddRange(settings.LastSession);
        if (targets.Count == 0) targets.Add(settings.StartLocation);

        var window = new MainWindow(targets, select);
        window.Show();
        window.BringToFront();
    }

    public static void OpenWindow(string parsingName)
    {
        var w = new MainWindow([parsingName], null);
        w.Show();
        w.BringToFront();
    }

    /// <summary>Called by a window when it closes; the last one saves the session and exits.</summary>
    public static void OnWindowClosed(MainWindow window)
    {
        if (MainWindows.Any(w => w != window && w.IsLoaded)) return;
        var settings = AppSettings.Current;
        settings.LastSession = window.ViewModel.SessionTabs.ToList();
        settings.Save();
        ShellClipboard.OleFlushClipboard(); // keep copied files pasteable after we exit
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
