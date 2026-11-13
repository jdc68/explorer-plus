using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Interop;

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
        SourceInitialized += (_, _) => HideMinMaxButtons();
    }

    private void HideMinMaxButtons()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)User32.GetWindowLongPtr(hwnd, User32.GWL_STYLE);
        User32.SetWindowLongPtr(hwnd, User32.GWL_STYLE, (IntPtr)(style & ~(User32.WS_MINIMIZEBOX | User32.WS_MAXIMIZEBOX)));
    }

    /// <summary>Shows the one Settings window, over <paramref name="owner"/>.</summary>
    public static void ShowFor(Window owner)
    {
        if (_open is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        _open = new SettingsWindow { Owner = owner };
        _open.Show();
    }

    private void OnForgetViewsClick(object sender, RoutedEventArgs e) => Vm.ForgetFolderViews();
    private void OnRestoreMenuDefaultsClick(object sender, RoutedEventArgs e) => Vm.RestoreDefaultMenuFilter();
    private void OnMakeDefaultClick(object sender, RoutedEventArgs e) => Vm.MakeDefault();
    private async void OnInstallIndexClick(object sender, RoutedEventArgs e) => await Vm.InstallIndexAsync();
    private async void OnRemoveIndexClick(object sender, RoutedEventArgs e) => await Vm.RemoveIndexAsync();
    private void OnRestoreExplorerClick(object sender, RoutedEventArgs e) => Vm.RestoreExplorer();
    private void OnClearRecentClick(object sender, RoutedEventArgs e) => Vm.ClearRecentFolders();
    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e) => Vm.OpenDataFolder();
    private void OnOpenErrorLogClick(object sender, RoutedEventArgs e) => Vm.OpenErrorLog();
}
