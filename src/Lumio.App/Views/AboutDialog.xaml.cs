using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Lumio.App.Views;

public partial class AboutDialog : Window
{
    private const string GitHubRepoUrl = "https://github.com/mazls/lumio";
    private const string WebsiteUrl = "https://mazls.github.io/lumio/";

    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {Lumio.Core.Updates.UpdateChecker.CurrentVersion.ToString(3)} • Windows Edition";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Enter)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnOpenGitHubClick(object sender, RoutedEventArgs e) => OpenInBrowser(GitHubRepoUrl);

    private void OnOpenWebsiteClick(object sender, RoutedEventArgs e) => OpenInBrowser(WebsiteUrl);

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Konnte Browser nicht öffnen: {ex.Message}\nURL: {url}",
                "Lumio Info",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
