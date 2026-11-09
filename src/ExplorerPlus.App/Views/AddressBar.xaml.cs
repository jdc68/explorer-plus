using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExplorerPlus.App.Services;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.App.Views;

public partial class AddressBar : UserControl
{
    private CancellationTokenSource? _suggestCts;
    private bool _settingText;

    public AddressBar() => InitializeComponent();

    /// <summary>Raised when editing ends so the window can return focus to the file list.</summary>
    public event Action? EditFinished;

    private MainViewModel? Vm => DataContext as MainViewModel;

    public bool IsEditing => Editor.Visibility == Visibility.Visible;

    public void BeginEdit(string? text = null)
    {
        _settingText = true;
        Editor.Text = text ?? Vm?.SelectedTab?.Location?.EditText ?? "";
        _settingText = false;
        Editor.Visibility = Visibility.Visible;
        CrumbView.Visibility = Visibility.Hidden;
        Editor.Focus();
        if (text is null) Editor.SelectAll(); else Editor.CaretIndex = Editor.Text.Length;
    }

    private void EndEdit(bool returnFocus)
    {
        _suggestCts?.Cancel();
        SuggestionPopup.IsOpen = false;
        Editor.Visibility = Visibility.Collapsed;
        CrumbView.Visibility = Visibility.Visible;
        if (returnFocus) EditFinished?.Invoke();
    }

    private void OnCrumbAreaClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null) return;
        BeginEdit();
    }

    private async void OnSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BreadcrumbSegment seg } && Vm is { } vm)
            await vm.NavigateAsync(seg.Location);
    }

    private async void OnSegmentMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: BreadcrumbSegment seg } && Vm is { } vm)
        {
            e.Handled = true;
            await vm.NavigateAsync(seg.Location, OpenTarget.NewTab);
        }
    }

    private async void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BreadcrumbSegment seg } button || Vm is not { } vm) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 500 };
        menu.Items.Add(new MenuItem { Header = "Loading…", IsEnabled = false });
        button.ContextMenu = menu;
        menu.IsOpen = true;

        var children = await LoadChildFoldersAsync(seg.Location);
        menu.Items.Clear();
        var current = vm.SelectedTab?.Breadcrumb;
        foreach (var child in children)
        {
            var item = new MenuItem
            {
                Header = child.DisplayName,
                Icon = new Image { Source = IconCache.Get(child.IconIndex), Width = 16, Height = 16 },
                FontWeight = current?.Any(c => string.Equals(c.Location.FileSystemPath ?? c.Location.ParsingName,
                    child.Path ?? "", StringComparison.OrdinalIgnoreCase)) == true ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var target = child;
            item.Click += async (_, _) =>
            {
                var loc = target.Path is not null ? await ShellLocation.ResolveAsync(target.Path) : await ShellLocation.FromPidlAsync(target.GetPidl());
                if (loc is not null) await vm.NavigateAsync(loc);
            };
            menu.Items.Add(item);
        }
        if (children.Count == 0) menu.Items.Add(new MenuItem { Header = "(no subfolders)", IsEnabled = false });
    }

    private static async Task<IReadOnlyList<FolderEntry>> LoadChildFoldersAsync(ShellLocation location)
    {
        var settings = AppSettings.Current;
        bool Keep(FolderEntry e) => (e.IsFolder || e.ShellAttributes.HasFlag(ExplorerPlus.Interop.SFGAO.FOLDER) && !e.IsBrowsableFile)
                                    && (settings.ShowHiddenFiles || !e.IsHidden) && !e.IsProtectedSystem;
        try
        {
            if (location.IsFileSystemFolder)
                return await Task.Run(() => FastFolderEnumerator.Enumerate(location.FileSystemPath!).Where(Keep)
                    .OrderBy(e => e.Name, NaturalComparer.Instance).ToList());
            return await StaWorkerPool.Enumeration.Run(() => ShellFolderEnumerator.Enumerate(location.Pidl).Where(Keep).ToList());
        }
        catch
        {
            return [];
        }
    }

    // ---------- Editing ----------

    private async void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            {
                e.Handled = true;
                var text = SuggestionPopup.IsOpen && SuggestionList.SelectedItem is string s ? s : Editor.Text;
                EndEdit(returnFocus: true);
                if (Vm is { } vm) await vm.NavigateFromAddressBarAsync(text);
                break;
            }
            case Key.Escape:
                e.Handled = true;
                if (SuggestionPopup.IsOpen) SuggestionPopup.IsOpen = false;
                else EndEdit(returnFocus: true);
                break;
            case Key.Down when SuggestionPopup.IsOpen:
                e.Handled = true;
                SuggestionList.SelectedIndex = Math.Min(SuggestionList.SelectedIndex + 1, SuggestionList.Items.Count - 1);
                SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
                break;
            case Key.Up when SuggestionPopup.IsOpen:
                e.Handled = true;
                SuggestionList.SelectedIndex = Math.Max(SuggestionList.SelectedIndex - 1, 0);
                SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
                break;
            case Key.Tab when SuggestionPopup.IsOpen && SuggestionList.Items.Count > 0:
                e.Handled = true;
                var pick = SuggestionList.SelectedItem as string ?? (string)SuggestionList.Items[0];
                _settingText = true;
                Editor.Text = pick.TrimEnd('\\') + "\\";
                _settingText = false;
                Editor.CaretIndex = Editor.Text.Length;
                _ = UpdateSuggestionsAsync(Editor.Text);
                break;
        }
    }

    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsEditing && !SuggestionList.IsKeyboardFocusWithin) EndEdit(returnFocus: false);
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingText || !IsEditing) return;
        _ = UpdateSuggestionsAsync(Editor.Text);
    }

    private async void OnSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionList.SelectedItem is not string s) return;
        EndEdit(returnFocus: true);
        if (Vm is { } vm) await vm.NavigateFromAddressBarAsync(s);
    }

    private async Task UpdateSuggestionsAsync(string text)
    {
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        try { await Task.Delay(120, cts.Token); } catch (OperationCanceledException) { return; }

        var expanded = Environment.ExpandEnvironmentVariables(text);
        var slash = expanded.LastIndexOf('\\');
        if (slash < 0) { SuggestionPopup.IsOpen = false; return; }
        var dir = expanded[..(slash + 1)];
        var prefix = expanded[(slash + 1)..];
        var showHidden = AppSettings.Current.ShowHiddenFiles;

        List<string> matches;
        try
        {
            matches = await Task.Run(() =>
            {
                if (!Directory.Exists(dir)) return [];
                return FastFolderEnumerator.Enumerate(dir, cts.Token)
                    .Where(f => f.IsFolder && (showHidden || !f.IsHidden) && f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Path!)
                    .OrderBy(p => p, NaturalComparer.Instance)
                    .Take(40)
                    .ToList();
            }, cts.Token);
        }
        catch { return; }
        if (cts.IsCancellationRequested || !IsEditing) return;

        SuggestionList.ItemsSource = matches;
        SuggestionList.SelectedIndex = -1;
        SuggestionPopup.IsOpen = matches.Count > 0;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
