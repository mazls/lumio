using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lumio.App.Avalonia.Views;

public partial class AboutDialog : UserControl
{
    private const string GitHubRepoUrl = "https://github.com/mazls/lumio";
    private const string WebsiteUrl = "https://mazls.github.io/lumio/";

    public event Action? Closed;

    public AboutDialog()
    {
        InitializeComponent();
        var edition = OperatingSystem.IsAndroid() ? "Android Edition" : "Raspberry Pi / Linux Edition";
        VersionText.Text = $"Version {Lumio.Core.Updates.UpdateChecker.CurrentVersion.ToString(3)} • {edition}";
        if (OperatingSystem.IsAndroid())
        {
            DescriptionText.Text = "Fotobox-App für Tablets und Handys – mit Tablet-Kamera, USB-Webcams oder Nikon-Kameras per USB und Sofortdruck auf dem Canon Selphy per USB oder WLAN.";
        }
        Loaded += (s, e) => Focus();
    }

    private void OnOpenWebsiteClick(object? sender, RoutedEventArgs e) => OpenInBrowser(WebsiteUrl);

    private void OnOpenGitHubClick(object? sender, RoutedEventArgs e) => OpenInBrowser(GitHubRepoUrl);

    private async void OpenInBrowser(string url)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri(url));
        }
        catch
        {
            // No browser available (kiosk device): nothing else to do
        }
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
