using System.Windows;
using System.Windows.Controls;

namespace ExplorerPlus.App.Views;

public partial class PromptWindow : Window
{
    private PromptWindow(string title, string message, string text, string ok)
    {
        InitializeComponent();
        Title = title + " - Explorer+";
        Heading.Text = title;
        Message.Text = message;
        Input.Text = text;
        OkButton.Content = ok;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    // the trimmed text, or null when cancelled or left empty
    public static string? Ask(Window owner, string title, string message, string text = "", string ok = "OK")
    {
        var window = new PromptWindow(title, message, text, ok) { Owner = owner };
        return window.ShowDialog() == true ? window.Input.Text.Trim() : null;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => OkButton.IsEnabled = Input.Text.Trim().Length > 0;

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
