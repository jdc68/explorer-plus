using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using ExplorerPlus.App.Infrastructure;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Integration;
using ExplorerPlus.Core.Shell;

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
        RefreshDefaultStatus();
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
    public bool RestoreSession { get => S.RestoreSession; set => Change(() => S.RestoreSession = value); }

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

    public string FolderViewsText => FolderViewStore.Count switch
    {
        0 => "No folder has a view set by hand.",
        1 => "1 folder has a view set by hand.",
        var n => $"{n:N0} folders have a view set by hand.",
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
