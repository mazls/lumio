using System.Diagnostics;
using System.IO;
using System.Windows;
using Lumio.Camera;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Lumio.Imaging;
using Lumio.Printing;
using SkiaSharp;

namespace Lumio.App.Views;

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
    private readonly TemplateStore _templateStore;
    private readonly SelphyPrintService _printService;

    public bool ExitRequested { get; private set; }

    public AdminDashboardDialog(
        SettingsService settingsService,
        EventStorage storage,
        TemplateStore? templateStore = null,
        SelphyPrintService? printService = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _storage = storage;
        _templateStore = templateStore ?? new TemplateStore();
        _printService = printService ?? new SelphyPrintService();

        LoadCurrentSettings();
        _ = PopulateCameraOptionsAsync();
        PopulateDslrOptions();
        PopulatePrinterOptions();
        PopulateTemplateOptions();
        PopulateBrandingOptions();
    }

    private void LoadCurrentSettings()
    {
        var current = _settingsService.Current;
        EventNameTextBox.Text = current.EventName;
        OutputFolderTextBox.Text = current.OutputRoot;
        CountdownTextBox.Text = current.CountdownSeconds.ToString();
        MultiShotPauseTextBox.Text = current.MultiShotPauseSeconds.ToString();
        ReviewTextBox.Text = current.ReviewSeconds.ToString();
        MirrorLiveViewCheckBox.IsChecked = current.MirrorLiveView;
        AdminPinTextBox.Text = current.AdminPin;
        AutoPrintCheckBox.IsChecked = current.AutoPrint;
    }

    private async Task PopulateCameraOptionsAsync()
    {
        try
        {
            var options = new List<CameraOptionItem>
            {
                new("📸 Integrierte Testkamera (Simulation)", CameraBackend.Simulated, null),
                new("📷 Nikon DSLR / DSLM (D5600, Z 50 II über USB)", CameraBackend.Nikon, null),
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
            CameraDetectionStatusText.Text = $"{webcams.Count} Kamera(s) auf dem Surface erkannt";
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
                options.Add(new PrinterOptionItem("⚠️ Kein Drucker in Windows installiert", string.Empty));
            }

            PrinterSelectionComboBox.ItemsSource = options;

            var current = _settingsService.Current;
            var selected = options.FirstOrDefault(o => o.Name == current.SelectedPrinter)
                           ?? options.FirstOrDefault(o => o.DisplayName.Contains("Selphy", StringComparison.OrdinalIgnoreCase))
                           ?? options.FirstOrDefault();

            PrinterSelectionComboBox.SelectedItem = selected;
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

    private async void OnTestPrintClick(object sender, RoutedEventArgs e)
    {
        if (PrinterSelectionComboBox.SelectedItem is not PrinterOptionItem printerOption || string.IsNullOrEmpty(printerOption.Name))
        {
            MessageBox.Show(this, "Bitte wähle zuerst einen Drucker aus.", "Drucken", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var templateId = (TemplateSelectionComboBox.SelectedItem as TemplateOptionItem)?.Id ?? "single-10x15";
            var template = _templateStore.GetById(templateId) ?? BuiltInTemplates.CreateSinglePhotoPostcard();

            // Render a test sample collage using generated test photos
            var samplePhotos = new List<byte[]>();
            for (var i = 0; i < template.RequiredPhotosCount; i++)
            {
                using var bmp = new SKBitmap(1200, 800);
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(i % 2 == 0 ? new SKColor(30, 41, 59) : new SKColor(71, 85, 105));
                using var font = new SKFont(SKTypeface.Default, 48);
                using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                canvas.DrawText($"Testfoto {i + 1}", 400, 420, SKTextAlign.Center, font, paint);
                using var img = SKImage.FromBitmap(bmp);
                using var encoded = img.Encode(SKEncodedImageFormat.Jpeg, 90);
                samplePhotos.Add(encoded.ToArray());
            }

            var metadata = new Dictionary<string, string>
            {
                ["EventName"] = EventNameTextBox.Text.Trim(),
                ["Date"] = DateTime.Now.ToString("dd.MM.yyyy"),
            };

            var renderer = new CollageRenderer();
            var testJpeg = renderer.Render(template, samplePhotos, metadata);

            await _printService.PrintPhotoAsync(testJpeg, printerOption.Name, copies: 1);

            MessageBox.Show(this,
                $"Testdruck erfolgreich an '{printerOption.Name}' gesendet!\n\nPrüfe deinen Canon Selphy.",
                "Druckauftrag gesendet",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Testdruck fehlgeschlagen:\n{ex.Message}",
                "Druckfehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnOpenTemplatesFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = _templateStore.TemplatesDirectory;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var updated = _settingsService.Current.Clone();

        updated.EventName = EventNameTextBox.Text.Trim();
        updated.OutputRoot = OutputFolderTextBox.Text.Trim();

        if (int.TryParse(CountdownTextBox.Text, out var countdown))
            updated.CountdownSeconds = countdown;

        if (int.TryParse(MultiShotPauseTextBox.Text, out var pause))
            updated.MultiShotPauseSeconds = pause;

        if (int.TryParse(ReviewTextBox.Text, out var review))
            updated.ReviewSeconds = review;

        updated.MirrorLiveView = MirrorLiveViewCheckBox.IsChecked ?? true;
        updated.AutoPrint = AutoPrintCheckBox.IsChecked ?? false;

        var pin = AdminPinTextBox.Text.Trim();
        if (pin.Length >= 4 && pin.All(char.IsAsciiDigit))
            updated.AdminPin = pin;

        if (CameraSelectionComboBox.SelectedItem is CameraOptionItem selectedCam)
        {
            updated.CameraBackend = selectedCam.Backend;
            updated.WebcamDeviceId = selectedCam.DeviceId;
        }

        if (PrinterSelectionComboBox.SelectedItem is PrinterOptionItem selectedPrinter && !string.IsNullOrEmpty(selectedPrinter.Name))
        {
            updated.SelectedPrinter = selectedPrinter.Name;
        }

        if (TemplateSelectionComboBox.SelectedItem is TemplateOptionItem selectedTemplate)
        {
            updated.SelectedTemplateId = selectedTemplate.Id;
        }

        updated.EmbedLogoOnPhotos = ShowLogoCheckBox.IsChecked ?? true;
        if (LogoPositionComboBox.SelectedItem is LogoPositionOption selectedLogoPos)
        {
            updated.LogoPosition = selectedLogoPos.Id;
        }

        updated.EmbedEventTextOnPhotos = ShowEventTextCheckBox.IsChecked ?? true;
        if (EventTextFormatComboBox.SelectedItem is EventTextFormatOption selectedTextFmt)
        {
            updated.EventTextFormat = selectedTextFmt.Id;
        }

        updated.CameraControl.Iso = CameraIsoComboBox.SelectedItem?.ToString() ?? "Auto";
        updated.CameraControl.ShutterSpeed = CameraShutterComboBox.SelectedItem?.ToString() ?? "1/125s (Blitz-Standard)";
        updated.CameraControl.Aperture = CameraApertureComboBox.SelectedItem?.ToString() ?? "f/5.6 (Empfohlen)";
        updated.CameraControl.WhiteBalance = CameraWhiteBalanceComboBox.SelectedItem?.ToString() ?? "Auto";
        updated.CameraControl.ExposureCompensation = CameraExposureCompComboBox.SelectedItem?.ToString() ?? "0 EV (Standard)";

        _settingsService.Update(updated);

        MessageBox.Show(this, "Einstellungen wurden gespeichert!", "Lumio", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = _storage.GetEventFolder();
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var aboutDialog = new AboutDialog
        {
            Owner = this
        };
        aboutDialog.ShowDialog();
    }

    private void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Möchtest du Lumio wirklich beenden und zum Desktop zurückkehren?",
            "Beenden bestätigen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            ExitRequested = true;
            Close();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
