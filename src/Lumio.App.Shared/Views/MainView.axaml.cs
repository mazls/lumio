using System;
using Avalonia;
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
using Lumio.Camera;
using Lumio.Core.Camera;
using Lumio.Core.Session;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Lumio.Core.Updates;
using Lumio.Imaging;
using Lumio.Printing;
using SkiaSharp;

namespace Lumio.App.Avalonia.Views;

public partial class MainView : UserControl
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
    private UiTimer? _reviewTimer;
    private int _galleryCurrentIndex;
    private List<string> _galleryFiles = [];
    private GalleryMediaPlayer? _galleryPlayer;

    // Resilient Auto-Reconnect
    private readonly object _reconnectLock = new();
    private CancellationTokenSource? _reconnectCts;
    private bool _isReconnecting;
    private DateTime _lastFrameTime = DateTime.MinValue;
    private UiTimer? _watchdogTimer;
    private int _noFrameReconnects;
    private const int MaxNoFrameReconnects = 3;
    private UiTimer? _bulbTimer;
    private int _isRenderingFrame = 0;
    private long _lastFrameRenderTicks = 0;

    public MainView()
    {
        InitializeComponent();
        _galleryPlayer = new GalleryMediaPlayer(GalleryMainImage, GalleryVideoHost);

        var timeProvider = TimeProvider.System;
        var settingsStore = new SettingsStore();
        _settingsService = new SettingsService(settingsStore);
        _storage = new EventStorage(_settingsService, timeProvider);
        _cameraManager = new CameraManager();
        _templateStore = new TemplateStore();
        _printService = new SelphyPrintService();

        _activeTemplate = _templateStore.GetById(_settingsService.Current.SelectedTemplateId)
                          ?? BuiltInTemplates.CreateSinglePhotoPostcard();

    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SetupEdgeToEdge();
        Focus();
        InitializeBooth();
    }

    protected override async void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        await TeardownBoothAsync();
    }

    public void ShowModal(Control content)
    {
        // Remember the dialog's designed size so it can shrink on small screens and grow back on rotation
        _modalDesignWidth = content.Width;
        _modalDesignMaxHeight = content.MaxHeight;
        ModalOverlayContent.Content = content;
        ModalOverlayGrid.IsVisible = true;
        FitModalToScreen();
    }

    private double _modalDesignWidth = double.NaN;
    private double _modalDesignMaxHeight = double.PositiveInfinity;
    private bool? _isPortraitLayout;

    private void FitModalToScreen()
    {
        if (ModalOverlayContent.Content is not Control content || Bounds.Width <= 0)
            return;

        var availableWidth = Math.Max(200, Bounds.Width - 24);
        var availableHeight = Math.Max(200, Bounds.Height - 24);
        if (!double.IsNaN(_modalDesignWidth))
            content.Width = Math.Min(_modalDesignWidth, availableWidth);
        // Dialogs with their own scrolling (admin menu) are capped to the screen; the others scroll as a whole
        if (!double.IsPositiveInfinity(_modalDesignMaxHeight))
            content.MaxHeight = Math.Min(_modalDesignMaxHeight, availableHeight);
    }

    // ==================== EDGE TO EDGE ====================

    /// <summary>Space taken by the camera cutout / system bars (Android); zero on desktops.</summary>
    private Thickness _safeArea;

    /// <summary>
    /// Backgrounds and live view reach into the camera cutout area (instead of a black bar there),
    /// while buttons and texts are kept out of it.
    /// </summary>
    private void SetupEdgeToEdge()
    {
        // Avalonia would otherwise pad the whole view, leaving a bare strip around the cutout
        TopLevel.SetAutoSafeAreaPadding(this, false);
        if (TopLevel.GetTopLevel(this)?.InsetsManager is not { } insets)
            return;

        insets.DisplayEdgeToEdgePreference = true;
        insets.SafeAreaChanged += (_, e) => ApplySafeArea(e.SafeAreaPadding);
        ApplySafeArea(insets.SafeAreaPadding);
    }

    private void ApplySafeArea(Thickness safe)
    {
        _safeArea = safe;
        TopBarGrid.Margin = new Thickness(24 + safe.Left, 20 + safe.Top, 24 + safe.Right, 0);
        UpdateBanner.Margin = new Thickness(16 + safe.Left, 84 + safe.Top, 16 + safe.Right, 0);
        CornerGalleryBorder.Margin = new Thickness(24 + safe.Left, 0, 0, 20 + safe.Bottom);
        GalleryHeaderGrid.Margin = new Thickness(28 + safe.Left, 20 + safe.Top, 28 + safe.Right, 20);
        GalleryFooterBorder.Padding = new Thickness(28 + safe.Left, 16, 28 + safe.Right, 16 + safe.Bottom);

        // Review layout depends on the orientation: lay it out again with the new insets
        _isPortraitLayout = null;
        if (Bounds.Width > 0)
            ApplyOrientationLayout(Bounds.Size);
    }

    private Thickness WithSafeArea(Thickness margin, bool top = true) => new(
        margin.Left + _safeArea.Left,
        margin.Top + (top ? _safeArea.Top : 0),
        margin.Right + _safeArea.Right,
        margin.Bottom + _safeArea.Bottom);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyOrientationLayout(e.NewSize);
        FitModalToScreen();
    }

    /// <summary>
    /// Rearranges the kiosk UI for portrait screens (phones/tablets held upright, portrait monitors)
    /// and back to the original landscape layout.
    /// </summary>
    private void ApplyOrientationLayout(Size size)
    {
        var portrait = size.Height > size.Width;
        var width = size.Width;

        // Size-dependent limits
        StartSubtitleText.MaxWidth = Math.Max(200, width - 48);
        EventTitleText.MaxWidth = Math.Max(120, width - 140);
        CameraStatusText.MaxWidth = Math.Max(100, width - 230);
        var previewWidth = Math.Min(480, width - 48);
        SeriesPreviewBorder.Width = previewWidth;
        SeriesPreviewBorder.Height = previewWidth * 2 / 3;

        if (_isPortraitLayout == portrait)
            return;
        _isPortraitLayout = portrait;

        // Mode pills: one row in landscape, 2 columns x 3 rows in portrait
        Button[] modeButtons = [ModeSingleButton, ModeQuadButton, ModeStripButton, ModeGifButton, ModeBoomerangButton, ModeVideoButton];
        var columns = portrait ? 2 : modeButtons.Length;
        ModeSelectorGrid.ColumnDefinitions.Clear();
        ModeSelectorGrid.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++)
            ModeSelectorGrid.ColumnDefinitions.Add(new ColumnDefinition(portrait ? GridLength.Star : GridLength.Auto));
        for (var r = 0; r < (modeButtons.Length + columns - 1) / columns; r++)
            ModeSelectorGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < modeButtons.Length; i++)
        {
            PlaceInGrid(modeButtons[i], row: i / columns, column: i % columns);
            modeButtons[i].HorizontalAlignment = portrait ? global::Avalonia.Layout.HorizontalAlignment.Stretch : global::Avalonia.Layout.HorizontalAlignment.Left;
            modeButtons[i].HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center;
        }

        // Start button
        StartCaptureButton.Height = portrait ? 84 : 100;
        StartCaptureButton.Padding = portrait ? new Thickness(32, 0) : new Thickness(50, 0);
        StartButtonIcon.FontSize = portrait ? 32 : 40;
        StartButtonLabel.FontSize = portrait ? 26 : 32;
        StopRecordingLabel.FontSize = portrait ? 20 : 24;

        // Review bar: one row in landscape; status on top and both buttons side by side below in portrait
        ReviewActionsGrid.ColumnDefinitions.Clear();
        ReviewActionsGrid.RowDefinitions.Clear();
        if (portrait)
        {
            ReviewActionsGrid.ColumnDefinitions = new ColumnDefinitions("*,12,*");
            ReviewActionsGrid.RowDefinitions = new RowDefinitions("Auto,14,Auto");
            PlaceInGrid(ReviewStatusPanel, row: 0, column: 0, columnSpan: 3);
            PlaceInGrid(PrintButton, row: 2, column: 0);
            PlaceInGrid(ReviewDoneButton, row: 2, column: 2);
            ReviewActionsBar.Padding = WithSafeArea(new Thickness(20, 16), top: false);
        }
        else
        {
            ReviewActionsGrid.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,16,Auto");
            PlaceInGrid(ReviewStatusPanel, row: 0, column: 0);
            PlaceInGrid(PrintButton, row: 0, column: 2);
            PlaceInGrid(ReviewDoneButton, row: 0, column: 4);
            ReviewActionsBar.Padding = WithSafeArea(new Thickness(36, 20), top: false);
        }
        ReviewDoneButton.Content = portrait ? "Fertig" : "Fertig / Nächstes Foto";
        UpdateReviewImageMargin();
        foreach (var button in new[] { PrintButton, ReviewDoneButton })
        {
            button.HorizontalAlignment = portrait ? global::Avalonia.Layout.HorizontalAlignment.Stretch : global::Avalonia.Layout.HorizontalAlignment.Left;
            button.HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center;
        }

        UpdateBackdrop(size);

        // Live view: fill the screen in landscape; in portrait show the whole camera frame across the full width,
        // otherwise only a narrow middle strip of the (landscape) photo would be visible
        LiveViewImageControl.Stretch = portrait ? Stretch.Uniform : Stretch.UniformToFill;

        // Fullscreen gallery: no room for side margins next to the arrows in portrait
        GalleryMainImage.Margin = portrait ? new Thickness(0, 10) : new Thickness(80, 10);
        // A native video view is drawn above everything: keep the arrow buttons next to it visible
        GalleryVideoHost.Margin = portrait ? new Thickness(64, 10) : GalleryMainImage.Margin;

        // Gallery footer: file name above the buttons on narrow portrait screens
        GalleryFooterGrid.RowDefinitions = portrait ? new RowDefinitions("Auto,10,Auto") : new RowDefinitions("Auto");
        PlaceInGrid(GalleryInfoPanel, row: 0, column: 0, columnSpan: portrait ? 3 : 1);
        PlaceInGrid(GalleryDeleteButton, row: portrait ? 2 : 0, column: 1);
        PlaceInGrid(GalleryPrintButton, row: portrait ? 2 : 0, column: 2);
        GalleryFooterGrid.ColumnDefinitions = portrait ? new ColumnDefinitions("0,*,*") : new ColumnDefinitions("*,Auto,Auto");
        GalleryDeleteButton.HorizontalAlignment = GalleryPrintButton.HorizontalAlignment = portrait
            ? global::Avalonia.Layout.HorizontalAlignment.Stretch
            : global::Avalonia.Layout.HorizontalAlignment.Left;
        GalleryDeleteButton.HorizontalContentAlignment = GalleryPrintButton.HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center;
    }

    /// <summary>Renders the party backdrop in the screen's aspect ratio (only when the orientation changes).</summary>
    private void UpdateBackdrop(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            return;

        // ~1600 px on the long side is plenty for a soft, blurry background
        var scale = 1600 / Math.Max(size.Width, size.Height);
        var width = Math.Max(16, (int)(size.Width * scale));
        var height = Math.Max(16, (int)(size.Height * scale));

        _ = Task.Run(() =>
        {
            try
            {
                var (background, bulbsA, bulbsB) = PartyScene.RenderLayeredPng(width, height);
                Dispatcher.UIThread.Post(() =>
                {
                    IdleBackdropImage.Source = LoadBitmap(background);
                    IdleBulbsAImage.Source = LoadBitmap(bulbsA);
                    IdleBulbsBImage.Source = LoadBitmap(bulbsB);
                    StartBulbAnimation();
                });
            }
            catch
            {
                // Decorative only
            }
        });
    }

    private static Bitmap LoadBitmap(byte[] png)
    {
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }

    /// <summary>
    /// Lets the fairy lights twinkle by fading the two bulb layers against each other. Runs at ~12 fps and only
    /// while the backdrop is actually visible (no live view), so it costs next to nothing.
    /// </summary>
    private void StartBulbAnimation()
    {
        if (_bulbTimer is not null)
            return;

        var start = Environment.TickCount64;
        _bulbTimer = new UiTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _bulbTimer.Tick += (_, _) =>
        {
            if (LiveViewImageControl.Source is not null || ReviewGrid.IsVisible || GalleryOverlayGrid.IsVisible)
                return;

            var t = (Environment.TickCount64 - start) / 1000.0;
            IdleBulbsAImage.Opacity = 0.6 + 0.4 * Math.Sin(t * 1.9);
            IdleBulbsBImage.Opacity = 0.6 + 0.4 * Math.Sin(t * 1.9 + Math.PI * 0.85 + 0.3 * Math.Sin(t * 0.37));
        };
        _bulbTimer.Start();
    }

    private static void PlaceInGrid(Control control, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, columnSpan);
    }

    public void CloseModal()
    {
        ModalOverlayContent.Content = null;
        ModalOverlayGrid.IsVisible = false;
    }

    private void InitializeBooth()
    {
        UpdateLiveViewMirror();
        EventTitleText.Text = _settingsService.Current.EventName.ToUpperInvariant();
        UpdateModeButtons();
        CameraManager.OnHardwareChanged += OnHardwareChanged;
        _ = StartCameraAsync();
        StartWatchdog();
        RefreshGalleryCorner();

        UpdateChecker.UpdateAvailable += OnUpdateAvailable;
        ShowUpdateBanner(UpdateChecker.AvailableUpdate);
        UpdateChecker.StartPeriodicCheck();
    }

    private void OnUpdateAvailable(ReleaseInfo release) => Dispatcher.UIThread.Post(() => ShowUpdateBanner(release));

    private void ShowUpdateBanner(ReleaseInfo? release)
    {
        UpdateBanner.IsVisible = release is not null && !UpdateChecker.IsDismissed(release);
        if (release is not null)
            UpdateBannerText.Text = $"Lumio {release.DisplayVersion} ist verfügbar – Details im Admin-Menü";
    }

    private void OnDismissUpdateClick(object? sender, RoutedEventArgs e)
    {
        UpdateBanner.IsVisible = false;
        if (UpdateChecker.AvailableUpdate is { } release)
            UpdateChecker.Dismiss(release);
    }

    private void OnHardwareChanged(bool devicesChanged)
    {
        // If already connected to a healthy real camera, ignore - unless a camera was plugged in or removed,
        // because an external camera takes priority over the built-in ones
        if (!devicesChanged && _activeCamera is not null && _activeCamera.Status == CameraStatus.Connected && _cameraManager.FallbackReason is null && _settingsService.Current.CameraBackend != CameraBackend.Simulated)
        {
            return;
        }

        if (devicesChanged)
        {
            _cameraManager.Invalidate();
        }

        Dispatcher.UIThread.Post(() =>
        {
            _noFrameReconnects = 0;
            TriggerAutoReconnect("Hardware geändert", cameraSwitch: devicesChanged);
        });
    }

    private async Task<bool> StartCameraAsync(bool isReconnect = false)
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

            Dispatcher.UIThread.Post(() =>
            {
                CameraStatusText.Text = isReconnect ? "Kamera verbinden..." : "Kamera wird initialisiert...";
            });

            // Perform camera discovery and connection in background thread so UI stays 100% fluid
            var camera = await Task.Run(() => _cameraManager.GetCameraAsync(_settingsService.Current));
            _activeCamera = camera;
            _activeCamera.FrameReady += OnCameraFrameReady;
            _activeCamera.StatusChanged += OnCameraStatusChanged;

            if (LiveViewNeeded)
            {
                await Task.Run(() => _activeCamera.StartLiveViewAsync());
            }
            else
            {
                // Battery saver: camera stays connected but idle until the next countdown
                await Task.Run(() => _activeCamera.StopLiveViewAsync());
                Dispatcher.UIThread.Post(() => LiveViewImageControl.Source = null);
            }
            _lastFrameTime = DateTime.UtcNow;

            Dispatcher.UIThread.Post(() =>
            {
                CameraStatusText.Text = $"{_activeCamera.DisplayName} bereit";
            });

            return _activeCamera.Status == CameraStatus.Connected && _cameraManager.FallbackReason is null;
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                CameraStatusText.Text = isReconnect ? "Kamera getrennt..." : $"Kamerafehler: {ex.Message}";
            });
            return false;
        }
    }

    private void StartWatchdog()
    {
        _watchdogTimer?.Stop();
        _watchdogTimer = new UiTimer { Interval = TimeSpan.FromSeconds(5) };
        _watchdogTimer.Tick += (s, e) =>
        {
            if (!_isReconnecting && LiveViewNeeded)
            {
                // Only monitor if a REAL camera is currently connected and supposed to be sending frames
                // Never poll or loop when in simulated fallback mode. Hardware change events handle reconnect!
                if (_activeCamera is not null &&
                    _activeCamera.Status == CameraStatus.Connected &&
                    _cameraManager.FallbackReason is null &&
                    _settingsService.Current.CameraBackend != CameraBackend.Simulated)
                {
                    if (_lastFrameTime != DateTime.MinValue && (DateTime.UtcNow - _lastFrameTime) > TimeSpan.FromSeconds(6))
                    {
                        // A camera that is connected but never delivers a picture is not reconnected forever
                        if (++_noFrameReconnects <= MaxNoFrameReconnects)
                            TriggerAutoReconnect("Keine Bilder empfangen");
                        else
                            CameraStatusText.Text = $"{_activeCamera.DisplayName} liefert kein Bild – bitte neu einstecken";
                    }
                }
            }
        };
        _watchdogTimer.Start();
    }

    // A camera switch (device plugged in/out) must not be ended by frames of the still running old camera,
    // and a switch requested during a running reconnect is repeated afterwards instead of being dropped.
    private bool _reconnectIsCameraSwitch;
    private bool _cameraSwitchPending;

    private void TriggerAutoReconnect(string reason, bool cameraSwitch = false)
    {
        lock (_reconnectLock)
        {
            if (_isReconnecting)
            {
                _cameraSwitchPending |= cameraSwitch;
                return;
            }

            _isReconnecting = true;
            _reconnectIsCameraSwitch = cameraSwitch;
            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();
        }

        var token = _reconnectCts.Token;
        Dispatcher.UIThread.Post(() =>
        {
            CameraStatusText.Text = "Kamera verbinden...";
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, token);
                int attempts = 0;
                while (!token.IsCancellationRequested && attempts++ < 2)
                {
                    bool reconnected = await StartCameraAsync(isReconnect: true);
                    if (reconnected) break;
                    if (attempts < 2) await Task.Delay(2000, token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                bool switchAgain;
                lock (_reconnectLock)
                {
                    _isReconnecting = false;
                    switchAgain = _cameraSwitchPending;
                    _cameraSwitchPending = false;
                }

                if (switchAgain)
                {
                    _cameraManager.Invalidate();
                    Dispatcher.UIThread.Post(() => TriggerAutoReconnect("Hardware geändert", cameraSwitch: true));
                }
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
        _noFrameReconnects = 0;
        _firstLiveViewFrame?.TrySetResult();
        if (!LiveViewNeeded)
            return;

        if (_isReconnecting && !_reconnectIsCameraSwitch)
        {
            lock (_reconnectLock)
            {
                _isReconnecting = false;
                _reconnectCts?.Cancel();
            }
        }

        // Throttle live view rendering to max ~15 FPS (every 66ms) to keep touch input 100% responsive
        var nowTicks = Environment.TickCount64;
        if (nowTicks - Interlocked.Read(ref _lastFrameRenderTicks) < 66)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isRenderingFrame, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastFrameRenderTicks, nowTicks);

        var frameData = frame.Data.ToArray();
        var isJpeg = frame.Format == FrameFormat.Jpeg;
        var width = frame.Width;
        var height = frame.Height;
        var stride = frame.Stride;

        // Decode the image OFF the UI thread!
        Task.Run(() =>
        {
            try
            {
                Bitmap? bitmap = null;
                if (isJpeg)
                {
                    using var ms = new MemoryStream(frameData);
                    bitmap = new Bitmap(ms);
                }
                else
                {
                    var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    using var skImage = SKImage.FromPixelCopy(info, frameData, stride);
                    using var data = skImage.Encode(SKEncodedImageFormat.Jpeg, 60);
                    using var ms = new MemoryStream(data.ToArray());
                    bitmap = new Bitmap(ms);
                }

                // Post ONLY the bitmap reference swap at Background priority (priority 4, below Input priority 5!)
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (ReviewGrid.IsVisible || GalleryOverlayGrid.IsVisible || !LiveViewNeeded)
                        {
                            bitmap.Dispose();
                            return;
                        }

                        var old = LiveViewImageControl.Source as Bitmap;
                        LiveViewImageControl.Source = bitmap;
                        old?.Dispose();
                    }
                    catch { }
                    finally
                    {
                        Interlocked.Exchange(ref _isRenderingFrame, 0);
                    }
                }, DispatcherPriority.Background);
            }
            catch
            {
                Interlocked.Exchange(ref _isRenderingFrame, 0);
            }
        });
    }

    private async void OnStartCaptureClick(object? sender, RoutedEventArgs e)
    {
        if (_activeCamera is null)
        {
            CameraStatusText.Text = "Kamera wird initialisiert...";
            await StartCameraAsync();
            if (_activeCamera is null)
            {
                CameraStatusText.Text = "Keine Kamera bereit!";
                return;
            }
        }

        if (_captureSessionActive)
            return;
        _captureSessionActive = true;

        IdleOverlayGrid.IsVisible = false;
        CornerGalleryBorder.IsVisible = false;
        CountdownOverlayGrid.IsVisible = true;

        try
        {
            if (_settingsService.Current.LiveViewOnlyDuringCapture)
            {
                await StartCaptureLiveViewAsync();
            }

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
            Console.Error.WriteLine($"Capture workflow failed: {ex}");
            CameraStatusText.Text = $"Fehler: {ex.Message}";
            EndCaptureSession();
        }
    }

    // ==================== BATTERY SAVER LIVE VIEW ====================

    /// <summary>From the start of a capture until the result is dismissed.</summary>
    private bool _captureSessionActive;
    private TaskCompletionSource? _firstLiveViewFrame;

    /// <summary>The live view runs permanently, or only during a capture session in battery saver mode.</summary>
    private bool LiveViewNeeded => !_settingsService.Current.LiveViewOnlyDuringCapture || _captureSessionActive;

    private async Task StartCaptureLiveViewAsync()
    {
        if (_activeCamera is null)
            return;

        MultiShotStepText.IsVisible = false;
        CountdownNumberText.Text = "📷";
        CountdownStatusText.Text = "Kamera wird vorbereitet...";

        _firstLiveViewFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastFrameTime = DateTime.UtcNow;
        await Task.Run(() => _activeCamera.StartLiveViewAsync());

        // Start the countdown once guests can see themselves (DSLRs need a moment to raise the mirror)
        await Task.WhenAny(_firstLiveViewFrame.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        _firstLiveViewFrame = null;
    }

    private void EndCaptureSession()
    {
        _captureSessionActive = false;
        if (!_settingsService.Current.LiveViewOnlyDuringCapture || _activeCamera is null)
            return;

        var camera = _activeCamera;
        LiveViewImageControl.Source = null;
        _ = Task.Run(async () =>
        {
            try { await camera.StopLiveViewAsync(); } catch { }
        });
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
        var finalPath = await _storage.MoveVideoAsync(result.VideoPath, DateTimeOffset.Now, extension: extension, posterJpeg: result.ThumbnailJpeg);

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

        var template = BuiltInTemplates.MatchPhotoOrientation(_activeTemplate, capturedPhotos[0]);
        var finalJpeg = await Task.Run(() => _renderer.Render(
            template,
            capturedPhotos,
            metadata,
            jpegQuality: 98,
            embedLogo: settings.EmbedLogoOnPhotos,
            logoPosition: settings.LogoPosition,
            embedEventText: settings.EmbedEventTextOnPhotos,
            renderScale: 2.0f));

        if (settings.BeautyFilterStrength > 0)
        {
            var strength = settings.BeautyFilterStrength;
            finalJpeg = await Task.Run(() => BeautyFilter.Apply(finalJpeg, strength));
        }

        var collagePath = await _storage.SaveCollageAsync(finalJpeg, DateTimeOffset.Now);
        ShowReview(finalJpeg, looksFor: settings.GuestLooksEnabled ? collagePath : null);
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

    /// <param name="looksFor">Saved collage the guests may put a look on (it is overwritten with their choice).</param>
    private void ShowReview(byte[] displayJpeg, string? looksFor = null)
    {
        ShowLookBar(displayJpeg, looksFor);
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
        // Picking a look takes a moment: at least 20 s while the looks are offered
        _reviewRemainingSeconds = Math.Max(LookBar.IsVisible ? MinLookReviewSeconds : 5, _settingsService.Current.ReviewSeconds);
        ReviewTimerText.Text = $"Zurück in {_reviewRemainingSeconds}s";

        _reviewTimer?.Stop();
        _reviewTimer = new UiTimer { Interval = TimeSpan.FromSeconds(1) };
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

    // ==================== LOOKS (GUEST FILTERS) ====================

    private byte[]? _lookBaseJpeg;
    private string? _lookTargetPath;
    private int _lookVersion;
    private const int MinLookReviewSeconds = 20;
    private double LookBarHeight => LookButtonsPanel.Rows * 100 + 10;

    private void ShowLookBar(byte[] baseJpeg, string? savedPath)
    {
        _lookVersion++;
        _lookBaseJpeg = savedPath is null ? null : baseJpeg;
        _lookTargetPath = savedPath;
        LookButtonsPanel.Children.Clear();
        LookBar.IsVisible = savedPath is not null;
        UpdateReviewImageMargin();
        if (savedPath is null)
            return;

        var buttons = new List<(PhotoLook Look, Image Preview)>();
        foreach (var (look, name) in PhotoLooks.All)
        {
            var preview = new Image { Height = 58, Stretch = Stretch.UniformToFill };
            var button = new Button
            {
                Padding = new Thickness(4),
                Margin = new Thickness(4),
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(14),
                Background = (IBrush)this.FindResource("GlassBrush")!,
                BorderThickness = new Thickness(2),
                Tag = look,
                Content = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = preview },
                        new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center },
                    },
                },
            };
            button.Click += (_, _) => _ = SelectLookAsync(look);
            buttons.Add((look, preview));
            LookButtonsPanel.Children.Add(button);
        }
        HighlightLook(PhotoLook.Original);

        // Previews from a small copy: decoding the full collage six times would be slow on phones
        var version = _lookVersion;
        _ = Task.Run(() =>
        {
            try
            {
                var small = PhotoLooks.Preview(baseJpeg, PhotoLook.Original, 200);
                foreach (var (look, image) in buttons)
                {
                    var bytes = PhotoLooks.Preview(small, look, 168);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (version != _lookVersion) return;
                        using var stream = new MemoryStream(bytes);
                        image.Source = new Bitmap(stream);
                    });
                }
            }
            catch { }
        });
    }

    private void HighlightLook(PhotoLook selected)
    {
        foreach (var button in LookButtonsPanel.Children.OfType<Button>())
        {
            button.BorderBrush = button.Tag is PhotoLook look && look == selected
                ? (IBrush)this.FindResource("AccentBrush")!
                : Brushes.Transparent;
        }
    }

    /// <summary>Applies the guest's look: shown, printed and saved (overwrites the collage; originals stay untouched).</summary>
    private async Task SelectLookAsync(PhotoLook look)
    {
        if (_lookBaseJpeg is not { } baseJpeg || _lookTargetPath is not { } path)
            return;

        var version = ++_lookVersion;
        HighlightLook(look);
        StartReviewCountdown(); // give guests time to look at it

        byte[] result;
        try
        {
            result = await Task.Run(() => PhotoLooks.Apply(baseJpeg, look));
        }
        catch
        {
            return;
        }
        if (version != _lookVersion)
            return; // another look was tapped meanwhile

        _currentReviewJpeg = result;
        using (var stream = new MemoryStream(result))
            ReviewImageControl.Source = new Bitmap(stream);

        try
        {
            await File.WriteAllBytesAsync(path, result);
            EventStorage.NotifyFileSaved(path);
            RefreshGalleryCorner();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Saving look failed: {ex.Message}");
        }
    }

    /// <summary>The review photo keeps clear of the bottom bar (and the look bar when shown).</summary>
    private void UpdateReviewImageMargin()
    {
        var portrait = _isPortraitLayout == true;
        LookButtonsPanel.Rows = portrait ? 2 : 1;
        LookButtonsPanel.Columns = portrait ? 3 : PhotoLooks.All.Count;
        var margin = portrait ? new Thickness(16, 90, 16, 220) : new Thickness(40, 50, 40, 130);
        if (LookBar.IsVisible)
            margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + LookBarHeight);
        ReviewImageControl.Margin = WithSafeArea(margin);
    }

    private void OnDoneReviewClick(object? sender, RoutedEventArgs e)
    {
        _lookVersion++;
        LookBar.IsVisible = false;
        _reviewTimer?.Stop();
        ReviewGrid.IsVisible = false;
        VideoRecordingOverlayGrid.IsVisible = false;
        CountdownOverlayGrid.IsVisible = false;
        IdleOverlayGrid.IsVisible = true;
        CornerGalleryBorder.IsVisible = true;
        EndCaptureSession();
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
            await _printService.PrintPhotoAsync(_currentReviewJpeg, _settingsService.Current.SelectedPrinter, borderless: _settingsService.Current.BorderlessPrint);
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
                // GIFs decode to their first frame, video messages show their poster image
                var latest = files.FirstOrDefault(f => !GalleryMediaPlayer.IsVideo(f) || File.Exists(GalleryMediaPlayer.PosterPath(f))) ?? files[0];
                var thumbnail = GalleryMediaPlayer.IsVideo(latest) ? GalleryMediaPlayer.PosterPath(latest) : latest;
                if (File.Exists(thumbnail))
                {
                    using var ms = new MemoryStream(File.ReadAllBytes(thumbnail));
                    CornerThumbnailImage.Source = Bitmap.DecodeToWidth(ms, 400);
                }
                GalleryCountText.Text = $"{files.Count} Foto{(files.Count == 1 ? "" : "s")}";
                // Only on the start screen - in the review it would cover the status text
                CornerGalleryBorder.IsVisible = IdleOverlayGrid.IsVisible;
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
        var isVideo = GalleryMediaPlayer.IsVideo(file);
        var isAnimation = GalleryMediaPlayer.IsGif(file);
        var kind = isVideo ? "Video" : isAnimation ? "Animation" : "Foto";
        GalleryIndexTitleText.Text = $"{kind} {_galleryCurrentIndex + 1} von {_galleryFiles.Count}";
        GalleryPhotoInfoText.Text = Path.GetFileName(file);
        GalleryPrintStatusText.IsVisible = false;
        GalleryPrintButton.IsVisible = !isVideo && !isAnimation;
        try
        {
            _galleryPlayer?.Show(file);
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
            await _printService.PrintPhotoAsync(bytes, _settingsService.Current.SelectedPrinter, borderless: _settingsService.Current.BorderlessPrint);
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

    /// <summary>Deletes the shown gallery item after the delete PIN (by default the admin PIN) was entered.</summary>
    private async void OnGalleryDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_galleryFiles.Count == 0)
            return;
        var file = _galleryFiles[_galleryCurrentIndex];

        // A native video view would cover the PIN dialog
        _galleryPlayer?.Stop();

        var pinTcs = new TaskCompletionSource<bool>();
        var pinDialog = new AdminPinDialog(
            _settingsService.Current.EffectiveDeletePin,
            title: "Löschen",
            subtitle: $"PIN eingeben, um {Path.GetFileName(file)} zu löschen",
            hint: "Lösch-PIN – ohne eigene Lösch-PIN gilt die Admin-PIN");
        pinDialog.Completed += ok =>
        {
            CloseModal();
            pinTcs.TrySetResult(ok);
        };
        ShowModal(pinDialog);

        if (!await pinTcs.Task)
        {
            DisplayGalleryPhoto();
            return;
        }

        try
        {
            EventStorage.DeleteMedia(file);
        }
        catch (Exception ex)
        {
            DisplayGalleryPhoto();
            GalleryPrintStatusText.Text = $"Löschen fehlgeschlagen: {ex.Message}";
            GalleryPrintStatusText.IsVisible = true;
            return;
        }

        _galleryFiles.RemoveAt(_galleryCurrentIndex);
        RefreshGalleryCorner();
        if (_galleryFiles.Count == 0)
        {
            CornerGalleryBorder.IsVisible = false;
            GalleryOverlayGrid.IsVisible = false;
            return;
        }

        _galleryCurrentIndex = Math.Min(_galleryCurrentIndex, _galleryFiles.Count - 1);
        DisplayGalleryPhoto();
        GalleryPrintStatusText.Text = $"{Path.GetFileName(file)} gelöscht";
        GalleryPrintStatusText.IsVisible = true;
    }

    private void OnCloseGalleryClick(object? sender, RoutedEventArgs e)
    {
        _galleryPlayer?.Stop();
        GalleryOverlayGrid.IsVisible = false;
    }

    // ==================== ADMIN & ABOUT ====================
    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new AboutDialog();
        about.Closed += CloseModal;
        ShowModal(about);
    }

    private async void OnAdminLockClick(object? sender, RoutedEventArgs e)
    {
        await OpenAdminMenuAsync();
    }

    private async Task OpenAdminMenuAsync()
    {
        var pinTcs = new TaskCompletionSource<bool>();
        var pinDialog = new AdminPinDialog(_settingsService.Current.AdminPin);
        pinDialog.Completed += auth =>
        {
            CloseModal();
            pinTcs.TrySetResult(auth);
        };

        ShowModal(pinDialog);
        var isAuthenticated = await pinTcs.Task;

        if (isAuthenticated)
        {
            var dashTcs = new TaskCompletionSource<bool>();
            var dashboard = new AdminDashboardDialog(_settingsService, _storage, _printService, _templateStore);

            dashboard.AboutRequested += () =>
            {
                var about = new AboutDialog();
                about.Closed += () => ShowModal(dashboard);
                ShowModal(about);
            };

            dashboard.Closed += (s, e) =>
            {
                CloseModal();
                dashTcs.TrySetResult(dashboard.ExitRequested);
            };

            ShowModal(dashboard);
            var exitRequested = await dashTcs.Task;

            if (exitRequested)
            {
                if (VisualRoot is Window window)
                {
                    window.Close();
                }
                else
                {
                    Environment.Exit(0);
                }
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

    private async void OnControlKeyDown(object? sender, KeyEventArgs e)
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
        CameraManager.OnHardwareChanged -= OnHardwareChanged;
        UpdateChecker.UpdateAvailable -= OnUpdateAvailable;
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
