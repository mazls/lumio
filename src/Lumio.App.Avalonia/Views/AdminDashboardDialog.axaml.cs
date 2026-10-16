using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Lumio.Camera;
using Lumio.Core.Camera;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Lumio.Imaging;
using Lumio.Printing;

namespace Lumio.App.Avalonia.Views;

public sealed record CameraOptionItem(string DisplayName, CameraBackend Backend, string? DeviceId)
{
    public override string ToString() => DisplayName;
}

public sealed record PrinterOptionItem(string DisplayName, string Name)
{
    public override string ToString() => DisplayName;
}

public sealed record TemplateOptionItem(string DisplayName, string Id)
{
    public override string ToString() => DisplayName;
}

public sealed record LogoPositionOption(string DisplayName, string Id)
{
    public override string ToString() => DisplayName;
}

public sealed record EventTextFormatOption(string DisplayName, string Id)
{
    public override string ToString() => DisplayName;
}

public partial class AdminDashboardDialog : Window
{
    private readonly SettingsService _settingsService;
    private readonly EventStorage _storage;
    private readonly SelphyPrintService _printService;
    private readonly TemplateStore _templateStore;

    public bool ExitRequested { get; private set; }

    public AdminDashboardDialog() : this(
        new SettingsService(new SettingsStore()),
        new EventStorage(new SettingsService(new SettingsStore()), TimeProvider.System),
        new SelphyPrintService(),
        new TemplateStore())
    {
    }

    public AdminDashboardDialog(
        SettingsService settingsService,
        EventStorage storage,
        SelphyPrintService? printService = null,
        TemplateStore? templateStore = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _storage = storage;
        _printService = printService ?? new SelphyPrintService();
        _templateStore = templateStore ?? new TemplateStore();

        LoadSettings();
        _ = PopulateCameraOptionsAsync();
        PopulateDslrOptions();
        PopulatePrinterOptions();
        PopulateTemplateOptions();
        PopulateBrandingOptions();
    }

    private void LoadSettings()
    {
        var current = _settingsService.Current;
        EventNameTextBox.Text = current.EventName;
        OutputFolderTextBox.Text = current.OutputRoot;
        CountdownUpDown.Value = current.CountdownSeconds;
        ReviewUpDown.Value = current.ReviewSeconds;
        AutoPrintCheckBox.IsChecked = current.AutoPrint;
        MirrorLiveViewCheckBox.IsChecked = current.MirrorLiveView;
        AdminPinTextBox.Text = current.AdminPin;
    }

    private async Task PopulateCameraOptionsAsync()
    {
        try
        {
            var options = new List<CameraOptionItem>
            {
                new("📸 Integrierte Testkamera (Simulation)", CameraBackend.Simulated, null),
                new("📷 Nikon DSLR / DSLM (D5600, Z 50 II über USB / gphoto2)", CameraBackend.Nikon, null),
            };

            var webcams = await WebcamCamera.FindDevicesAsync();
            foreach (var cam in webcams)
            {
                options.Add(new($"🎥 {cam.Name}", CameraBackend.Webcam, cam.Id));
            }

            CameraSelectionComboBox.ItemsSource = options;

            var current = _settingsService.Current;
            CameraOptionItem? matching = null;
            if (current.CameraBackend == CameraBackend.Webcam && current.WebcamDeviceId is not null)
            {
                matching = options.FirstOrDefault(o => o.Backend == CameraBackend.Webcam && o.DeviceId == current.WebcamDeviceId);
            }
            matching ??= options.FirstOrDefault(o => o.Backend == current.CameraBackend);
            matching ??= options.FirstOrDefault();

            CameraSelectionComboBox.SelectedItem = matching;
            CameraDetectionStatusText.Text = $"{webcams.Count} USB/V4L2-Kamera(s) erkannt";
        }
        catch (Exception ex)
        {
            CameraDetectionStatusText.Text = $"Kamera-Erkennung: {ex.Message}";
        }
    }

    private void PopulateDslrOptions()
    {
        var isoOptions = new[] { "Auto", "100", "200", "400", "800", "1600", "3200", "6400" };
        var shutterOptions = new[] { "Auto", "1/60s", "1/100s", "1/125s (Blitz-Standard)", "1/160s", "1/200s", "1/250s" };
        var apertureOptions = new[] { "Auto", "f/2.8", "f/3.5", "f/4.0", "f/5.6 (Empfohlen)", "f/8.0", "f/11" };
        var wbOptions = new[] { "Auto", "Tageslicht (5400K)", "Blitz (5600K)", "Kunstlicht (3200K)", "Leuchtstoff (4000K)", "Bewölkt (6500K)", "Schatten (7500K)" };
        var evOptions = new[] { "-2.0 EV (Viel dunkler)", "-1.0 EV (Dunkler)", "0 EV (Standard)", "+1.0 EV (Heller)", "+2.0 EV (Viel heller)" };

        CameraIsoComboBox.ItemsSource = isoOptions;
        CameraShutterComboBox.ItemsSource = shutterOptions;
        CameraApertureComboBox.ItemsSource = apertureOptions;
        CameraWhiteBalanceComboBox.ItemsSource = wbOptions;
        CameraExposureCompComboBox.ItemsSource = evOptions;

        var dslr = _settingsService.Current.CameraControl;

        CameraIsoComboBox.SelectedItem = isoOptions.FirstOrDefault(i => i.StartsWith(dslr.Iso, StringComparison.OrdinalIgnoreCase)) ?? "Auto";
        CameraShutterComboBox.SelectedItem = shutterOptions.FirstOrDefault(s => s.StartsWith(dslr.ShutterSpeed, StringComparison.OrdinalIgnoreCase)) ?? "1/125s (Blitz-Standard)";
        CameraApertureComboBox.SelectedItem = apertureOptions.FirstOrDefault(a => a.StartsWith(dslr.Aperture, StringComparison.OrdinalIgnoreCase)) ?? "f/5.6 (Empfohlen)";
        CameraWhiteBalanceComboBox.SelectedItem = wbOptions.FirstOrDefault(w => w.StartsWith(dslr.WhiteBalance, StringComparison.OrdinalIgnoreCase)) ?? "Auto";
        CameraExposureCompComboBox.SelectedItem = evOptions.FirstOrDefault(e => e.StartsWith(dslr.ExposureCompensation, StringComparison.OrdinalIgnoreCase)) ?? "0 EV (Standard)";
    }

    private void PopulatePrinterOptions()
    {
        try
        {
            var installed = SelphyPrintService.GetInstalledPrinters();
            var options = new List<PrinterOptionItem>();

            foreach (var p in installed)
            {
                var label = p.IsSelphy ? $"🖨️ {p.Name} (Canon Selphy)" : $"🖨️ {p.Name}";
                if (p.IsDefault) label += " [Standard]";
                options.Add(new PrinterOptionItem(label, p.Name));
            }

            if (options.Count == 0)
            {
                options.Add(new PrinterOptionItem("⚠️ Kein Drucker gefunden (CUPS)", string.Empty));
            }

            PrinterComboBox.ItemsSource = options;

            var current = _settingsService.Current;
            var selected = options.FirstOrDefault(o => o.Name == current.SelectedPrinter)
                           ?? options.FirstOrDefault(o => o.DisplayName.Contains("Selphy", StringComparison.OrdinalIgnoreCase))
                           ?? options.FirstOrDefault();

            PrinterComboBox.SelectedItem = selected;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Printer enumeration error: {ex.Message}");
        }
    }

    private void PopulateTemplateOptions()
    {
        try
        {
            var templates = _templateStore.GetAllTemplates();
            var options = templates.Select(t => new TemplateOptionItem($"🎨 {t.Name} ({t.RequiredPhotosCount} Foto(s))", t.Id)).ToList();

            TemplateSelectionComboBox.ItemsSource = options;

            var current = _settingsService.Current;
            var selected = options.FirstOrDefault(o => o.Id == current.SelectedTemplateId)
                           ?? options.FirstOrDefault();

            TemplateSelectionComboBox.SelectedItem = selected;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Template enumeration error: {ex.Message}");
        }
    }

    private void PopulateBrandingOptions()
    {
        var logoPositions = new List<LogoPositionOption>
        {
            new("Unten rechts (Empfohlen)", "BottomRight"),
            new("Unten links", "BottomLeft"),
            new("Oben rechts", "TopRight"),
            new("Oben links", "TopLeft"),
            new("Deaktiviert (Kein Logo)", "None"),
        };
        LogoPositionComboBox.ItemsSource = logoPositions;

        var textFormats = new List<EventTextFormatOption>
        {
            new("Event & Jahr (z.B. Mein Event – 2026)", "EventAndYear"),
            new("Event & Datum (z.B. Mein Event • 17.10.2026)", "EventAndDate"),
            new("Nur Event-Name (z.B. Mein Event)", "EventOnly"),
            new("Kein Text auf Fotos", "None"),
        };
        EventTextFormatComboBox.ItemsSource = textFormats;

        var current = _settingsService.Current;
        ShowLogoCheckBox.IsChecked = current.EmbedLogoOnPhotos;
        LogoPositionComboBox.SelectedItem = logoPositions.FirstOrDefault(o => o.Id.Equals(current.LogoPosition, StringComparison.OrdinalIgnoreCase)) ?? logoPositions[0];

        ShowEventTextCheckBox.IsChecked = current.EmbedEventTextOnPhotos;
        EventTextFormatComboBox.SelectedItem = textFormats.FirstOrDefault(o => o.Id.Equals(current.EventTextFormat, StringComparison.OrdinalIgnoreCase)) ?? textFormats[0];
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var copy = _settingsService.Current.Clone();
        if (!string.IsNullOrWhiteSpace(EventNameTextBox.Text))
            copy.EventName = EventNameTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(OutputFolderTextBox.Text))
            copy.OutputRoot = OutputFolderTextBox.Text.Trim();

        copy.CountdownSeconds = (int)(CountdownUpDown.Value ?? 3);
        copy.ReviewSeconds = (int)(ReviewUpDown.Value ?? 12);
        copy.AutoPrint = AutoPrintCheckBox.IsChecked ?? false;
        copy.MirrorLiveView = MirrorLiveViewCheckBox.IsChecked ?? true;
        if (!string.IsNullOrWhiteSpace(AdminPinTextBox.Text))
            copy.AdminPin = AdminPinTextBox.Text.Trim();

        if (CameraSelectionComboBox.SelectedItem is CameraOptionItem selectedCam)
        {
            copy.CameraBackend = selectedCam.Backend;
            copy.WebcamDeviceId = selectedCam.DeviceId;
        }

        if (PrinterComboBox.SelectedItem is PrinterOptionItem selectedPrinter && !string.IsNullOrEmpty(selectedPrinter.Name))
        {
            copy.SelectedPrinter = selectedPrinter.Name;
        }

        if (TemplateSelectionComboBox.SelectedItem is TemplateOptionItem selectedTemplate)
        {
            copy.SelectedTemplateId = selectedTemplate.Id;
        }

        copy.EmbedLogoOnPhotos = ShowLogoCheckBox.IsChecked ?? true;
        if (LogoPositionComboBox.SelectedItem is LogoPositionOption selectedLogoPos)
        {
            copy.LogoPosition = selectedLogoPos.Id;
        }

        copy.EmbedEventTextOnPhotos = ShowEventTextCheckBox.IsChecked ?? true;
        if (EventTextFormatComboBox.SelectedItem is EventTextFormatOption selectedTextFmt)
        {
            copy.EventTextFormat = selectedTextFmt.Id;
        }

        copy.CameraControl.Iso = CameraIsoComboBox.SelectedItem?.ToString() ?? "Auto";
        copy.CameraControl.ShutterSpeed = CameraShutterComboBox.SelectedItem?.ToString() ?? "1/125s (Blitz-Standard)";
        copy.CameraControl.Aperture = CameraApertureComboBox.SelectedItem?.ToString() ?? "f/5.6 (Empfohlen)";
        copy.CameraControl.WhiteBalance = CameraWhiteBalanceComboBox.SelectedItem?.ToString() ?? "Auto";
        copy.CameraControl.ExposureCompensation = CameraExposureCompComboBox.SelectedItem?.ToString() ?? "0 EV (Standard)";

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
