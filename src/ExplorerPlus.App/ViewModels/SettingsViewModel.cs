using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using ExplorerPlus.App.Infrastructure;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Integration;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Indexing;

namespace ExplorerPlus.App.ViewModels;

public sealed record Choice(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>Backs the Settings window. Every change is applied and saved at once; there is no OK/Cancel.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private static AppSettings S => AppSettings.Current;
    private string _hiddenMenuText;
    private DefaultFileManagerStatus _defaultStatus = new(DefaultFileManagerState.Off, null);
    private string? _integrationMessage;

    public SettingsViewModel()
    {
        _hiddenMenuText = string.Join(Environment.NewLine, S.HiddenMenuItems);
        ViewRules = ContentViewRules.Kinds.Select(k => new ViewRuleRow(k)).ToList();
        RefreshDefaultStatus();
        _ = RefreshIndexStatusAsync();
    }

    private void Change(Action apply, string? property = null)
    {
        apply();
        S.NotifyChanged();
        OnPropertyChanged(property);
    }

    // ---------- General ----------

    public IReadOnlyList<Choice> Themes { get; } = [new("Use system setting", "System"), new("Light", "Light"), new("Dark", "Dark")];

    public Choice Theme
    {
        get => Themes.FirstOrDefault(t => t.Value == S.Theme) ?? Themes[0];
        set
        {
            if (value is null) return;
            Change(() => S.Theme = value.Value);
            App.ApplyTheme();
        }
    }

    public IReadOnlyList<Choice> StartLocations { get; } =
    [
        new("Home", ShellLocation.Known.Home),
        new("This PC", ShellLocation.Known.ThisPC),
        new("Downloads", "shell:Downloads"),
        new("Documents", "shell:Personal"),
        new("Desktop", "shell:Desktop"),
    ];

    public Choice StartLocation
    {
        get => StartLocations.FirstOrDefault(c => string.Equals(c.Value, S.StartLocation, StringComparison.OrdinalIgnoreCase))
               ?? new Choice(S.StartLocation, S.StartLocation);
        set { if (value is not null) Change(() => S.StartLocation = value.Value); }
    }

    public bool OpenFoldersInNewTab { get => S.OpenFoldersInNewTab; set => Change(() => S.OpenFoldersInNewTab = value); }

    public bool QueueTransfers { get => S.QueueTransfers; set => Change(() => S.QueueTransfers = value); }
    public bool SmoothScrolling { get => S.SmoothScrolling; set => Change(() => S.SmoothScrolling = value); }
    public bool RestoreSession { get => S.RestoreSession; set => Change(() => S.RestoreSession = value); }

    public bool KeepRunningInBackground
    {
        get => S.KeepRunningInBackground;
        set
        {
            Change(() => S.KeepRunningInBackground = value);
            Task.Run(Background.SyncStartupEntry);
        }
    }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            Change(() => S.StartWithWindows = value);
            Task.Run(Background.SyncStartupEntry);
        }
    }

    // ---------- Files and folders ----------

    public bool ShowHiddenFiles { get => S.ShowHiddenFiles; set => Change(() => S.ShowHiddenFiles = value); }
    public bool ShowFileExtensions { get => S.ShowFileExtensions; set => Change(() => S.ShowFileExtensions = value); }
    public bool ShowFolderSizes { get => S.ShowFolderSizes; set => Change(() => S.ShowFolderSizes = value); }
    public bool ShowCheckboxes { get => S.ShowCheckboxes; set => Change(() => S.ShowCheckboxes = value); }
    public bool ContentAwareViews { get => S.ContentAwareViews; set => Change(() => S.ContentAwareViews = value); }

    public bool ShowProtectedOsFiles
    {
        get => S.ShowProtectedOsFiles;
        set
        {
            if (value && !S.ShowProtectedOsFiles && MessageBox.Show(
                    "You have chosen to display protected operating system files. These files are required to start and run Windows. Deleting or editing them can make your computer inoperable.\n\nAre you sure you want to display them?",
                    "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                // Tell the check box to snap back.
                Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged());
                return;
            }
            Change(() => S.ShowProtectedOsFiles = value);
        }
    }

    public IReadOnlyList<ViewRuleRow> ViewRules { get; }

    public void RestoreDefaultViewRules()
    {
        S.ViewRules = ContentViewRules.Defaults();
        S.NotifyChanged();
        foreach (var row in ViewRules) row.Reload();
    }

    public string FolderViewsText => FolderViewStore.Count switch
    {
        0 => "No folder has its own view, sort or columns.",
        1 => "1 folder has its own view, sort or columns.",
        var n => $"{n:N0} folders have their own view, sort or columns.",
    };

    public void ForgetFolderViews()
    {
        FolderViewStore.Clear();
        OnPropertyChanged(nameof(FolderViewsText));
    }

    // ---------- Navigation pane and Home ----------

    public bool ShowNetwork { get => S.ShowNetwork; set => Change(() => S.ShowNetwork = value); }
    public bool ShowLinux { get => S.ShowLinux; set => Change(() => S.ShowLinux = value); }
    public bool ShowGallery { get => S.ShowGallery; set => Change(() => S.ShowGallery = value); }
    public bool ShowLibraries { get => S.ShowLibraries; set => Change(() => S.ShowLibraries = value); }
    public bool ShowRecentFiles { get => S.ShowRecentFiles; set => Change(() => S.ShowRecentFiles = value); }

    // ---------- Context menu ----------

    /// <summary>One entry per line; an entry hides every menu item whose text or verb contains it.</summary>
    public string HiddenMenuText
    {
        get => _hiddenMenuText;
        set
        {
            if (!Set(ref _hiddenMenuText, value)) return;
            S.HiddenMenuItems = value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            S.Save();
        }
    }

    public void RestoreDefaultMenuFilter() => HiddenMenuText = string.Join(Environment.NewLine, new AppSettings().HiddenMenuItems);

    // ---------- Windows integration ----------

    public string DefaultStatusText => _defaultStatus.State switch
    {
        DefaultFileManagerState.On => "Explorer+ opens folders from other apps, folder links and Win+E.",
        DefaultFileManagerState.OnForOtherCopy => $"Folders open in another copy of Explorer+: {_defaultStatus.RegisteredExe}",
        _ => "File Explorer opens folders from other apps and Win+E.",
    };

    public bool IsDefault => _defaultStatus.State == DefaultFileManagerState.On;
    public bool CanMakeDefault => _defaultStatus.State != DefaultFileManagerState.On;
    public bool CanRestoreExplorer => _defaultStatus.State != DefaultFileManagerState.Off;

    public string MakeDefaultLabel => _defaultStatus.State == DefaultFileManagerState.OnForOtherCopy ? "Use this copy" : "Make Explorer+ the default";

    public string? IntegrationMessage
    {
        get => _integrationMessage;
        private set => Set(ref _integrationMessage, value);
    }

    // ---------- Instant search ----------

    private IndexServiceState _indexState = IndexServiceState.Unknown;
    private StatusResponse? _indexStatus;
    private bool _indexBusy;
    private string? _indexMessage;

    public string IndexStatusText
    {
        get
        {
            if (_indexBusy) return "Waiting for the installer…";
            if (!IndexServiceSetup.IsShipped) return "Not part of this copy of Explorer+ (a development build without the service).";
            return _indexState switch
            {
                IndexServiceState.NotInstalled => "Off. Enter in the search box uses Windows Search.",
                IndexServiceState.Stopped => "Installed, but not running.",
                IndexServiceState.Starting => "Starting…",
                IndexServiceState.Running when _indexStatus is null => "Running, but not answering yet.",
                IndexServiceState.Running => DescribeVolumes(_indexStatus!),
                _ => "Couldn't ask Windows about the service.",
            };
        }
    }

    private static string DescribeVolumes(StatusResponse status)
    {
        if (status.Volumes.Count == 0) return "On, but there are no NTFS drives to read.";
        var parts = status.Volumes.Select(v => v.State switch
        {
            VolumeState.Ready => $"{v.Root.TrimEnd('\\')} {v.Count:N0} items",
            VolumeState.Scanning => $"{v.Root.TrimEnd('\\')} reading…",
            _ => $"{v.Root.TrimEnd('\\')} failed ({v.Error})",
        });
        var text = "On: " + string.Join(", ", parts) + ".";
        if (IsOutdated(status)) text += $" The service is version {status.Version}; this Explorer+ is {IndexServiceSetup.AppVersion}.";
        return text;
    }

    private static bool IsOutdated(StatusResponse? status) =>
        status is not null && Version.TryParse(status.Version, out var service) && Version.TryParse(IndexServiceSetup.AppVersion, out var app) && service < app;

    public string IndexInstallLabel => _indexState switch
    {
        IndexServiceState.NotInstalled or IndexServiceState.Unknown => "Turn on instant search",
        IndexServiceState.Stopped => "Start the service",
        _ when IsOutdated(_indexStatus) => "Update the service",
        _ => "Reinstall the service",
    };

    public bool CanInstallIndex => !_indexBusy && IndexServiceSetup.IsShipped;
    public bool CanRemoveIndex => !_indexBusy && _indexState is not (IndexServiceState.NotInstalled or IndexServiceState.Unknown);

    public string? IndexMessage
    {
        get => _indexMessage;
        private set => Set(ref _indexMessage, value);
    }

    public async Task RefreshIndexStatusAsync()
    {
        _indexState = await Task.Run(IndexClient.QueryService);
        _indexStatus = _indexState == IndexServiceState.Running ? await IndexSearch.GetStatusAsync(fresh: true) : null;
        RaiseIndexProperties();
    }

    private void RaiseIndexProperties()
    {
        OnPropertyChanged(nameof(IndexStatusText));
        OnPropertyChanged(nameof(IndexInstallLabel));
        OnPropertyChanged(nameof(CanInstallIndex));
        OnPropertyChanged(nameof(CanRemoveIndex));
    }

    public Task InstallIndexAsync() => RunIndexSetupAsync(install: true);
    public Task RemoveIndexAsync() => RunIndexSetupAsync(install: false);

    private async Task RunIndexSetupAsync(bool install)
    {
        _indexBusy = true;
        IndexMessage = null;
        RaiseIndexProperties();
        var error = await IndexServiceSetup.RunAsync(install);
        _indexBusy = false;
        IndexMessage = error is null ? (install ? "Instant search is on." : "Instant search is off, and the service is removed.") : "Didn't work: " + error;
        await RefreshIndexStatusAsync();
        if (!install || error is not null) return;
        // The drives are read in the background; show the counts as they come in.
        for (var i = 0; i < 60 && (_indexStatus is null || _indexStatus.Volumes.Any(v => v.State == VolumeState.Scanning)); i++)
        {
            await Task.Delay(1000);
            await RefreshIndexStatusAsync();
        }
    }

    private void RefreshDefaultStatus()
    {
        try { _defaultStatus = Environment.ProcessPath is { } exe ? DefaultFileManager.ForCurrentUser().GetStatus(exe) : _defaultStatus; }
        catch (Exception ex) { IntegrationMessage = ex.Message; }
        OnPropertyChanged(nameof(DefaultStatusText));
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(CanMakeDefault));
        OnPropertyChanged(nameof(CanRestoreExplorer));
        OnPropertyChanged(nameof(MakeDefaultLabel));
    }

    public void MakeDefault()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            DefaultFileManager.ForCurrentUser().Register(exe);
            IntegrationMessage = AppPaths.Profile is not null
                ? "Done. Note: this is a dev profile; Windows will start this exe without the profile, with your everyday settings."
                : "Done. Turn it off here at any time to give folders back to File Explorer.";
        }
        catch (Exception ex) { IntegrationMessage = "Couldn't change the setting: " + ex.Message; }
        RefreshDefaultStatus();
    }

    public void RestoreExplorer()
    {
        try
        {
            DefaultFileManager.ForCurrentUser().Unregister();
            IntegrationMessage = "File Explorer opens folders again.";
        }
        catch (Exception ex) { IntegrationMessage = "Couldn't change the setting: " + ex.Message; }
        RefreshDefaultStatus();
    }

    public void ClearRecentFolders()
    {
        JumpLists.ClearRecent();
        IntegrationMessage = "Recent folders cleared from the jump list.";
    }

    // ---------- About ----------

    public string VersionText
    {
        get
        {
            var asm = Assembly.GetEntryAssembly();
            var version = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm?.GetName().Version?.ToString() ?? "";
            var plus = version.IndexOf('+'); // drop the commit hash the SDK appends
            return "Explorer+ " + (plus > 0 ? version[..plus] : version) + (AppPaths.Profile is { } p ? $" (profile “{p}”)" : "");
        }
    }

    public string DataFolder => AppPaths.DataFolder;
    public string ExePath => Environment.ProcessPath ?? "";

    public void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        App.OpenWindow(AppPaths.DataFolder);
    }

    public void OpenErrorLog()
    {
        var log = Path.Combine(AppPaths.DataFolder, "errors.log");
        if (!File.Exists(log)) { MessageBox.Show("No errors have been logged.", "Explorer+"); return; }
        try { Process.Start(new ProcessStartInfo(log) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Explorer+"); }
    }
}

public sealed class ViewRuleRow(FolderContent content) : ObservableObject
{
    private static AppSettings S => AppSettings.Current;

    private static readonly IReadOnlyList<Choice> AllViews = ViewModes.All.Select(m => new Choice(ViewModes.Label(m), m.ToString())).ToList();

    public IReadOnlyList<Choice> Views => AllViews;

    private ViewRule Rule => S.ViewRules.First(r => r.Content == content);

    public string Label => ContentViewRules.Label(content);

    public bool HasExtensions => content != FolderContent.General;

    public Choice View
    {
        get => AllViews.First(v => v.Value == Rule.View.ToString());
        set
        {
            if (value is null || !Enum.TryParse<ViewMode>(value.Value, out var mode) || mode == Rule.View) return;
            Rule.View = mode;
            S.NotifyChanged();
            OnPropertyChanged();
        }
    }

    public string ExtensionsText
    {
        get => string.Join(" ", Rule.Extensions);
        set
        {
            var list = value.Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ContentViewRules.NormalizeExtension).Where(e => e.Length > 0).Distinct().ToList();
            if (list.SequenceEqual(Rule.Extensions)) return;
            Rule.Extensions = list;
            S.NotifyChanged();
        }
    }

    public void Reload()
    {
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(ExtensionsText));
    }
}
