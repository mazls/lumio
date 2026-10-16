using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Lumio.Printing;

namespace Lumio.App.Avalonia.Views;

public partial class AdminDashboardDialog : Window
{
    private readonly SettingsService _settingsService;
    private readonly EventStorage _storage;
    private readonly SelphyPrintService _printService;

    public bool ExitRequested { get; private set; }

    public AdminDashboardDialog() : this(
        new SettingsService(new SettingsStore()),
        new EventStorage(new SettingsService(new SettingsStore()), TimeProvider.System),
        new SelphyPrintService())
    {
    }

    public AdminDashboardDialog(SettingsService settingsService, EventStorage storage, SelphyPrintService printService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _storage = storage;
        _printService = printService;

        LoadSettings();
    }

    private void LoadSettings()
    {
        var current = _settingsService.Current;
        EventNameTextBox.Text = current.EventName;
        CountdownUpDown.Value = current.CountdownSeconds;
        ReviewUpDown.Value = current.ReviewSeconds;
        AutoPrintCheckBox.IsChecked = current.AutoPrint;
        AdminPinTextBox.Text = current.AdminPin;

        CameraSelectionComboBox.SelectedIndex = current.CameraBackend switch
        {
            CameraBackend.Nikon => 0,
            CameraBackend.Webcam => 1,
            _ => 2,
        };

        // Populate printers
        var printers = SelphyPrintService.GetInstalledPrinters();
        PrinterComboBox.Items.Clear();
        foreach (var p in printers)
        {
            var item = new ComboBoxItem { Content = p.IsSelphy ? $"🖨️ {p.Name} (Selphy)" : p.Name, Tag = p.Name };
            PrinterComboBox.Items.Add(item);
            if (string.Equals(p.Name, current.SelectedPrinter, StringComparison.OrdinalIgnoreCase))
            {
                PrinterComboBox.SelectedItem = item;
            }
        }

        if (PrinterComboBox.SelectedItem is null && PrinterComboBox.Items.Count > 0)
        {
            PrinterComboBox.SelectedIndex = 0;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var copy = _settingsService.Current.Clone();
        copy.EventName = EventNameTextBox.Text?.Trim() ?? "Event";
        copy.CountdownSeconds = (int)(CountdownUpDown.Value ?? 3);
        copy.ReviewSeconds = (int)(ReviewUpDown.Value ?? 12);
        copy.AutoPrint = AutoPrintCheckBox.IsChecked ?? false;
        if (!string.IsNullOrWhiteSpace(AdminPinTextBox.Text))
            copy.AdminPin = AdminPinTextBox.Text.Trim();

        copy.CameraBackend = CameraSelectionComboBox.SelectedIndex switch
        {
            0 => CameraBackend.Nikon,
            1 => CameraBackend.Webcam,
            _ => CameraBackend.Simulated,
        };

        if (PrinterComboBox.SelectedItem is ComboBoxItem item && item.Tag is string printerName)
        {
            copy.SelectedPrinter = printerName;
        }

        _settingsService.Update(copy);
        Close();
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folder = _storage.GetEventFolder();
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new AboutDialog();
        about.ShowDialog(this);
    }

    private void OnExitAppClick(object? sender, RoutedEventArgs e)
    {
        ExitRequested = true;
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
