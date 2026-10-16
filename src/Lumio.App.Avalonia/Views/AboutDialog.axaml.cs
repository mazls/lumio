using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lumio.App.Avalonia.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Enter)
        {
            Close();
            e.Handled = true;
        }
    }
}
