using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

public partial class AdminDashboardDialog : UserControl
{
    private readonly SettingsService _settingsService;
    private readonly EventStorage _storage;
    private readonly SelphyPrintService _printService;
    private readonly TemplateStore _templateStore;
    private TextBox? _currentFocusedTextBox;
    private UiTimer? _hardwareScanTimer;
    private int _isScanningHardware;

    public bool ExitRequested { get; private set; }
    public event EventHandler? Closed;
    public event Action? AboutRequested;

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
        PopulateUsbDrives();

        Closed += (_, _) =>
        {
            _hardwareScanTimer?.Stop();
            _hardwareScanTimer = null;
        };

        _hardwareScanTimer = new UiTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _hardwareScanTimer.Tick += async (_, _) => await ScanHardwareAsync();
        _hardwareScanTimer.Start();
    }

    private async Task ScanHardwareAsync()
    {
        if (Interlocked.CompareExchange(ref _isScanningHardware, 1, 0) != 0)
            return;

        try
        {
            PopulateUsbDrives();
            await PopulateCameraOptionsAsync();
            PopulatePrinterOptions();
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

    private void LoadSettings()
    {
        var current = _settingsService.Current;
        EventNameTextBox.Text = current.EventName;
        OutputFolderTextBox.Text = current.OutputRoot;
        CountdownUpDown.Value = current.CountdownSeconds;
        ReviewUpDown.Value = current.ReviewSeconds;
        EnableVideoMessagesCheckBox.IsChecked = current.EnableVideoMessages;
        VideoDurationUpDown.Value = current.VideoMessageDurationSeconds;
        AutoPrintCheckBox.IsChecked = current.AutoPrint;
        BorderlessPrintCheckBox.IsChecked = current.BorderlessPrint;
        MirrorLiveViewCheckBox.IsChecked = current.MirrorLiveView;
        LiveViewPowerSavingCheckBox.IsChecked = current.LiveViewOnlyDuringCapture;
        AdminPinTextBox.Text = current.AdminPin;
        OnScreenKeyboardCheckBox.IsChecked = current.OnScreenKeyboardEnabled;
    }

    private async Task PopulateCameraOptionsAsync()
    {
        try
        {
            var options = new List<CameraOptionItem>();

            if (CameraManager.CustomCameraDiscovery != null)
            {
                var customCameras = await CameraManager.CustomCameraDiscovery();
                foreach (var cam in customCameras)
                {
                    var backend = cam.Id.StartsWith("ptp:", StringComparison.OrdinalIgnoreCase)
                        ? CameraBackend.Dslr
                        : CameraBackend.Webcam;
                    options.Add(new(cam.Name, backend, cam.Id));
                }
                options.Add(new("📸 Integrierte Testkamera (Simulation)", CameraBackend.Simulated, null));
            }
            else
            {
                options.Add(new("📸 Integrierte Testkamera (Simulation)", CameraBackend.Simulated, null));
                options.Add(new("📷 DSLR / Systemkamera (Nikon, Canon, Sony, Fuji via USB)", CameraBackend.Dslr, null));

                var webcams = await WebcamCamera.FindDevicesAsync();
                foreach (var cam in webcams)
                {
                    options.Add(new($"🎥 {cam.Name}", CameraBackend.Webcam, cam.Id));
                }
            }

            CameraSelectionComboBox.ItemsSource = options;

            var current = _settingsService.Current;
            CameraOptionItem? matching = null;
            if (!string.IsNullOrEmpty(current.WebcamDeviceId))
            {
                matching = options.FirstOrDefault(o => o.DeviceId == current.WebcamDeviceId);
            }
            matching ??= options.FirstOrDefault(o => o.Backend == current.CameraBackend);
            if (matching is null && current.CameraBackend == CameraBackend.Nikon)
            {
                matching = options.FirstOrDefault(o => o.Backend == CameraBackend.Dslr);
            }
            matching ??= options.FirstOrDefault();

            CameraSelectionComboBox.SelectedItem = matching;
            CameraDetectionStatusText.Text = $"{options.Count} Kamera-Option(en) verfügbar";
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

    #region On-Screen Keyboard Handling

    private void OnInputGotFocus(object? sender, global::Avalonia.Input.GotFocusEventArgs e)
    {
        if (sender is TextBox tb)
        {
            _currentFocusedTextBox = tb;
            if (OnScreenKeyboardCheckBox.IsChecked == true)
            {
                OnScreenKeyboardBorder.IsVisible = true;
            }
        }
    }

    private void OnKeyboardToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (OnScreenKeyboardCheckBox.IsChecked == true)
        {
            OnScreenKeyboardBorder.IsVisible = true;
            _currentFocusedTextBox ??= EventNameTextBox;
        }
        else
        {
            OnScreenKeyboardBorder.IsVisible = false;
        }
    }

    private void OnCloseKeyboardClick(object? sender, RoutedEventArgs e)
    {
        OnScreenKeyboardBorder.IsVisible = false;
    }

    private void OnKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key && _currentFocusedTextBox is not null)
        {
            var tb = _currentFocusedTextBox;
            var text = tb.Text ?? string.Empty;
            var caret = tb.CaretIndex;
            if (caret < 0 || caret > text.Length) caret = text.Length;

            tb.Text = text.Insert(caret, key);
            tb.CaretIndex = caret + key.Length;
            tb.Focus();
        }
    }

    private void OnBackspaceClick(object? sender, RoutedEventArgs e)
    {
        if (_currentFocusedTextBox is not null)
        {
            var tb = _currentFocusedTextBox;
            var text = tb.Text ?? string.Empty;
            var caret = tb.CaretIndex;

            if (tb.SelectionStart != tb.SelectionEnd)
            {
                var start = Math.Min(tb.SelectionStart, tb.SelectionEnd);
                var length = Math.Abs(tb.SelectionEnd - tb.SelectionStart);
                tb.Text = text.Remove(start, length);
                tb.CaretIndex = start;
            }
            else if (caret > 0 && caret <= text.Length)
            {
                tb.Text = text.Remove(caret - 1, 1);
                tb.CaretIndex = caret - 1;
            }
            tb.Focus();
        }
    }

    #endregion

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var copy = _settingsService.Current.Clone();
        if (!string.IsNullOrWhiteSpace(EventNameTextBox.Text))
            copy.EventName = EventNameTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(OutputFolderTextBox.Text))
            copy.OutputRoot = OutputFolderTextBox.Text.Trim();

        copy.CountdownSeconds = (int)(CountdownUpDown.Value ?? 3);
        copy.ReviewSeconds = (int)(ReviewUpDown.Value ?? 12);
        copy.EnableVideoMessages = EnableVideoMessagesCheckBox.IsChecked ?? true;
        copy.VideoMessageDurationSeconds = (int)(VideoDurationUpDown.Value ?? 20);
        copy.AutoPrint = AutoPrintCheckBox.IsChecked ?? false;
        copy.BorderlessPrint = BorderlessPrintCheckBox.IsChecked ?? true;
        copy.MirrorLiveView = MirrorLiveViewCheckBox.IsChecked ?? true;
        copy.LiveViewOnlyDuringCapture = LiveViewPowerSavingCheckBox.IsChecked ?? false;
        copy.OnScreenKeyboardEnabled = OnScreenKeyboardCheckBox.IsChecked ?? false;

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

        if (CameraIsoComboBox.SelectedItem is DslrOption iso) copy.CameraControl.Iso = iso.Value;
        if (CameraShutterComboBox.SelectedItem is DslrOption shutter) copy.CameraControl.ShutterSpeed = shutter.Value;
        if (CameraApertureComboBox.SelectedItem is DslrOption aperture) copy.CameraControl.Aperture = aperture.Value;
        if (CameraWhiteBalanceComboBox.SelectedItem is DslrOption wb) copy.CameraControl.WhiteBalance = wb.Value;
        if (CameraExposureCompComboBox.SelectedItem is DslrOption ev) copy.CameraControl.ExposureCompensation = ev.Value;

        _settingsService.Update(copy);
        Closed?.Invoke(this, EventArgs.Empty);
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
        AboutRequested?.Invoke();
    }

    private void OnExitAppClick(object? sender, RoutedEventArgs e)
    {
        ExitRequested = true;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void PopulateUsbDrives()
    {
        var drives = UsbDriveDetector.DetectDrives();
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

    private void OnSelectUsbDriveClick(object? sender, RoutedEventArgs e)
    {
        if (UsbDrivesComboBox.SelectedItem is UsbDriveInfo drive)
        {
            OutputFolderTextBox.Text = drive.RootPath;
            UsbStatusText.Text = $"✅ Speicherort auf USB-Laufwerk '{drive.Name}' gesetzt!";
        }
    }

    private async void OnBrowseOutputFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Speicherordner für Fotos & Videos wählen",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            OutputFolderTextBox.Text = folders[0].Path.LocalPath;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
