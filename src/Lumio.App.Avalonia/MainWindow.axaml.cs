using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lumio.App.Avalonia.Views;
using Lumio.Camera;
using Lumio.Core.Camera;
using Lumio.Core.Session;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Lumio.Imaging;
using Lumio.Printing;
using SkiaSharp;

namespace Lumio.App.Avalonia;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly EventStorage _storage;
    private readonly CameraManager _cameraManager;
    private readonly TemplateStore _templateStore;
    private readonly SelphyPrintService _printService;
    private readonly CollageRenderer _renderer = new();

    private ICameraService? _activeCamera;
    private BoothMode _activeMode = BoothMode.SinglePhoto;
    private BoothTemplate _activeTemplate;

    private byte[]? _currentReviewJpeg;
    private int _reviewRemainingSeconds;
    private DispatcherTimer? _reviewTimer;
    private int _galleryCurrentIndex;
    private List<string> _galleryFiles = [];

    // Resilient Auto-Reconnect
    private readonly object _reconnectLock = new();
    private CancellationTokenSource? _reconnectCts;
    private bool _isReconnecting;
    private DateTime _lastFrameTime = DateTime.MinValue;
    private DispatcherTimer? _watchdogTimer;

    public MainWindow()
    {
        InitializeComponent();

        var timeProvider = TimeProvider.System;
        var settingsStore = new SettingsStore();
        _settingsService = new SettingsService(settingsStore);
        _storage = new EventStorage(_settingsService, timeProvider);
        _cameraManager = new CameraManager();
        _templateStore = new TemplateStore();
        _printService = new SelphyPrintService();

        _activeTemplate = _templateStore.GetById(_settingsService.Current.SelectedTemplateId)
                          ?? BuiltInTemplates.CreateSinglePhotoPostcard();

        Opened += async (s, e) => await InitializeBoothAsync();
        Closing += async (s, e) => await TeardownBoothAsync();
    }

    private async Task InitializeBoothAsync()
    {
        UpdateLiveViewMirror();
        EventTitleText.Text = _settingsService.Current.EventName.ToUpperInvariant();
        UpdateModeButtons();
        await StartCameraAsync();
        StartWatchdog();
        RefreshGalleryCorner();
    }

    private async Task StartCameraAsync(bool isReconnect = false)
    {
        try
        {
            if (_activeCamera is not null)
            {
                _activeCamera.FrameReady -= OnCameraFrameReady;
                _activeCamera.StatusChanged -= OnCameraStatusChanged;
                try
                {
                    await _activeCamera.StopLiveViewAsync();
                }
                catch { }
            }

            CameraStatusText.Text = isReconnect ? "Kamera neu verbinden..." : "Kamera wird initialisiert...";
            _activeCamera = await _cameraManager.GetCameraAsync(_settingsService.Current);
            _activeCamera.FrameReady += OnCameraFrameReady;
            _activeCamera.StatusChanged += OnCameraStatusChanged;

            await _activeCamera.StartLiveViewAsync();
            _lastFrameTime = DateTime.UtcNow;
            CameraStatusText.Text = $"{_activeCamera.DisplayName} bereit";
        }
        catch (Exception ex)
        {
            CameraStatusText.Text = isReconnect ? "Kamera getrennt..." : $"Kamerafehler: {ex.Message}";
        }
    }

    private void StartWatchdog()
    {
        _watchdogTimer?.Stop();
        _watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watchdogTimer.Tick += (s, e) =>
        {
            if (_activeCamera is not null && _activeCamera.Status == CameraStatus.Connected && !_isReconnecting)
            {
                if (_lastFrameTime != DateTime.MinValue && (DateTime.UtcNow - _lastFrameTime) > TimeSpan.FromSeconds(5))
                {
                    TriggerAutoReconnect("Keine Bilder empfangen");
                }
            }
        };
        _watchdogTimer.Start();
    }

    private void TriggerAutoReconnect(string reason)
    {
        lock (_reconnectLock)
        {
            if (_isReconnecting)
                return;

            _isReconnecting = true;
            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();
        }

        var token = _reconnectCts.Token;
        Dispatcher.UIThread.Post(() =>
        {
            CameraStatusText.Text = "Kamera neu verbinden...";
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, token);
                while (!token.IsCancellationRequested)
                {
                    bool reconnected = false;
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        try
                        {
                            await StartCameraAsync(isReconnect: true);
                            if (_activeCamera?.Status == CameraStatus.Connected && _cameraManager.FallbackReason is null)
                            {
                                reconnected = true;
                            }
                        }
                        catch { }
                    });

                    if (reconnected) break;
                    await Task.Delay(2500, token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_reconnectLock) { _isReconnecting = false; }
            }
        }, token);
    }

    private void OnCameraStatusChanged(object? sender, CameraStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (status == CameraStatus.Error || status == CameraStatus.Disconnected)
            {
                CameraStatusText.Text = "Kamera getrennt (Verbinde neu...)";
                TriggerAutoReconnect("Status geändert auf " + status);
            }
            else if (status == CameraStatus.Connected)
            {
                CameraStatusText.Text = $"{_activeCamera?.DisplayName ?? "Kamera"} bereit";
            }
        });
    }

    private void OnCameraFrameReady(object? sender, LiveViewFrame frame)
    {
        _lastFrameTime = DateTime.UtcNow;
        if (_isReconnecting)
        {
            lock (_reconnectLock)
            {
                _isReconnecting = false;
                _reconnectCts?.Cancel();
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (frame.Format == FrameFormat.Bgra32)
                {
                    var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    using var skImage = SKImage.FromPixelCopy(info, frame.Data, frame.Stride);
                    using var data = skImage.Encode(SKEncodedImageFormat.Jpeg, 80);
                    using var ms = new MemoryStream(data.ToArray());
                    LiveViewImageControl.Source = new Bitmap(ms);
                }
                else
                {
                    using var ms = new MemoryStream(frame.Data);
                    LiveViewImageControl.Source = new Bitmap(ms);
                }
            }
            catch { }
        });
    }

    private async void OnStartCaptureClick(object? sender, RoutedEventArgs e)
    {
        if (_activeCamera is null)
            return;

        IdleOverlayGrid.IsVisible = false;
        CornerGalleryBorder.IsVisible = false;
        CountdownOverlayGrid.IsVisible = true;

        try
        {
            if (_activeMode == BoothMode.VideoMessage)
            {
                await RunVideoMessageWorkflowAsync();
            }
            else if (_activeMode == BoothMode.Boomerang)
            {
                await RunBoomerangWorkflowAsync();
            }
            else if (_activeMode == BoothMode.Gif)
            {
                await RunGifWorkflowAsync();
            }
            else
            {
                await RunPhotoWorkflowAsync();
            }
        }
        catch (Exception ex)
        {
            CountdownOverlayGrid.IsVisible = false;
            VideoRecordingOverlayGrid.IsVisible = false;
            IdleOverlayGrid.IsVisible = true;
            CornerGalleryBorder.IsVisible = true;
            CameraStatusText.Text = $"Fehler: {ex.Message}";
        }
    }

    private CancellationTokenSource? _videoRecordingCts;

    private async Task RunVideoMessageWorkflowAsync()
    {
        MultiShotStepText.IsVisible = false;
        SeriesPreviewBorder.IsVisible = false;

        // 1. Countdown: 3... 2... 1...
        for (var r = _settingsService.Current.CountdownSeconds; r > 0; r--)
        {
            CountdownNumberText.Text = r.ToString();
            CountdownStatusText.Text = r == 1 ? "Bereit machen für deine Botschaft!" : "Videobotschaft startet gleich...";
            await Task.Delay(1000);
        }

        // 2. Switch to active video recording overlay
        CountdownOverlayGrid.IsVisible = false;
        VideoRecordingOverlayGrid.IsVisible = true;

        var maxDurationSec = _settingsService.Current.VideoMessageDurationSeconds;
        using var recorder = new VideoRecorder(_activeCamera!);
        _videoRecordingCts = new CancellationTokenSource();

        recorder.StartRecording();
        var startTime = DateTime.UtcNow;

        try
        {
            while (!_videoRecordingCts.IsCancellationRequested)
            {
                var elapsed = DateTime.UtcNow - startTime;
                var totalSeconds = (int)elapsed.TotalSeconds;

                if (totalSeconds >= maxDurationSec)
                    break;

                VideoTimerText.Text = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2} / {maxDurationSec / 60:D2}:{maxDurationSec % 60:D2}";
                RecBlinkDot.Opacity = (elapsed.Milliseconds < 500) ? 1.0 : 0.2;

                await Task.Delay(100);
            }
        }
        catch (OperationCanceledException) { }

        // 3. Processing overlay
        VideoRecordingOverlayGrid.IsVisible = false;
        CountdownOverlayGrid.IsVisible = true;
        CountdownNumberText.Text = "⏳";
        CountdownStatusText.Text = "Videobotschaft wird gespeichert...";

        var result = await recorder.StopRecordingAsync();

        // 4. Move to storage
        var extension = Path.GetExtension(result.VideoPath).TrimStart('.');
        var finalPath = await _storage.MoveVideoAsync(result.VideoPath, DateTimeOffset.Now, extension: extension);

        if (result.ThumbnailJpeg is not null)
        {
            ShowReview(result.ThumbnailJpeg);
        }
        else
        {
            OnDoneReviewClick(null, new RoutedEventArgs());
        }
    }

    private void OnStopRecordingClick(object? sender, RoutedEventArgs e)
    {
        _videoRecordingCts?.Cancel();
    }

    private async Task RunBoomerangWorkflowAsync()
    {
        for (var r = 3; r > 0; r--)
        {
            CountdownNumberText.Text = r.ToString();
            CountdownStatusText.Text = r == 1 ? "Aktion starten!" : "Bereit machen für Boomerang!";
            await Task.Delay(1000);
        }

        CountdownNumberText.Text = "⚡";
        CountdownStatusText.Text = "Bewegung aufnehmen...";
        TriggerFlash();

        var burstFrames = new List<LiveViewFrame>();
        var lockObj = new object();
        var lastCapture = DateTime.MinValue;

        void OnBoomerangFrame(object? s, LiveViewFrame f)
        {
            var now = DateTime.UtcNow;
            lock (lockObj)
            {
                if ((now - lastCapture).TotalMilliseconds >= 75 && burstFrames.Count < 16)
                {
                    lastCapture = now;
                    var copy = f.Data.ToArray();
                    burstFrames.Add(new LiveViewFrame(copy, f.Format, f.Width, f.Height, f.Stride));
                }
            }
        }

        _activeCamera!.FrameReady += OnBoomerangFrame;
        try
        {
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalMilliseconds < 1300 && burstFrames.Count < 16)
            {
                await Task.Delay(25);
            }
        }
        finally
        {
            _activeCamera.FrameReady -= OnBoomerangFrame;
        }

        if (burstFrames.Count < 3)
        {
            var photo = await _activeCamera.CapturePhotoAsync();
            burstFrames.Add(new LiveViewFrame(photo.JpegData, FrameFormat.Jpeg, 1200, 800, 0));
        }

        CountdownNumberText.Text = "⏳";
        CountdownStatusText.Text = "Boomerang wird berechnet...";

        var overlay = $"{_settingsService.Current.EventName} · {DateTime.Now:yyyy}";
        var gifBytes = await Task.Run(() => GifEncoder.EncodeBoomerang(burstFrames, frameDelayMs: 90, overlayText: overlay));
        var path = await _storage.SaveGifAsync(gifBytes, DateTimeOffset.Now, prefix: "BOOM");

        ShowReview(gifBytes);
    }

    private async Task RunGifWorkflowAsync()
    {
        const int gifShotsCount = 4;
        var capturedPhotos = new List<byte[]>();

        for (var shotIndex = 1; shotIndex <= gifShotsCount; shotIndex++)
        {
            MultiShotStepText.IsVisible = true;
            MultiShotStepText.Text = $"Pose {shotIndex} von {gifShotsCount}";

            var countdownSec = shotIndex == 1 ? _settingsService.Current.CountdownSeconds : 2;
            for (var r = countdownSec; r > 0; r--)
            {
                CountdownNumberText.Text = r.ToString();
                CountdownStatusText.Text = r == 1 ? "Pose halten & lächeln!" : $"Pose {shotIndex}!";
                await Task.Delay(1000);
            }

            CountdownNumberText.Text = "📸";
            CountdownStatusText.Text = "Bitte stillhalten!";
            TriggerFlash();

            var photo = await _activeCamera!.CapturePhotoAsync();
            capturedPhotos.Add(photo.JpegData);

            if (shotIndex < gifShotsCount)
            {
                using (var previewStream = new MemoryStream(photo.JpegData))
                {
                    SeriesPreviewImage.Source = new Bitmap(previewStream);
                }

                SeriesPreviewBorder.IsVisible = true;
                CountdownStatusText.Text = "Super! Nächste Pose startet in Kürze...";
                await Task.Delay(1500);
                SeriesPreviewBorder.IsVisible = false;
            }
        }

        CountdownNumberText.Text = "🎬";
        CountdownStatusText.Text = "GIF wird generiert...";

        var overlay = $"{_settingsService.Current.EventName} · {DateTime.Now:yyyy}";
        var gifBytes = await Task.Run(() => GifEncoder.EncodeFromJpegs(capturedPhotos, frameDelayMs: 250, overlayText: overlay));
        var path = await _storage.SaveGifAsync(gifBytes, DateTimeOffset.Now, prefix: "GIF");

        ShowReview(capturedPhotos[0]);
    }

    private async Task RunPhotoWorkflowAsync()
    {
        var totalShots = _activeTemplate.RequiredPhotosCount;
        var capturedPhotos = new List<byte[]>();

        for (var shotIndex = 1; shotIndex <= totalShots; shotIndex++)
        {
            if (totalShots > 1)
            {
                MultiShotStepText.IsVisible = true;
                MultiShotStepText.Text = $"Foto {shotIndex} von {totalShots}";
            }

            var countdownSec = shotIndex == 1 ? _settingsService.Current.CountdownSeconds : _settingsService.Current.MultiShotPauseSeconds;
            for (var r = countdownSec; r > 0; r--)
            {
                CountdownNumberText.Text = r.ToString();
                CountdownStatusText.Text = r == 1 ? "Lächeln!" : $"Foto {shotIndex} von {totalShots}";
                await Task.Delay(1000);
            }

            CountdownNumberText.Text = "📸";
            CountdownStatusText.Text = "Bitte stillhalten!";
            TriggerFlash();

            var photo = await _activeCamera!.CapturePhotoAsync();
            capturedPhotos.Add(photo.JpegData);
            await _storage.SaveOriginalAsync(photo);

            if (shotIndex < totalShots)
            {
                using (var previewStream = new MemoryStream(photo.JpegData))
                {
                    SeriesPreviewImage.Source = new Bitmap(previewStream);
                }

                SeriesPreviewBorder.IsVisible = true;
                CountdownStatusText.Text = "Nächstes Foto startet in Kürze...";
                await Task.Delay(Math.Max(1000, _settingsService.Current.MultiShotPauseSeconds * 1000));
                SeriesPreviewBorder.IsVisible = false;
            }
        }

        CountdownNumberText.Text = "🎨";
        CountdownStatusText.Text = "Foto wird aufbereitet...";

        var settings = _settingsService.Current;
        var formattedEvent = PhotoBrandingHelper.FormatEventText(settings.EventName, settings.EventTextFormat);
        var metadata = new Dictionary<string, string>
        {
            ["EventName"] = formattedEvent,
            ["Date"] = settings.EventTextFormat == "EventAndDate" ? DateTime.Now.ToString("dd.MM.yyyy") : string.Empty,
            ["Year"] = DateTime.Now.Year.ToString(),
        };

        var finalJpeg = await Task.Run(() => _renderer.Render(
            _activeTemplate,
            capturedPhotos,
            metadata,
            jpegQuality: 95,
            embedLogo: settings.EmbedLogoOnPhotos,
            logoPosition: settings.LogoPosition,
            embedEventText: settings.EmbedEventTextOnPhotos));
        await _storage.SaveCollageAsync(finalJpeg, DateTimeOffset.Now);

        ShowReview(finalJpeg);
    }

    private void TriggerFlash()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            FlashBorder.Opacity = 1;
            await Task.Delay(100);
            FlashBorder.Opacity = 0;
        });
    }

    private void ShowReview(byte[] displayJpeg)
    {
        CountdownOverlayGrid.IsVisible = false;
        MultiShotStepText.IsVisible = false;
        SeriesPreviewBorder.IsVisible = false;

        _currentReviewJpeg = displayJpeg;
        try
        {
            using var ms = new MemoryStream(displayJpeg);
            ReviewImageControl.Source = new Bitmap(ms);
        }
        catch { }

        ReviewGrid.IsVisible = true;
        PrintButton.IsVisible = _activeMode != BoothMode.Gif && _activeMode != BoothMode.Boomerang && _activeMode != BoothMode.VideoMessage;
        StartReviewCountdown();
        RefreshGalleryCorner();

        if (_settingsService.Current.AutoPrint && _activeMode != BoothMode.Gif && _activeMode != BoothMode.Boomerang && _activeMode != BoothMode.VideoMessage)
        {
            _ = PrintCurrentPhotoAsync();
        }
    }

    private void StartReviewCountdown()
    {
        _reviewRemainingSeconds = Math.Max(5, _settingsService.Current.ReviewSeconds);
        ReviewTimerText.Text = $"Zurück in {_reviewRemainingSeconds}s";

        _reviewTimer?.Stop();
        _reviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reviewTimer.Tick += (s, e) =>
        {
            _reviewRemainingSeconds--;
            ReviewTimerText.Text = $"Zurück in {_reviewRemainingSeconds}s";
            if (_reviewRemainingSeconds <= 0)
            {
                _reviewTimer.Stop();
                OnDoneReviewClick(null, new RoutedEventArgs());
            }
        };
        _reviewTimer.Start();
    }

    private void OnDoneReviewClick(object? sender, RoutedEventArgs e)
    {
        _reviewTimer?.Stop();
        ReviewGrid.IsVisible = false;
        VideoRecordingOverlayGrid.IsVisible = false;
        CountdownOverlayGrid.IsVisible = false;
        IdleOverlayGrid.IsVisible = true;
        CornerGalleryBorder.IsVisible = true;
    }

    private async void OnPrintPhotoClick(object? sender, RoutedEventArgs e)
    {
        await PrintCurrentPhotoAsync();
    }

    private async Task PrintCurrentPhotoAsync()
    {
        if (_currentReviewJpeg is null)
            return;

        PrintButtonLabelText.Text = "Druckt...";
        PrintButton.IsEnabled = false;

        try
        {
            await _printService.PrintPhotoAsync(_currentReviewJpeg, _settingsService.Current.SelectedPrinter);
            PrintStatusFeedbackText.Text = "Druckauftrag an Selphy gesendet!";
            PrintStatusFeedbackText.IsVisible = true;
        }
        catch (Exception ex)
        {
            PrintStatusFeedbackText.Text = $"Druckfehler: {ex.Message}";
            PrintStatusFeedbackText.IsVisible = true;
        }
        finally
        {
            PrintButtonLabelText.Text = "Drucken";
            PrintButton.IsEnabled = true;
        }
    }

    // ==================== MODES ====================
    private void OnSelectModeSingleClick(object? sender, RoutedEventArgs e) => SetMode(BoothMode.SinglePhoto);
    private void OnSelectModeQuadClick(object? sender, RoutedEventArgs e) => SetMode(BoothMode.CollageQuad);
    private void OnSelectModeStripClick(object? sender, RoutedEventArgs e) => SetMode(BoothMode.CollageStrip);
    private void OnSelectModeGifClick(object? sender, RoutedEventArgs e) => SetMode(BoothMode.Gif);
    private void OnSelectModeBoomerangClick(object? sender, RoutedEventArgs e) => SetMode(BoothMode.Boomerang);
    private void OnSelectModeVideoClick(object? sender, RoutedEventArgs e) => SetMode(BoothMode.VideoMessage);

    private void SetMode(BoothMode mode)
    {
        _activeMode = mode;
        _activeTemplate = mode switch
        {
            BoothMode.CollageQuad => BuiltInTemplates.CreateQuadCollage(),
            BoothMode.CollageStrip => BuiltInTemplates.CreateDoubleStripForSelphy(),
            _ => BuiltInTemplates.CreateSinglePhotoPostcard(),
        };
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        ModeVideoButton.IsVisible = _settingsService.Current.EnableVideoMessages;
        StartButtonLabel.Text = _activeMode switch
        {
            BoothMode.CollageQuad => "COLLAGE STARTEN",
            BoothMode.CollageStrip => "STREIFEN STARTEN",
            BoothMode.Gif => "GIF STARTEN",
            BoothMode.Boomerang => "BOOMERANG STARTEN",
            BoothMode.VideoMessage => "BOTSCHAFT STARTEN",
            _ => "FOTO STARTEN"
        };
        StartButtonIcon.Text = _activeMode switch
        {
            BoothMode.CollageQuad => "🖼️",
            BoothMode.CollageStrip => "🎞️",
            BoothMode.Gif => "🎬",
            BoothMode.Boomerang => "⚡",
            BoothMode.VideoMessage => "📹",
            _ => "📸"
        };
    }

    // ==================== GALLERY ====================
    private void RefreshGalleryCorner()
    {
        try
        {
            var files = _storage.GetEventPhotos();
            if (files.Count > 0)
            {
                var latest = files[0];
                if (latest.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                {
                    using var ms = new MemoryStream(File.ReadAllBytes(latest));
                    CornerThumbnailImage.Source = new Bitmap(ms);
                }
                GalleryCountText.Text = $"{files.Count} Foto{(files.Count == 1 ? "" : "s")}";
                CornerGalleryBorder.IsVisible = true;
            }
        }
        catch { }
    }

    private void OnCornerGalleryClick(object? sender, PointerReleasedEventArgs e)
    {
        _galleryFiles = _storage.GetEventPhotos().ToList();
        if (_galleryFiles.Count == 0)
            return;

        _galleryCurrentIndex = 0;
        DisplayGalleryPhoto();
        GalleryOverlayGrid.IsVisible = true;
    }

    private void DisplayGalleryPhoto()
    {
        if (_galleryFiles.Count == 0 || _galleryCurrentIndex < 0 || _galleryCurrentIndex >= _galleryFiles.Count)
            return;

        var file = _galleryFiles[_galleryCurrentIndex];
        GalleryIndexTitleText.Text = $"Foto {_galleryCurrentIndex + 1} von {_galleryFiles.Count}";
        GalleryPhotoInfoText.Text = Path.GetFileName(file);
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(file));
            GalleryMainImage.Source = new Bitmap(ms);
        }
        catch { }
    }

    private void OnGalleryPrevClick(object? sender, RoutedEventArgs e)
    {
        if (_galleryFiles.Count == 0) return;
        _galleryCurrentIndex = (_galleryCurrentIndex - 1 + _galleryFiles.Count) % _galleryFiles.Count;
        DisplayGalleryPhoto();
    }

    private void OnGalleryNextClick(object? sender, RoutedEventArgs e)
    {
        if (_galleryFiles.Count == 0) return;
        _galleryCurrentIndex = (_galleryCurrentIndex + 1) % _galleryFiles.Count;
        DisplayGalleryPhoto();
    }

    private async void OnGalleryPrintClick(object? sender, RoutedEventArgs e)
    {
        if (_galleryFiles.Count == 0) return;
        var file = _galleryFiles[_galleryCurrentIndex];
        if (file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            GalleryPrintStatusText.Text = "ℹ️ Animierte GIFs können nicht gedruckt werden.";
            GalleryPrintStatusText.IsVisible = true;
            return;
        }

        GalleryPrintButtonLabel.Text = "Druckt...";
        GalleryPrintButton.IsEnabled = false;
        try
        {
            var bytes = await File.ReadAllBytesAsync(file);
            await _printService.PrintPhotoAsync(bytes, _settingsService.Current.SelectedPrinter);
            GalleryPrintStatusText.Text = "Druckauftrag gesendet!";
            GalleryPrintStatusText.IsVisible = true;
        }
        catch (Exception ex)
        {
            GalleryPrintStatusText.Text = $"Fehler: {ex.Message}";
            GalleryPrintStatusText.IsVisible = true;
        }
        finally
        {
            GalleryPrintButtonLabel.Text = "Foto drucken";
            GalleryPrintButton.IsEnabled = true;
        }
    }

    private void OnCloseGalleryClick(object? sender, RoutedEventArgs e)
    {
        GalleryOverlayGrid.IsVisible = false;
    }

    // ==================== ADMIN & ABOUT ====================
    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new AboutDialog();
        about.ShowDialog(this);
    }

    private async void OnAdminLockClick(object? sender, RoutedEventArgs e)
    {
        await OpenAdminMenuAsync();
    }

    private async Task OpenAdminMenuAsync()
    {
        var pinDialog = new AdminPinDialog(_settingsService.Current.AdminPin);
        await pinDialog.ShowDialog(this);

        if (pinDialog.IsAuthenticated)
        {
            var dashboard = new AdminDashboardDialog(_settingsService, _storage, _printService, _templateStore);
            await dashboard.ShowDialog(this);

            if (dashboard.ExitRequested)
            {
                Close();
            }
            else
            {
                UpdateLiveViewMirror();
                EventTitleText.Text = _settingsService.Current.EventName.ToUpperInvariant();
                ModeVideoButton.IsVisible = _settingsService.Current.EnableVideoMessages;
                if (!_settingsService.Current.EnableVideoMessages && _activeMode == BoothMode.VideoMessage)
                {
                    SetMode(BoothMode.SinglePhoto);
                }
                _activeTemplate = _templateStore.GetById(_settingsService.Current.SelectedTemplateId)
                                  ?? BuiltInTemplates.CreateSinglePhotoPostcard();
                await StartCameraAsync();
            }
        }
    }

    private void UpdateLiveViewMirror()
    {
        LiveViewImageControl.RenderTransform = new ScaleTransform(_settingsService.Current.MirrorLiveView ? -1 : 1, 1);
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.A && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control))
        {
            await OpenAdminMenuAsync();
            e.Handled = true;
        }
        else if (GalleryOverlayGrid.IsVisible)
        {
            if (e.Key == Key.Left) OnGalleryPrevClick(null, new RoutedEventArgs());
            else if (e.Key == Key.Right) OnGalleryNextClick(null, new RoutedEventArgs());
        }
    }

    private async Task TeardownBoothAsync()
    {
        _watchdogTimer?.Stop();
        _reconnectCts?.Cancel();

        if (_activeCamera is not null)
        {
            _activeCamera.FrameReady -= OnCameraFrameReady;
            _activeCamera.StatusChanged -= OnCameraStatusChanged;
            await _activeCamera.DisposeAsync();
        }
    }
}
