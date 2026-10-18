using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Lumio.Camera;
using Lumio.Core.Camera;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Lumio.Core.Updates;
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
    private readonly CameraManager? _cameraManager;
    private TextBox? _currentFocusedTextBox;
    private System.Windows.Threading.DispatcherTimer? _hardwareScanTimer;
    private int _isScanningHardware;

    public bool ExitRequested { get; private set; }

    public AdminDashboardDialog(
        SettingsService settingsService,
        EventStorage storage,
        TemplateStore? templateStore = null,
        SelphyPrintService? printService = null,
        CameraManager? cameraManager = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _storage = storage;
        _templateStore = templateStore ?? new TemplateStore();
        _printService = printService ?? new SelphyPrintService();
        _cameraManager = cameraManager;

        LoadCurrentSettings();
        _ = ShowVersionInfoAsync();
        PopulateInitialDropdowns();
        PopulateDslrOptions();
        PopulateTemplateOptions();
        PopulateBrandingOptions();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void PopulateInitialDropdowns()
    {
        // 1. Instant camera options
        var initialCams = new List<CameraOptionItem>
        {
            new("📸 Integrierte Testkamera (Simulation)", CameraBackend.Simulated, null),
            new("📷 DSLR / Systemkamera (Nikon D5600, Z 50 II, Canon via USB)", CameraBackend.Dslr, null),
        };
        CameraSelectionComboBox.ItemsSource = initialCams;
        CameraSelectionComboBox.SelectedItem = _settingsService.Current.CameraBackend == CameraBackend.Simulated
            ? initialCams[0]
            : initialCams[1];

        // 2. Instant printer placeholder
        var currentPrinter = _settingsService.Current.SelectedPrinter;
        var initialPrinterText = !string.IsNullOrWhiteSpace(currentPrinter)
            ? $"🖨️ {currentPrinter}"
            : "🖨️ Drucker werden gesucht...";
        PrinterSelectionComboBox.ItemsSource = new List<PrinterOptionItem> { new(initialPrinterText, currentPrinter ?? string.Empty) };
        PrinterSelectionComboBox.SelectedIndex = 0;

        // 3. Instant USB placeholder
        UsbDrivesComboBox.ItemsSource = new List<string> { "💾 USB-Laufwerke werden gesucht..." };
        UsbDrivesComboBox.SelectedIndex = 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = ScanHardwareAsync();

        // Auto-refresh USB, Camera, Printer and DSLR every 3 seconds while Admin menu is open
        _hardwareScanTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _hardwareScanTimer.Tick += async (_, _) => await ScanHardwareAsync();
        _hardwareScanTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hardwareScanTimer?.Stop();
        _hardwareScanTimer = null;
    }

    private async void OnRefreshHardwareClick(object sender, RoutedEventArgs e)
    {
        await ScanHardwareAsync();
    }

    private async Task ScanHardwareAsync()
    {
        if (Interlocked.CompareExchange(ref _isScanningHardware, 1, 0) != 0)
            return;

        try
        {
            await PopulateUsbDrivesAsync();
            await PopulateCameraOptionsAsync();
            await PopulatePrinterOptionsAsync();
            await CheckDigiCamStatusAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Hardware scan error: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _isScanningHardware, 0);
        }
    }

    private void LoadCurrentSettings()
    {
        var current = _settingsService.Current;
        EventNameTextBox.Text = current.EventName;
        ShowEventFolderInfo();
        OutputFolderTextBox.Text = current.OutputRoot;
        CountdownTextBox.Text = current.CountdownSeconds.ToString();
        MultiShotPauseTextBox.Text = current.MultiShotPauseSeconds.ToString();
        ReviewTextBox.Text = current.ReviewSeconds.ToString();
        EnableVideoMessagesCheckBox.IsChecked = current.EnableVideoMessages;
        VideoDurationTextBox.Text = current.VideoMessageDurationSeconds.ToString();
        MirrorLiveViewCheckBox.IsChecked = current.MirrorLiveView;
        LiveViewPowerSavingCheckBox.IsChecked = current.LiveViewOnlyDuringCapture;
        AdminPinTextBox.Text = current.AdminPin;
        AutoPrintCheckBox.IsChecked = current.AutoPrint;
        BorderlessPrintCheckBox.IsChecked = current.BorderlessPrint;
        OnScreenKeyboardCheckBox.IsChecked = current.OnScreenKeyboardEnabled;
    }

    private async Task PopulateCameraOptionsAsync()
    {
        try
        {
            var webcams = await WebcamCamera.FindDevicesAsync();
            var options = new List<CameraOptionItem>
            {
                new("📸 Integrierte Testkamera (Simulation)", CameraBackend.Simulated, null),
                new("📷 DSLR / Systemkamera (Nikon D5600, Z 50 II, Canon via USB)", CameraBackend.Dslr, null),
            };

            foreach (var cam in webcams)
            {
                options.Add(new($"🎥 {cam.Name}", CameraBackend.Webcam, cam.Id));
            }

            var currentOptions = CameraSelectionComboBox.ItemsSource as IReadOnlyList<CameraOptionItem>
                                 ?? (CameraSelectionComboBox.ItemsSource as List<CameraOptionItem>);

            bool changed = currentOptions is null || currentOptions.Count != options.Count;
            if (!changed && currentOptions is not null)
            {
                for (var i = 0; i < options.Count; i++)
                {
                    if (currentOptions[i].Backend != options[i].Backend || currentOptions[i].DeviceId != options[i].DeviceId)
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed && CameraSelectionComboBox.SelectedItem is CameraOptionItem)
            {
                return;
            }

            var selectedItem = CameraSelectionComboBox.SelectedItem as CameraOptionItem;
            CameraSelectionComboBox.ItemsSource = options;

            var current = _settingsService.Current;
            CameraOptionItem? matching = null;
            if (selectedItem is not null)
            {
                matching = options.FirstOrDefault(o => o.Backend == selectedItem.Backend && o.DeviceId == selectedItem.DeviceId);
            }
            if (matching is null && current.CameraBackend == CameraBackend.Webcam && current.WebcamDeviceId is not null)
            {
                matching = options.FirstOrDefault(o => o.Backend == CameraBackend.Webcam && o.DeviceId == current.WebcamDeviceId);
            }
            matching ??= options.FirstOrDefault(o => o.Backend == current.CameraBackend);
            if (matching is null && (current.CameraBackend == CameraBackend.Nikon || current.CameraBackend == CameraBackend.Dslr))
            {
                matching = options.FirstOrDefault(o => o.Backend == CameraBackend.Dslr);
            }
            matching ??= options.FirstOrDefault();

            CameraSelectionComboBox.SelectedItem = matching;
            CameraDetectionStatusText.Text = $"{options.Count - 1} Kamera-Quelle(n) verfügbar (Auto-Scan aktiv)";
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

    private async Task PopulatePrinterOptionsAsync()
    {
        try
        {
            var installed = await Task.Run(() => SelphyPrintService.GetInstalledPrinters());
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

            var currentOptions = PrinterSelectionComboBox.ItemsSource as IReadOnlyList<PrinterOptionItem>
                                 ?? (PrinterSelectionComboBox.ItemsSource as List<PrinterOptionItem>);

            bool changed = currentOptions is null || currentOptions.Count != options.Count;
            if (!changed && currentOptions is not null)
            {
                for (var i = 0; i < options.Count; i++)
                {
                    if (currentOptions[i].Name != options[i].Name)
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed && PrinterSelectionComboBox.SelectedItem is PrinterOptionItem)
            {
                return;
            }

            var selectedItem = PrinterSelectionComboBox.SelectedItem as PrinterOptionItem;
            PrinterSelectionComboBox.ItemsSource = options;

            var current = _settingsService.Current;
            var selected = (selectedItem is not null ? options.FirstOrDefault(o => o.Name == selectedItem.Name) : null)
                           ?? options.FirstOrDefault(o => o.Name == current.SelectedPrinter)
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

    private async Task PopulateUsbDrivesAsync()
    {
        try
        {
            var drives = await Task.Run(() => UsbDriveDetector.DetectDrives());

            var existingDrives = UsbDrivesComboBox.ItemsSource as IReadOnlyList<UsbDriveInfo>
                                 ?? (UsbDrivesComboBox.ItemsSource as List<UsbDriveInfo>);

            bool changed = existingDrives is null || existingDrives.Count != drives.Count;
            if (!changed && existingDrives is not null)
            {
                for (var i = 0; i < drives.Count; i++)
                {
                    if (existingDrives[i].RootPath != drives[i].RootPath)
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed && UsbDrivesComboBox.SelectedItem is UsbDriveInfo)
            {
                return;
            }

            if (drives.Count > 0)
            {
                var previousRoot = (UsbDrivesComboBox.SelectedItem as UsbDriveInfo)?.RootPath;
                UsbDrivesComboBox.ItemsSource = drives;
                var currentRoot = _settingsService.Current.OutputRoot;
                var matched = (previousRoot is not null ? drives.FirstOrDefault(d => string.Equals(d.RootPath, previousRoot, StringComparison.OrdinalIgnoreCase)) : null)
                              ?? drives.FirstOrDefault(d => currentRoot.StartsWith(d.RootPath, StringComparison.OrdinalIgnoreCase))
                              ?? drives[0];

                UsbDrivesComboBox.SelectedItem = matched;
                SelectUsbButton.IsEnabled = true;
                UsbStatusText.Text = $"✅ {drives.Count} USB-Laufwerk(e) gefunden (Auto-Scan aktiv).";
            }
            else
            {
                UsbDrivesComboBox.ItemsSource = new List<string> { "Kein USB-Laufwerk erkannt" };
                UsbDrivesComboBox.SelectedIndex = 0;
                SelectUsbButton.IsEnabled = false;
                UsbStatusText.Text = "💡 Schließe einen USB-Stick an, um Fotos & Videos direkt extern zu sichern (Auto-Scan aktiv).";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"USB enumeration error: {ex.Message}");
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
            var dslrDevices = await DslrCamera.FindConnectedDevicesAsync();
            if (dslrDevices.Count > 0)
            {
                var successBrush = GetThemeBrush("SuccessBrush", System.Windows.Media.Brushes.LightGreen);
                DigiCamStatusIndicator.Fill = successBrush;
                DigiCamStatusText.Text = $"✅ {dslrDevices[0].Name} verbunden (Integrierter Treiber)";
                DigiCamStatusText.Foreground = successBrush;
                return;
            }
        }
        catch { }

        var mutedBrush = GetThemeBrush("TextMutedBrush", System.Windows.Media.Brushes.Gray);
        DigiCamStatusIndicator.Fill = mutedBrush;
        DigiCamStatusText.Text = "ℹ️ Integrierter DSLR-Treiber bereit – bitte Nikon, Canon oder Sony per USB anschließen.";
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
            var testJpeg = renderer.Render(template, samplePhotos, metadata, jpegQuality: 98, renderScale: 2.0f);

            await _printService.PrintPhotoAsync(testJpeg, printerOption.Name, copies: 1, borderless: BorderlessPrintCheckBox.IsChecked ?? true);

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
        // A new event (or a renamed one) gets its own folder, dated with its first photo
        if (_startNewEvent || updated.EventName != _settingsService.Current.EventName)
            updated.EventStartDate = null;
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
        updated.LiveViewOnlyDuringCapture = LiveViewPowerSavingCheckBox.IsChecked ?? false;
        updated.AutoPrint = AutoPrintCheckBox.IsChecked ?? false;
        updated.BorderlessPrint = BorderlessPrintCheckBox.IsChecked ?? true;

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

    /// <summary>Shows the running version and, if GitHub has a newer release, a download hint (checked again on open).</summary>
    private async Task ShowVersionInfoAsync()
    {
        CurrentVersionText.Text = $"Lumio {UpdateChecker.CurrentVersion.ToString(3)}";
        ShowUpdate(UpdateChecker.AvailableUpdate);
        ShowUpdate(await UpdateChecker.CheckAsync() ?? UpdateChecker.AvailableUpdate);
    }

    private void ShowUpdate(ReleaseInfo? release)
    {
        UpdateAvailablePanel.Visibility = release is not null ? Visibility.Visible : Visibility.Collapsed;
        if (release is not null)
            UpdateAvailableText.Text = $"🎉 Neue Version {release.DisplayVersion} verfügbar";
    }

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = UpdateChecker.AvailableUpdate?.Url ?? UpdateChecker.ReleasesPage,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private bool _startNewEvent;

    private void ShowEventFolderInfo()
    {
        EventFolderInfoText.Text = _settingsService.Current.EventStartDate is { } started
                                   && DateOnly.TryParse(started, System.Globalization.CultureInfo.InvariantCulture, out var date)
            ? $"📂 {_storage.GetEventFolderName()} – läuft seit {date:dd.MM.yyyy}, auch über Mitternacht hinaus"
            : $"📂 {_storage.GetEventFolderName()} – beginnt mit dem ersten Foto";
    }

    private void OnStartNewEventClick(object sender, RoutedEventArgs e)
    {
        _startNewEvent = true;
        EventFolderInfoText.Text = "🆕 Nach dem Speichern beginnt mit dem nächsten Foto ein neuer Event-Ordner.";
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

    private async void OnTakeTestPhotoClick(object sender, RoutedEventArgs e)
    {
        TakeTestPhotoButton.IsEnabled = false;
        TakeTestPhotoButton.Content = "⏳ Aufnahme...";

        try
        {
            if (CameraSelectionComboBox.SelectedItem is not CameraOptionItem selectedCam)
            {
                MessageBox.Show(this, "Bitte wähle zuerst eine Kamera aus der Liste aus.", "Testfoto", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dslrSettings = new DslrCameraSettings();
            if (CameraIsoComboBox.SelectedItem is DslrOption iso) dslrSettings.Iso = iso.Value;
            if (CameraShutterComboBox.SelectedItem is DslrOption shutter) dslrSettings.ShutterSpeed = shutter.Value;
            if (CameraApertureComboBox.SelectedItem is DslrOption aperture) dslrSettings.Aperture = aperture.Value;
            if (CameraWhiteBalanceComboBox.SelectedItem is DslrOption wb) dslrSettings.WhiteBalance = wb.Value;
            if (CameraExposureCompComboBox.SelectedItem is DslrOption ev) dslrSettings.ExposureCompensation = ev.Value;

            var tempSettings = _settingsService.Current.Clone();
            tempSettings.CameraBackend = selectedCam.Backend;
            tempSettings.WebcamDeviceId = selectedCam.DeviceId;
            tempSettings.CameraControl = dslrSettings;

            ICameraService? camera = null;
            bool shouldDispose = false;

            if (_cameraManager != null)
            {
                camera = await _cameraManager.GetCameraAsync(tempSettings);
            }
            else
            {
                camera = tempSettings.CameraBackend switch
                {
                    CameraBackend.Webcam => new WebcamCamera(tempSettings.WebcamDeviceId),
                    CameraBackend.Dslr or CameraBackend.Nikon => new DslrCamera(tempSettings),
                    _ => new SimulatedCamera()
                };
                shouldDispose = true;
                await camera.ConnectAsync();
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var photo = await camera.CapturePhotoAsync(cts.Token);

            if (photo.JpegData is { Length: > 100 })
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(photo.JpegData);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();

                TestPhotoImage.Source = bi;
                TestPhotoStatusText.Text = $"Auflösung: {bi.PixelWidth} × {bi.PixelHeight} px  •  Größe: {photo.JpegData.Length / 1024} KB  •  Aufgenommen: {DateTime.Now:HH:mm:ss}";
                TestPhotoOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                MessageBox.Show(this, "Die Kamera hat keine Bilddaten zurückgegeben.", "Testfoto", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (shouldDispose && camera != null)
            {
                await camera.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Testfoto fehlgeschlagen:\n\n{ex.Message}", "Testfoto-Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TakeTestPhotoButton.IsEnabled = true;
            TakeTestPhotoButton.Content = "📸 Testfoto";
        }
    }

    private void OnCloseTestPhotoClick(object sender, RoutedEventArgs e)
    {
        TestPhotoOverlay.Visibility = Visibility.Collapsed;
        TestPhotoImage.Source = null;
    }
}
