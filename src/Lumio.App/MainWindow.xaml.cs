using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumio.App.Services;
using Lumio.App.Views;
using Lumio.Camera;
using Lumio.Core.Camera;
using Lumio.Core.Session;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Lumio.Imaging;
using Lumio.Printing;
using SkiaSharp;

namespace Lumio.App;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly EventStorage _storage;
    private readonly CameraManager _cameraManager;
    private readonly PhotoCaptureWorkflow _workflow;
    private readonly TemplateStore _templateStore;
    private readonly SelphyPrintService _printService;

    private ICameraService? _activeCamera;
    private WriteableBitmap? _liveBitmap;
    private DispatcherTimer? _reviewTimer;
    private int _reviewSecondsLeft;
    private bool _isCapturing;
    private byte[]? _lastResultJpeg;
    private string? _lastResultFilePath;
    private BoothMode _activeMode = BoothMode.SinglePhoto;
    private BoothTemplate _activeTemplate;

    // Dedicated Native GIF Players
    private readonly GifPlayer _reviewGifPlayer;
    private readonly GifPlayer _galleryGifPlayer;
    private readonly GifPlayer _cornerGifPlayer;

    // Gallery state
    private List<string> _galleryPhotos = [];
    private int _galleryCurrentIndex;

    // Resilient Auto-Reconnect & Watchdog
    private DateTime _lastFrameReceivedTime = DateTime.MinValue;
    private DispatcherTimer? _frameWatchdogTimer;
    private readonly object _reconnectLock = new();
    private CancellationTokenSource? _reconnectCts;
    private bool _isReconnecting;

    public MainWindow(
        SettingsService settingsService,
        EventStorage storage,
        CameraManager cameraManager,
        PhotoCaptureWorkflow workflow,
        TemplateStore? templateStore = null,
        SelphyPrintService? printService = null)
    {
        InitializeComponent();

        _reviewGifPlayer = new GifPlayer(ReviewImageControl);
        _galleryGifPlayer = new GifPlayer(GalleryMainImage);
        _cornerGifPlayer = new GifPlayer(CornerThumbnailImage);

        _settingsService = settingsService;
        _storage = storage;
        _cameraManager = cameraManager;
        _workflow = workflow;
        _templateStore = templateStore ?? new TemplateStore();
        _printService = printService ?? new SelphyPrintService();

        _activeTemplate = _templateStore.GetById(_settingsService.Current.SelectedTemplateId)
                          ?? BuiltInTemplates.CreateSinglePhotoPostcard();

        _settingsService.Changed += OnSettingsChanged;
        ApplySettings(_settingsService.Current);
        UpdateModeSelectorButtons();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCornerGalleryThumbnail();
        await InitializeCameraAsync();
        StartFrameWatchdog();

        // Warm up printer spooler RPC and storage/USB cache in background without blocking UI
        _ = Task.Run(() =>
        {
            try { SelphyPrintService.GetInstalledPrinters(); } catch { }
            try { UsbDriveDetector.DetectDrives(); } catch { }
        });
    }

    private async Task InitializeCameraAsync(bool isReconnect = false)
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

            CameraStatusIndicator.Fill = (Brush)FindResource("WarningBrush");
            CameraStatusText.Text = isReconnect ? "Kamera neu verbinden..." : "Kamera verbindet...";

            _activeCamera = await _cameraManager.GetCameraAsync(_settingsService.Current);
            _activeCamera.FrameReady += OnCameraFrameReady;
            _activeCamera.StatusChanged += OnCameraStatusChanged;

            await _activeCamera.StartLiveViewAsync();

            _lastFrameReceivedTime = DateTime.UtcNow;

            if (_cameraManager.FallbackReason is not null)
            {
                CameraStatusIndicator.Fill = (Brush)FindResource("AccentOrangeBrush");
                CameraStatusText.Text = "Kamera-Hinweis (Klick)";
                CameraStatusText.ToolTip = _cameraManager.FallbackReason;
            }
            else
            {
                CameraStatusIndicator.Fill = (Brush)FindResource("SuccessBrush");
                CameraStatusText.Text = _activeCamera.DisplayName;
                CameraStatusText.ToolTip = _activeCamera.DisplayName;
            }
        }
        catch (Exception ex)
        {
            CameraStatusIndicator.Fill = (Brush)FindResource("DangerBrush");
            CameraStatusText.Text = "Kamera getrennt";

            if (!isReconnect)
            {
                MessageBox.Show(this, $"Kamera-Initialisierung fehlgeschlagen:\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void StartFrameWatchdog()
    {
        _frameWatchdogTimer?.Stop();
        _frameWatchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _frameWatchdogTimer.Tick += (s, e) =>
        {
            if (_isCapturing || ReviewGrid.Visibility == Visibility.Visible || GalleryOverlayGrid.Visibility == Visibility.Visible)
                return;

            if (_activeCamera is not null && _activeCamera is not SimulatedCamera && _activeCamera.Status == CameraStatus.Connected && !_isReconnecting)
            {
                if (_lastFrameReceivedTime != DateTime.MinValue && (DateTime.UtcNow - _lastFrameReceivedTime) > TimeSpan.FromSeconds(12))
                {
                    TriggerAutoReconnect("Keine Kamerabilder empfangen");
                }
            }
        };
        _frameWatchdogTimer.Start();
    }

    private void TriggerAutoReconnect(string reason)
    {
        lock (_reconnectLock)
        {
            if (_isCapturing || _isReconnecting)
                return;

            _isReconnecting = true;
            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();
        }

        var token = _reconnectCts.Token;

        Dispatcher.InvokeAsync(() =>
        {
            CameraStatusIndicator.Fill = (Brush)FindResource("AccentOrangeBrush");
            CameraStatusText.Text = "Kamera neu verbinden...";
            CameraStatusText.ToolTip = $"Verbindung wird wiederhergestellt: {reason}";
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, token);

                // Try reconnecting up to 2 times, never infinite loop
                for (int attempt = 0; attempt < 2 && !token.IsCancellationRequested; attempt++)
                {
                    bool reconnected = false;
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            await InitializeCameraAsync(isReconnect: true);
                            if (_activeCamera?.Status == CameraStatus.Connected && _cameraManager.FallbackReason is null)
                            {
                                reconnected = true;
                            }
                        }
                        catch
                        {
                            // Ignore during background auto-reconnect
                        }
                    });

                    if (reconnected)
                    {
                        break;
                    }

                    await Task.Delay(3000, token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_reconnectLock)
                {
                    _isReconnecting = false;
                }
            }
        }, token);
    }

    private void OnCameraFrameReady(object? sender, LiveViewFrame frame)
    {
        _lastFrameReceivedTime = DateTime.UtcNow;

        if (_isReconnecting)
        {
            lock (_reconnectLock)
            {
                _isReconnecting = false;
                _reconnectCts?.Cancel();
            }
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (_isCapturing && ReviewGrid.Visibility == Visibility.Visible)
                return;

            if (GalleryOverlayGrid.Visibility == Visibility.Visible)
                return;

            if (frame.Format == FrameFormat.Jpeg)
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(frame.Data);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                LiveViewImageControl.Source = bi;
                return;
            }

            if (_liveBitmap is null || _liveBitmap.PixelWidth != frame.Width || _liveBitmap.PixelHeight != frame.Height)
            {
                _liveBitmap = new WriteableBitmap(
                    frame.Width,
                    frame.Height,
                    96,
                    96,
                    PixelFormats.Bgr32,
                    null);
                LiveViewImageControl.Source = _liveBitmap;
            }

            var rect = new Int32Rect(0, 0, frame.Width, frame.Height);
            _liveBitmap.WritePixels(rect, frame.Data, frame.Stride, 0);
        }, DispatcherPriority.Render);
    }

    private void OnCameraStatusChanged(object? sender, CameraStatus status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            switch (status)
            {
                case CameraStatus.Connected:
                    CameraStatusIndicator.Fill = (Brush)FindResource("SuccessBrush");
                    CameraStatusText.Text = _activeCamera?.DisplayName ?? "Verbunden";
                    break;
                case CameraStatus.Connecting:
                    CameraStatusIndicator.Fill = (Brush)FindResource("WarningBrush");
                    CameraStatusText.Text = "Verbinde...";
                    break;
                case CameraStatus.Error:
                case CameraStatus.Disconnected:
                default:
                    CameraStatusIndicator.Fill = (Brush)FindResource("AccentOrangeBrush");
                    CameraStatusText.Text = "Kamera getrennt (Verbinde neu...)";
                    TriggerAutoReconnect("Kamera meldete Status " + status);
                    break;
            }
        });
    }

    private void ApplySettings(BoothSettings settings)
    {
        EventTitleText.Text = settings.EventName.ToUpperInvariant();
        LiveViewScaleTransform.ScaleX = settings.MirrorLiveView ? -1 : 1;
        ModeVideoButton.Visibility = settings.EnableVideoMessages ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.EnableVideoMessages && _activeMode == BoothMode.VideoMessage)
        {
            _activeMode = BoothMode.SinglePhoto;
            UpdateModeSelectorButtons();
        }

        if (!string.Equals(_activeTemplate.Id, settings.SelectedTemplateId, StringComparison.OrdinalIgnoreCase))
        {
            _activeTemplate = _templateStore.GetById(settings.SelectedTemplateId) ?? BuiltInTemplates.CreateSinglePhotoPostcard();
            UpdateModeSelectorButtons();
        }

        UpdateCornerGalleryThumbnail();
    }

    private void OnSettingsChanged(object? sender, BoothSettings newSettings)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            ApplySettings(newSettings);
            await InitializeCameraAsync();
        });
    }

    private void OnSelectModeSingleClick(object sender, RoutedEventArgs e)
    {
        _activeMode = BoothMode.SinglePhoto;
        SetTemplate("single-10x15");
    }

    private void OnSelectModeQuadClick(object sender, RoutedEventArgs e)
    {
        _activeMode = BoothMode.CollageQuad;
        SetTemplate("quad-10x15");
    }

    private void OnSelectModeStripClick(object sender, RoutedEventArgs e)
    {
        _activeMode = BoothMode.CollageStrip;
        SetTemplate("double-strip-10x15");
    }

    private void OnSelectModeGifClick(object sender, RoutedEventArgs e)
    {
        _activeMode = BoothMode.Gif;
        UpdateModeSelectorButtons();
    }

    private void OnSelectModeBoomerangClick(object sender, RoutedEventArgs e)
    {
        _activeMode = BoothMode.Boomerang;
        UpdateModeSelectorButtons();
    }

    private void OnSelectModeVideoClick(object sender, RoutedEventArgs e)
    {
        _activeMode = BoothMode.VideoMessage;
        UpdateModeSelectorButtons();
    }

    private void SetTemplate(string templateId)
    {
        _activeTemplate = _templateStore.GetById(templateId) ?? BuiltInTemplates.CreateSinglePhotoPostcard();
        UpdateModeSelectorButtons();
    }

    private void UpdateModeSelectorButtons()
    {
        var accentBrush = (Brush)FindResource("AccentBrush");
        var glassBorderBrush = (Brush)FindResource("GlassBorderBrush");

        ModeSingleButton.BorderBrush = _activeMode == BoothMode.SinglePhoto ? accentBrush : glassBorderBrush;
        ModeQuadButton.BorderBrush = _activeMode == BoothMode.CollageQuad ? accentBrush : glassBorderBrush;
        ModeStripButton.BorderBrush = _activeMode == BoothMode.CollageStrip ? accentBrush : glassBorderBrush;
        ModeGifButton.BorderBrush = _activeMode == BoothMode.Gif ? accentBrush : glassBorderBrush;
        ModeBoomerangButton.BorderBrush = _activeMode == BoothMode.Boomerang ? accentBrush : glassBorderBrush;
        ModeVideoButton.BorderBrush = _activeMode == BoothMode.VideoMessage ? accentBrush : glassBorderBrush;

        switch (_activeMode)
        {
            case BoothMode.SinglePhoto:
                StartButtonIcon.Text = "📸";
                StartButtonLabel.Text = "FOTO STARTEN";
                StartSubtitleText.Text = "Tippe auf den Button, um den Countdown zu starten";
                break;
            case BoothMode.CollageQuad:
                StartButtonIcon.Text = "🖼️";
                StartButtonLabel.Text = "4ER COLLAGE STARTEN";
                StartSubtitleText.Text = "Aufnahme startet 4 Fotos mit Countdown für den Ausdruck";
                break;
            case BoothMode.CollageStrip:
                StartButtonIcon.Text = "🎞️";
                StartButtonLabel.Text = "DOPPELSTREIFEN STARTEN";
                StartSubtitleText.Text = "Aufnahme startet 3 Fotos für den Selphy Doppelstreifen";
                break;
            case BoothMode.Gif:
                StartButtonIcon.Text = "🎬";
                StartButtonLabel.Text = "GIF STARTEN";
                StartSubtitleText.Text = "4 Posen mit Countdown werden zu einem animierten Mini-Clip";
                break;
            case BoothMode.Boomerang:
                StartButtonIcon.Text = "⚡";
                StartButtonLabel.Text = "BOOMERANG STARTEN";
                StartSubtitleText.Text = "Kurze Action-Bewegung vor & zurück in Dauerschleife";
                break;
            case BoothMode.VideoMessage:
                StartButtonIcon.Text = "📹";
                StartButtonLabel.Text = "BOTSCHAFT STARTEN";
                StartSubtitleText.Text = "Nimm eine persönliche Videobotschaft fürs Gästebuch auf";
                break;
        }
    }

    private async void OnStartCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_isCapturing || _activeCamera is null)
            return;

        _isCapturing = true;
        IdleOverlayGrid.Visibility = Visibility.Collapsed;
        ReviewGrid.Visibility = Visibility.Collapsed;
        CountdownOverlayGrid.Visibility = Visibility.Visible;
        CornerGalleryBorder.Visibility = Visibility.Collapsed;
        SeriesPreviewBorder.Visibility = Visibility.Collapsed;
        CountdownNumberBorder.Visibility = Visibility.Visible;
        PrintStatusFeedbackText.Visibility = Visibility.Collapsed;

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
            MessageBox.Show(this, $"Aufnahme fehlgeschlagen:\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            ReturnToIdle();
        }
        finally
        {
            _isCapturing = false;
        }
    }

    private CancellationTokenSource? _videoRecordingCts;

    private async Task RunVideoMessageWorkflowAsync()
    {
        MultiShotStepText.Visibility = Visibility.Collapsed;

        // 1. Countdown: 3... 2... 1...
        for (var r = _settingsService.Current.CountdownSeconds; r > 0; r--)
        {
            CountdownNumberText.Text = r.ToString();
            CountdownStatusText.Text = r == 1 ? "Bereit machen für deine Botschaft!" : "Videobotschaft startet gleich...";
            await Task.Delay(1000);
        }

        // 2. Hide countdown overlay and show active video recording overlay
        CountdownOverlayGrid.Visibility = Visibility.Collapsed;
        VideoRecordingOverlayGrid.Visibility = Visibility.Visible;

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
        catch (OperationCanceledException)
        {
        }

        // 3. Processing overlay
        VideoRecordingOverlayGrid.Visibility = Visibility.Collapsed;
        CountdownOverlayGrid.Visibility = Visibility.Visible;
        CountdownNumberText.Text = "⏳";
        CountdownStatusText.Text = "Videobotschaft wird gespeichert...";

        var result = await recorder.StopRecordingAsync();

        // 4. Move to event storage
        var extension = Path.GetExtension(result.VideoPath).TrimStart('.');
        var finalPath = await _storage.MoveVideoAsync(result.VideoPath, DateTimeOffset.Now, extension: extension);

        _lastResultFilePath = finalPath;
        _lastResultJpeg = result.ThumbnailJpeg;

        ShowReview(finalPath, result.ThumbnailJpeg, isGif: false, isBoomerang: false, isVideo: true);
    }

    private void OnStopRecordingClick(object sender, RoutedEventArgs e)
    {
        _videoRecordingCts?.Cancel();
    }

    private async Task RunBoomerangWorkflowAsync()
    {
        MultiShotStepText.Visibility = Visibility.Collapsed;

        // 1. Countdown: 3... 2... 1...
        for (var r = _settingsService.Current.CountdownSeconds; r > 0; r--)
        {
            CountdownNumberText.Text = r.ToString();
            CountdownStatusText.Text = r == 1 ? "Bereit machen!" : "Gleich geht's los...";
            await Task.Delay(1000);
        }

        // 2. Action Burst Capture
        CountdownNumberText.Text = "⚡";
        CountdownStatusText.Text = "AUFNAHME LÄUFT! JETZT BEWEGEN!";
        TriggerFlash();

        var burstFrames = new List<LiveViewFrame>();
        var lockObj = new object();
        var lastCapture = DateTime.MinValue;

        void OnBoomerangFrame(object? sender, LiveViewFrame f)
        {
            var now = DateTime.UtcNow;
            lock (lockObj)
            {
                // Capture ~12-16 frames spaced roughly 75ms apart
                if ((now - lastCapture).TotalMilliseconds >= 75 && burstFrames.Count < 16)
                {
                    lastCapture = now;
                    var copyData = f.Data.ToArray();
                    burstFrames.Add(new LiveViewFrame(copyData, f.Format, f.Width, f.Height, f.Stride));
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

        // Resilient fallback if stream was empty
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

        _lastResultFilePath = path;
        _lastResultJpeg = null;
        ShowReview(path, null, isGif: true, isBoomerang: true);
    }

    private async Task RunGifWorkflowAsync()
    {
        const int gifShotsCount = 4;
        var capturedPhotos = new List<byte[]>();

        for (var shotIndex = 1; shotIndex <= gifShotsCount; shotIndex++)
        {
            MultiShotStepText.Visibility = Visibility.Visible;
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
                    var previewBmp = new BitmapImage();
                    previewBmp.BeginInit();
                    previewBmp.CacheOption = BitmapCacheOption.OnLoad;
                    previewBmp.StreamSource = previewStream;
                    previewBmp.EndInit();
                    previewBmp.Freeze();
                    SeriesPreviewImage.Source = previewBmp;
                }

                SeriesPreviewBorder.Visibility = Visibility.Visible;
                CountdownNumberBorder.Visibility = Visibility.Collapsed;
                CountdownStatusText.Text = $"Super! Nächste Pose startet in 2s...";
                await Task.Delay(1500);

                SeriesPreviewBorder.Visibility = Visibility.Collapsed;
                CountdownNumberBorder.Visibility = Visibility.Visible;
            }
        }

        CountdownNumberText.Text = "🎬";
        CountdownStatusText.Text = "GIF wird generiert...";

        var overlay = $"{_settingsService.Current.EventName} · {DateTime.Now:yyyy}";
        var gifBytes = await Task.Run(() => GifEncoder.EncodeFromJpegs(capturedPhotos, frameDelayMs: 250, overlayText: overlay));
        var path = await _storage.SaveGifAsync(gifBytes, DateTimeOffset.Now, prefix: "GIF");

        _lastResultFilePath = path;
        _lastResultJpeg = capturedPhotos[0];
        ShowReview(path, capturedPhotos[0], isGif: true, isBoomerang: false);
    }

    private async Task RunPhotoWorkflowAsync()
    {
        var totalShots = _activeTemplate.RequiredPhotosCount;
        var capturedPhotos = new List<byte[]>();
        string? finalPath = null;

        for (var shotIndex = 1; shotIndex <= totalShots; shotIndex++)
        {
            if (totalShots > 1)
            {
                MultiShotStepText.Visibility = Visibility.Visible;
                MultiShotStepText.Text = $"Foto {shotIndex} von {totalShots}";
            }
            else
            {
                MultiShotStepText.Visibility = Visibility.Collapsed;
            }

            var progress = new Progress<CaptureProgress>(p =>
            {
                switch (p.Stage)
                {
                    case CaptureStage.Countdown:
                        CountdownNumberText.Text = p.SecondsRemaining.ToString();
                        CountdownStatusText.Text = p.SecondsRemaining == 1 ? "Lächeln!" : "Bereit machen...";
                        break;
                    case CaptureStage.Capturing:
                        CountdownNumberText.Text = "📸";
                        CountdownStatusText.Text = "Bitte stillhalten!";
                        TriggerFlash();
                        break;
                    case CaptureStage.Saving:
                        CountdownNumberText.Text = "⏳";
                        CountdownStatusText.Text = "Foto wird verarbeitet...";
                        break;
                }
            });

            var captureResult = await _workflow.RunAsync(_activeCamera!, _settingsService.Current.CountdownSeconds, progress);
            capturedPhotos.Add(captureResult.Photo.JpegData);
            finalPath = captureResult.FilePath;

            // Between-shots preview and configurable pause
            if (totalShots > 1 && shotIndex < totalShots)
            {
                using (var previewStream = new MemoryStream(captureResult.Photo.JpegData))
                {
                    var previewBmp = new BitmapImage();
                    previewBmp.BeginInit();
                    previewBmp.CacheOption = BitmapCacheOption.OnLoad;
                    previewBmp.StreamSource = previewStream;
                    previewBmp.EndInit();
                    previewBmp.Freeze();
                    SeriesPreviewImage.Source = previewBmp;
                }

                SeriesPreviewBorder.Visibility = Visibility.Visible;
                CountdownNumberBorder.Visibility = Visibility.Collapsed;

                var pauseSeconds = _settingsService.Current.MultiShotPauseSeconds;
                for (var p = pauseSeconds; p > 0; p--)
                {
                    CountdownStatusText.Text = $"Klasse! Foto {shotIndex + 1} startet in {p}s...";
                    await Task.Delay(1000);
                }

                SeriesPreviewBorder.Visibility = Visibility.Collapsed;
                CountdownNumberBorder.Visibility = Visibility.Visible;
            }
        }

        // Render collage if multi-shot
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

        var renderer = new CollageRenderer();
        _lastResultJpeg = await Task.Run(() => renderer.Render(
            _activeTemplate,
            capturedPhotos,
            metadata,
            jpegQuality: 95,
            embedLogo: settings.EmbedLogoOnPhotos,
            logoPosition: settings.LogoPosition,
            embedEventText: settings.EmbedEventTextOnPhotos));
        finalPath = await _storage.SaveCollageAsync(_lastResultJpeg, DateTimeOffset.Now);

        _lastResultFilePath = finalPath;
        ShowReview(finalPath!, _lastResultJpeg, isGif: false, isBoomerang: false);

        // Auto-Print check
        if (_settingsService.Current.AutoPrint)
        {
            TriggerDirectPrint();
        }
    }

    private void TriggerFlash()
    {
        var anim = new DoubleAnimation
        {
            From = 0.9,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        FlashBorder.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void ShowReview(string filePath, byte[]? jpegBytes, bool isGif, bool isBoomerang, bool isVideo = false)
    {
        CountdownOverlayGrid.Visibility = Visibility.Collapsed;
        VideoRecordingOverlayGrid.Visibility = Visibility.Collapsed;

        if (isVideo)
        {
            _reviewGifPlayer.Stop();
            if (jpegBytes is not null)
            {
                using var stream = new MemoryStream(jpegBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                ReviewImageControl.Source = bitmap;
            }
            PrintButton.Visibility = Visibility.Collapsed;
            ReviewStatusTitleText.Text = "📹 VIDEOBOTSCHAFT GESPEICHERT!";
        }
        else if (isGif)
        {
            _reviewGifPlayer.Play(filePath);
            PrintButton.Visibility = Visibility.Collapsed;
            ReviewStatusTitleText.Text = isBoomerang ? "⚡ BOOMERANG GESPEICHERT!" : "🎬 GIF GESPEICHERT!";
        }
        else
        {
            _reviewGifPlayer.Stop();
            using var stream = jpegBytes is not null
                ? (Stream)new MemoryStream(jpegBytes)
                : new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            ReviewImageControl.Source = bitmap;
            PrintButton.Visibility = Visibility.Visible;
            ReviewStatusTitleText.Text = "FOTO GESPEICHERT!";
        }

        ReviewGrid.Visibility = Visibility.Visible;

        _reviewSecondsLeft = _settingsService.Current.ReviewSeconds;
        UpdateReviewTimerText();

        _reviewTimer?.Stop();
        _reviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reviewTimer.Tick += (_, _) =>
        {
            _reviewSecondsLeft--;
            if (_reviewSecondsLeft <= 0)
            {
                _reviewTimer.Stop();
                ReturnToIdle();
            }
            else
            {
                UpdateReviewTimerText();
            }
        };
        _reviewTimer.Start();
    }

    private void UpdateReviewTimerText()
    {
        ReviewTimerText.Text = $"Zurück in {_reviewSecondsLeft}s";
    }

    private void OnDoneReviewClick(object sender, RoutedEventArgs e)
    {
        _reviewTimer?.Stop();
        ReturnToIdle();
    }

    private void OnPrintPhotoClick(object sender, RoutedEventArgs e)
    {
        TriggerDirectPrint();
    }

    private async void TriggerDirectPrint()
    {
        if (_lastResultJpeg is null)
            return;

        PrintButton.IsEnabled = false;
        PrintButtonLabelText.Text = "Druckt...";
        PrintStatusFeedbackText.Visibility = Visibility.Visible;
        PrintStatusFeedbackText.Text = "Druckauftrag wird an den Selphy gesendet...";

        try
        {
            var printer = _settingsService.Current.SelectedPrinter;
            await _printService.PrintPhotoAsync(_lastResultJpeg, printer, copies: 1);

            PrintStatusFeedbackText.Text = "✅ Foto erfolgreich an den Drucker gesendet!";
            PrintButtonLabelText.Text = "Gedruckt ✓";
        }
        catch (Exception ex)
        {
            PrintStatusFeedbackText.Text = $"Druckfehler: {ex.Message}";
            PrintButtonLabelText.Text = "Drucken";
            PrintButton.IsEnabled = true;
        }
    }

    private void ReturnToIdle()
    {
        _reviewTimer?.Stop();
        _reviewGifPlayer.Stop();
        ReviewImageControl.Source = null;

        ReviewGrid.Visibility = Visibility.Collapsed;
        CountdownOverlayGrid.Visibility = Visibility.Collapsed;
        VideoRecordingOverlayGrid.Visibility = Visibility.Collapsed;
        IdleOverlayGrid.Visibility = Visibility.Visible;
        PrintButton.IsEnabled = true;
        PrintButtonLabelText.Text = "Drucken";
        PrintStatusFeedbackText.Visibility = Visibility.Collapsed;
        UpdateCornerGalleryThumbnail();
    }

    // ==================== CORNER GALLERY & GALLERY VIEWER ====================

    private void UpdateCornerGalleryThumbnail()
    {
        try
        {
            var photos = _storage.GetEventPhotos();
            if (photos.Count == 0)
            {
                _cornerGifPlayer.Stop();
                CornerGalleryBorder.Visibility = Visibility.Collapsed;
                return;
            }

            var latest = photos[0];
            var isGif = latest.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

            if (isGif)
            {
                _cornerGifPlayer.Play(latest);
            }
            else
            {
                _cornerGifPlayer.Stop();
                using var fileStream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = 160; // fast thumbnail
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fileStream;
                bmp.EndInit();
                bmp.Freeze();
                CornerThumbnailImage.Source = bmp;
            }

            GalleryCountText.Text = $"{photos.Count} Element{(photos.Count == 1 ? "" : "e")}";
            CornerGalleryBorder.Visibility = Visibility.Visible;
        }
        catch
        {
            CornerGalleryBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCornerGalleryClick(object sender, MouseButtonEventArgs e)
    {
        _galleryPhotos = _storage.GetEventPhotos().ToList();
        if (_galleryPhotos.Count == 0)
            return;

        _galleryCurrentIndex = 0;
        GalleryOverlayGrid.Visibility = Visibility.Visible;
        DisplayGalleryPhoto(_galleryCurrentIndex);
    }

    private void DisplayGalleryPhoto(int index)
    {
        if (index < 0 || index >= _galleryPhotos.Count)
            return;

        var path = _galleryPhotos[index];
        try
        {
            var isGif = path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
            if (isGif)
            {
                _galleryGifPlayer.Play(path);
                GalleryPrintButton.IsEnabled = false;
                GalleryPrintButtonLabel.Text = "🎬 Animiertes GIF";
            }
            else
            {
                _galleryGifPlayer.Stop();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();

                GalleryMainImage.Source = bmp;
                GalleryPrintButton.IsEnabled = true;
                GalleryPrintButtonLabel.Text = "Foto drucken (Selphy)";
            }

            GalleryIndexTitleText.Text = $"{(isGif ? "Animation" : "Foto")} {index + 1} von {_galleryPhotos.Count}";
            GalleryPhotoInfoText.Text = Path.GetFileName(path);
            GalleryPrintStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            GalleryPhotoInfoText.Text = $"Fehler beim Laden: {ex.Message}";
        }
    }

    private void OnGalleryNextClick(object sender, RoutedEventArgs e)
    {
        if (_galleryPhotos.Count == 0) return;
        _galleryCurrentIndex = (_galleryCurrentIndex + 1) % _galleryPhotos.Count;
        DisplayGalleryPhoto(_galleryCurrentIndex);
    }

    private void OnGalleryPrevClick(object sender, RoutedEventArgs e)
    {
        if (_galleryPhotos.Count == 0) return;
        _galleryCurrentIndex = (_galleryCurrentIndex - 1 + _galleryPhotos.Count) % _galleryPhotos.Count;
        DisplayGalleryPhoto(_galleryCurrentIndex);
    }

    private void OnCloseGalleryClick(object sender, RoutedEventArgs e)
    {
        _galleryGifPlayer.Stop();
        GalleryMainImage.Source = null;
        GalleryOverlayGrid.Visibility = Visibility.Collapsed;
        UpdateCornerGalleryThumbnail();
    }

    private async void OnGalleryPrintClick(object sender, RoutedEventArgs e)
    {
        if (_galleryPhotos.Count == 0 || _galleryCurrentIndex < 0 || _galleryCurrentIndex >= _galleryPhotos.Count)
            return;

        var path = _galleryPhotos[_galleryCurrentIndex];
        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            GalleryPrintStatusText.Visibility = Visibility.Visible;
            GalleryPrintStatusText.Text = "ℹ️ Animierte GIFs können nicht auf Papier gedruckt werden.";
            return;
        }

        GalleryPrintButton.IsEnabled = false;
        GalleryPrintButtonLabel.Text = "Druckt...";
        GalleryPrintStatusText.Visibility = Visibility.Visible;
        GalleryPrintStatusText.Text = "Druckauftrag wird an den Selphy gesendet...";

        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var printer = _settingsService.Current.SelectedPrinter;
            await _printService.PrintPhotoAsync(bytes, printer, copies: 1);

            GalleryPrintStatusText.Text = "✅ Foto erfolgreich an den Selphy gesendet!";
            GalleryPrintButtonLabel.Text = "Gedruckt ✓";
        }
        catch (Exception ex)
        {
            GalleryPrintStatusText.Text = $"Druckfehler: {ex.Message}";
            GalleryPrintButtonLabel.Text = "Foto drucken (Selphy)";
            GalleryPrintButton.IsEnabled = true;
        }
    }

    private void OnCameraStatusClick(object sender, MouseButtonEventArgs e)
    {
        if (_cameraManager.FallbackReason is not null)
        {
            MessageBox.Show(this, _cameraManager.FallbackReason, "Kamera-Information", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (_activeCamera is not null)
        {
            MessageBox.Show(this, $"Aktive Kamera: {_activeCamera.DisplayName}\nStatus: {_activeCamera.Status}", "Kamera-Information", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var aboutDialog = new AboutDialog
        {
            Owner = this
        };
        aboutDialog.ShowDialog();
    }

    private void OnAdminLockClick(object sender, RoutedEventArgs e)
    {
        OpenAdminMenu();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Secret shortcut: Esc or Ctrl+A opens Admin PIN dialog
        if (e.Key == Key.Escape || (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control))
        {
            OpenAdminMenu();
            e.Handled = true;
        }
        else if (GalleryOverlayGrid.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Left)
            {
                OnGalleryPrevClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                OnGalleryNextClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }

    private void OpenAdminMenu()
    {
        var pinDialog = new AdminPinDialog(_settingsService.Current.AdminPin)
        {
            Owner = this,
        };

        if (pinDialog.ShowDialog() == true && pinDialog.IsAuthenticated)
        {
            var adminDashboard = new AdminDashboardDialog(_settingsService, _storage, _templateStore, _printService, _cameraManager)
            {
                Owner = this,
            };

            adminDashboard.ShowDialog();

            if (adminDashboard.ExitRequested)
            {
                Close();
            }
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _reviewGifPlayer.Dispose();
        _galleryGifPlayer.Dispose();
        _cornerGifPlayer.Dispose();

        _frameWatchdogTimer?.Stop();
        _reconnectCts?.Cancel();

        if (_activeCamera is not null)
        {
            _activeCamera.FrameReady -= OnCameraFrameReady;
            _activeCamera.StatusChanged -= OnCameraStatusChanged;
        }
        await _cameraManager.DisposeAsync();
    }
}
