using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ExplorerPlus.App.Services;
using ExplorerPlus.App.ViewModels;

namespace ExplorerPlus.App.Views;

public partial class QuickLookWindow : Window
{
    private readonly TabViewModel _tab;
    private readonly PaneViewModel _preview = new(quickLook: true);

    public QuickLookWindow(Window owner, TabViewModel tab)
    {
        InitializeComponent();
        Owner = owner;
        _tab = tab;
        DataContext = _preview;
        PlaceOver(owner);

        _preview.PropertyChanged += OnPreviewChanged;
        Host.Failed += _preview.HandlerFailed;
        _tab.PropertyChanged += OnTabChanged;
        _preview.Attach(tab);
        UpdateInfo();

        PreviewKeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            _tab.PropertyChanged -= OnTabChanged;
            _preview.PropertyChanged -= OnPreviewChanged;
            _preview.Attach(null);
            Host.Clear();
        };
    }

    public TabViewModel Tab => _tab;

    /// <summary>Most of the owner window, centred on it; Quick Look follows the owner's monitor.</summary>
    private void PlaceOver(Window owner)
    {
        var bounds = owner.WindowState == WindowState.Maximized ? owner.RestoreBounds : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
        if (owner.WindowState == WindowState.Maximized)
            bounds = new Rect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        Width = Math.Max(MinWidth, bounds.Width * 0.72);
        Height = Math.Max(MinHeight, bounds.Height * 0.78);
        Left = bounds.Left + (bounds.Width - Width) / 2;
        Top = bounds.Top + (bounds.Height - Height) / 2;
    }

    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneViewModel.HandlerTarget)) return;
        if (_preview.HandlerTarget is { } target) Host.Show(target);
        else Host.Clear();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.SelectedItems)) UpdateInfo();
        else if (e.PropertyName is nameof(TabViewModel.Location)) Close();
    }

    private void UpdateInfo()
    {
        var selection = _tab.SelectedItems;
        if (selection.Count != 1)
        {
            InfoIcon.Source = null;
            InfoName.Text = selection.Count == 0 ? "Nothing selected" : $"{selection.Count} items selected";
            InfoDetails.Text = "";
            Title = "Quick Look - Explorer+";
            return;
        }
        var item = selection[0];
        InfoIcon.Source = IconCache.Get(item.IconIndex, 32);
        InfoName.Text = item.Name;
        var parts = new List<string> { item.TypeName };
        if (item.SizeText is { Length: > 0 } size && size != "…") parts.Add(size);
        if (item.ModifiedText is { Length: > 0 } modified) parts.Add(modified);
        InfoDetails.Text = string.Join("  ·  ", parts.Where(p => p.Length > 0));
        Title = $"{item.Name} - Quick Look";
    }

    /// <summary>When Quick Look has focus (it was clicked), Space and Esc close it and other keys go back to the list.</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBox && e.Key is not (Key.Escape or Key.Space)) return;
        if (e.Key is Key.Escape or Key.Space)
        {
            e.Handled = true;
            Close();
            Owner?.Activate();
        }
        else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            Owner?.Activate();
        }
    }
}
