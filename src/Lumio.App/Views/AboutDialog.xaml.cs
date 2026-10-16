using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Lumio.App.Views;

public partial class AboutDialog : Window
{
    private const string GitHubRepoUrl = "https://github.com/mazls/lumio";

    public AboutDialog()
    {
        InitializeComponent();
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

    private void OnOpenGitHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GitHubRepoUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Konnte Browser nicht öffnen: {ex.Message}\nURL: {GitHubRepoUrl}",
                "Lumio Info",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
