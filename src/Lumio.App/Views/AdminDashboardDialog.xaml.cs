using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Lumio.Camera;
using Lumio.Core.Camera;
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
    private TextBox? _currentFocusedTextBox;

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
        PopulateUsbDrives();
        _ = CheckDigiCamStatusAsync();
    }

    private void LoadCurrentSettings()
    {
        var current = _settingsService.Current;
        EventNameTextBox.Text = current.EventName;
        OutputFolderTextBox.Text = current.OutputRoot;
        CountdownTextBox.Text = current.CountdownSeconds.ToString();
        MultiShotPauseTextBox.Text = current.MultiShotPauseSeconds.ToString();
        ReviewTextBox.Text = current.ReviewSeconds.ToString();
        EnableVideoMessagesCheckBox.IsChecked = current.EnableVideoMessages;
        VideoDurationTextBox.Text = current.VideoMessageDurationSeconds.ToString();
        MirrorLiveViewCheckBox.IsChecked = current.MirrorLiveView;
        AdminPinTextBox.Text = current.AdminPin;
        AutoPrintCheckBox.IsChecked = current.AutoPrint;
        OnScreenKeyboardCheckBox.IsChecked = current.OnScreenKeyboardEnabled;
    }

    private async Task PopulateCameraOptionsAsync()
    {
        try
        {
            var options = new List<CameraOptionItem>
            {
                new("📸 Integrierte Testkamera (Simulation)", CameraBackend.Simulated, null),
                new("📷 DSLR / Systemkamera (Nikon, Canon, Sony, Fuji via USB)", CameraBackend.Dslr, null),
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
            if (matching is null && current.CameraBackend == CameraBackend.Nikon)
            {
                matching = options.FirstOrDefault(o => o.Backend == CameraBackend.Dslr);
            }
            matching ??= options.FirstOrDefault();

            CameraSelectionComboBox.SelectedItem = matching;
            CameraDetectionStatusText.Text = $"{webcams.Count} Kamera(s) auf dem System erkannt";
        }
        catch (Exception ex)
        {
            CameraDetectionStatusText.Text = $"Kamera-Erkennung: {ex.Message}";
        }
    }

    private void PopulateDslrOptions()
    {
        CameraIsoComboBox.ItemsSource = DslrOptions.Iso;
        CameraShutterComboBox.ItemsSource = DslrOptions.ShutterSpeeds;
        CameraApertureComboBox.ItemsSource = DslrOptions.Apertures;
        CameraWhiteBalanceComboBox.ItemsSource = DslrOptions.WhiteBalances;
        CameraExposureCompComboBox.ItemsSource = DslrOptions.ExposureCompensations;

        var dslr = _settingsService.Current.CameraControl;

        CameraIsoComboBox.SelectedItem = DslrOptions.Find(DslrOptions.Iso, dslr.Iso);
        CameraShutterComboBox.SelectedItem = DslrOptions.Find(DslrOptions.ShutterSpeeds, dslr.ShutterSpeed);
        CameraApertureComboBox.SelectedItem = DslrOptions.Find(DslrOptions.Apertures, dslr.Aperture);
        CameraWhiteBalanceComboBox.SelectedItem = DslrOptions.Find(DslrOptions.WhiteBalances, dslr.WhiteBalance);
        CameraExposureCompComboBox.SelectedItem = DslrOptions.Find(DslrOptions.ExposureCompensations, dslr.ExposureCompensation);
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

    private void PopulateUsbDrives()
    {
        var drives = UsbDriveDetector.DetectDrives();
        if (drives.Count > 0)
        {
            UsbDrivesComboBox.ItemsSource = drives;
            var currentRoot = _settingsService.Current.OutputRoot;
            var matched = drives.FirstOrDefault(d => currentRoot.StartsWith(d.RootPath, StringComparison.OrdinalIgnoreCase)) ?? drives[0];
            UsbDrivesComboBox.SelectedItem = matched;
            SelectUsbButton.IsEnabled = true;
            UsbStatusText.Text = $"✅ {drives.Count} USB-Laufwerk(e) gefunden.";
        }
        else
        {
            UsbDrivesComboBox.ItemsSource = new List<string> { "Kein USB-Laufwerk erkannt" };
            UsbDrivesComboBox.SelectedIndex = 0;
            SelectUsbButton.IsEnabled = false;
            UsbStatusText.Text = "💡 Schließe einen USB-Stick an, um Fotos & Videos direkt extern zu sichern.";
        }
    }

    private System.Windows.Media.Brush GetThemeBrush(string key, System.Windows.Media.Brush fallback)
    {
        return TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
    }

    private async Task CheckDigiCamStatusAsync()
    {
        try
        {
            var url = BoothSettings.DefaultDigiCamControlUrl;
            var status = await DigiCamControlManager.GetStatusAsync(url);
            if (status.IsReachable)
            {
                var camInfo = !string.IsNullOrWhiteSpace(status.ConnectedCamera) && status.ConnectedCamera != "null"
                    ? $"Kamera verbunden: {status.ConnectedCamera}"
                    : "Bereit – bitte Nikon/Canon/DSLR per USB anstecken und einschalten";

                var successBrush = GetThemeBrush("SuccessBrush", System.Windows.Media.Brushes.LightGreen);
                DigiCamStatusIndicator.Fill = successBrush;
                DigiCamStatusText.Text = $"✅ {camInfo}";
                DigiCamStatusText.Foreground = successBrush;
                return;
            }

            if (status.IsProcessRunning)
            {
                var orangeBrush = GetThemeBrush("AccentOrangeBrush", System.Windows.Media.Brushes.Orange);
                DigiCamStatusIndicator.Fill = orangeBrush;
                DigiCamStatusText.Text = "⏳ Kamera-Treiber wird initialisiert...";
                DigiCamStatusText.Foreground = orangeBrush;
                return;
            }
        }
        catch { }

        var mutedBrush = GetThemeBrush("TextMutedBrush", System.Windows.Media.Brushes.Gray);
        DigiCamStatusIndicator.Fill = mutedBrush;
        DigiCamStatusText.Text = "ℹ️ DSLR-Treiber startet automatisch bei Auswahl von 'DSLR / Systemkamera'.";
        DigiCamStatusText.Foreground = mutedBrush;
    }

    private async void OnCheckDigiCamStatusClick(object sender, RoutedEventArgs e)
    {
        await CheckDigiCamStatusAsync();
    }

    private void OnSelectUsbDriveClick(object sender, RoutedEventArgs e)
    {
        if (UsbDrivesComboBox.SelectedItem is UsbDriveInfo drive)
        {
            OutputFolderTextBox.Text = drive.RootPath;
            UsbStatusText.Text = $"✅ Speicherort auf USB-Laufwerk '{drive.Name}' gesetzt!";
        }
    }

    private void OnBrowseOutputFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Speicherordner für Fotos & Videos wählen",
            InitialDirectory = Directory.Exists(OutputFolderTextBox.Text) ? OutputFolderTextBox.Text : null,
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputFolderTextBox.Text = dialog.FolderName;
        }
    }

    #region On-Screen Keyboard Handling

    private void OnInputGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            _currentFocusedTextBox = tb;
            if (OnScreenKeyboardCheckBox.IsChecked == true)
            {
                OnScreenKeyboardBorder.Visibility = Visibility.Visible;
            }
        }
    }

    private void OnKeyboardToggleClick(object sender, RoutedEventArgs e)
    {
        if (OnScreenKeyboardCheckBox.IsChecked == true)
        {
            OnScreenKeyboardBorder.Visibility = Visibility.Visible;
            if (_currentFocusedTextBox is null)
                _currentFocusedTextBox = EventNameTextBox;
        }
        else
        {
            OnScreenKeyboardBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCloseKeyboardClick(object sender, RoutedEventArgs e)
    {
        OnScreenKeyboardBorder.Visibility = Visibility.Collapsed;
    }

    private void OnKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key && _currentFocusedTextBox is not null)
        {
            var tb = _currentFocusedTextBox;
            var caret = tb.SelectionStart;
            tb.Text = tb.Text.Insert(caret, key);
            tb.SelectionStart = caret + key.Length;
            tb.Focus();
        }
    }

    private void OnBackspaceClick(object sender, RoutedEventArgs e)
    {
        if (_currentFocusedTextBox is not null)
        {
            var tb = _currentFocusedTextBox;
            var caret = tb.SelectionStart;
            if (tb.SelectionLength > 0)
            {
                tb.Text = tb.Text.Remove(caret, tb.SelectionLength);
                tb.SelectionStart = caret;
            }
            else if (caret > 0)
            {
                tb.Text = tb.Text.Remove(caret - 1, 1);
                tb.SelectionStart = caret - 1;
            }
            tb.Focus();
        }
    }

    #endregion

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
                $"Testdruck erfolgreich an '{printerOption.Name}' gesendet!\n\nPrüfe deinen Drucker.",
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
        updated.OnScreenKeyboardEnabled = OnScreenKeyboardCheckBox.IsChecked ?? false;

        if (int.TryParse(CountdownTextBox.Text, out var countdown))
            updated.CountdownSeconds = countdown;

        if (int.TryParse(MultiShotPauseTextBox.Text, out var pause))
            updated.MultiShotPauseSeconds = pause;

        if (int.TryParse(ReviewTextBox.Text, out var review))
            updated.ReviewSeconds = review;

        updated.EnableVideoMessages = EnableVideoMessagesCheckBox.IsChecked ?? true;
        if (int.TryParse(VideoDurationTextBox.Text, out var vidDur))
            updated.VideoMessageDurationSeconds = vidDur;

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

        if (CameraIsoComboBox.SelectedItem is DslrOption iso) updated.CameraControl.Iso = iso.Value;
        if (CameraShutterComboBox.SelectedItem is DslrOption shutter) updated.CameraControl.ShutterSpeed = shutter.Value;
        if (CameraApertureComboBox.SelectedItem is DslrOption aperture) updated.CameraControl.Aperture = aperture.Value;
        if (CameraWhiteBalanceComboBox.SelectedItem is DslrOption wb) updated.CameraControl.WhiteBalance = wb.Value;
        if (CameraExposureCompComboBox.SelectedItem is DslrOption ev) updated.CameraControl.ExposureCompensation = ev.Value;

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
        var aboutDialog = new AboutDialog { Owner = this };
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
