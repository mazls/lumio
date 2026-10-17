using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lumio.App.Avalonia.Views;

public partial class AboutDialog : UserControl
{
    public event Action? Closed;

    public AboutDialog()
    {
        InitializeComponent();
        Loaded += (s, e) => Focus();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Closed?.Invoke();
    }

    private void OnControlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Enter)
        {
            Closed?.Invoke();
            e.Handled = true;
        }
    }
}
