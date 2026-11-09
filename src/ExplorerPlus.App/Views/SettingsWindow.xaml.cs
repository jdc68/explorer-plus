using System.Windows;
using System.Windows.Input;
using ExplorerPlus.App.ViewModels;

namespace ExplorerPlus.App.Views;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private SettingsViewModel Vm { get; } = new();

    private SettingsWindow()
    {
        InitializeComponent();
        DataContext = Vm;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => _open = null;
    }

    /// <summary>Shows the one Settings window, over <paramref name="owner"/>.</summary>
    public static void ShowFor(Window owner)
    {
        if (_open is { } open)
        {
            open.Activate();
            return;
        }
        _open = new SettingsWindow { Owner = owner };
        _open.Show();
    }

    private void OnForgetViewsClick(object sender, RoutedEventArgs e) => Vm.ForgetFolderViews();
    private void OnRestoreMenuDefaultsClick(object sender, RoutedEventArgs e) => Vm.RestoreDefaultMenuFilter();
    private void OnMakeDefaultClick(object sender, RoutedEventArgs e) => Vm.MakeDefault();
    private void OnRestoreExplorerClick(object sender, RoutedEventArgs e) => Vm.RestoreExplorer();
    private void OnClearRecentClick(object sender, RoutedEventArgs e) => Vm.ClearRecentFolders();
    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e) => Vm.OpenDataFolder();
    private void OnOpenErrorLogClick(object sender, RoutedEventArgs e) => Vm.OpenErrorLog();
}
