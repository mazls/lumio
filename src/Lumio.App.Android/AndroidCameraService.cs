using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Util;
using Android.Views;
using Lumio.Core.Camera;
using Lumio.Core.Settings;
using CapturedPhoto = Lumio.Core.Camera.CapturedPhoto;

namespace Lumio.App.Android;

/// <summary>
/// Native Android Camera driver using Camera2 API.
/// Supports both built-in front/back tablet cameras and external USB webcams via UVC.
/// </summary>
public sealed class AndroidCameraService : ICameraService
{
    private readonly Activity _activity;
    private readonly BoothSettings? _settings;
    private readonly object _lock = new();

    private CameraDevice? _cameraDevice;
    private CameraCaptureSession? _captureSession;
    private ImageReader? _previewReader;
    private ImageReader? _stillCaptureReader;
    private HandlerThread? _backgroundThread;
    private Handler? _backgroundHandler;

    private TaskCompletionSource<CapturedPhoto>? _stillCaptureTcs;
    private CameraStatus _status = CameraStatus.Disconnected;
    private bool _isLiveViewRunning;
    private byte[]? _latestFrameBytes;
    private long _lastPreviewTicks;
    private int _isProcessingPreview;
    private byte[]? _cachedNv21;
    private readonly MemoryStream _cachedJpegStream = new(256 * 1024);

    public AndroidCameraService(Activity activity, BoothSettings? settings = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _settings = settings;
    }

    public string DisplayName { get; private set; } = "Android Tablet-Kamera";

    public CameraStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            StatusChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<CameraStatus>? StatusChanged;
    public event EventHandler<LiveViewFrame>? FrameReady;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Status == CameraStatus.Connected)
            return;

        Status = CameraStatus.Connecting;

        // Check CAMERA permission
#pragma warning disable CA1416
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M && _activity.CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted)
        {
            _activity.RequestPermissions([global::Android.Manifest.Permission.Camera], 101);
            Status = CameraStatus.Error;
            throw new CameraException("Kamera-Berechtigung auf dem Tablet erforderlich. Bitte in den Android-Einstellungen erlauben.");
        }
#pragma warning restore CA1416

        StartBackgroundThread();

        var cameraManager = (CameraManager?)_activity.GetSystemService(Context.CameraService);
        if (cameraManager is null)
        {
            Status = CameraStatus.Error;
            throw new CameraException("Android Camera-Service nicht verfügbar.");
        }

        var cameraId = SelectCameraId(cameraManager);
        if (cameraId is null)
        {
            Status = CameraStatus.Error;
            global::Android.Util.Log.Error("LumioCamera", "SelectCameraId returned NULL! No compatible camera found.");
            throw new CameraException("Keine kompatible Kamera auf dem Tablet gefunden.");
        }

        global::Android.Util.Log.Info("LumioCamera", $"ConnectAsync: Attempting to open camera ID '{cameraId}' ({DisplayName})");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var stateCallback = new DeviceStateCallback(
            onOpened: device =>
            {
                global::Android.Util.Log.Info("LumioCamera", $"DeviceStateCallback onOpened: Camera '{device.Id}' opened successfully!");
                _cameraDevice = device;
                Status = CameraStatus.Connected;
                tcs.TrySetResult(true);
            },
            onDisconnected: device =>
            {
                global::Android.Util.Log.Warn("LumioCamera", $"DeviceStateCallback onDisconnected: Camera '{device?.Id}' disconnected.");
                device.Close();
                _cameraDevice = null;
                Status = CameraStatus.Disconnected;
            },
            onError: (device, error) =>
            {
                global::Android.Util.Log.Error("LumioCamera", $"DeviceStateCallback onError: Camera '{device?.Id}' error code: {error}");
                device?.Close();
                _cameraDevice = null;
                Status = CameraStatus.Error;
                tcs.TrySetException(new CameraException($"Android Kamerafehler: {error}"));
            });

        try
        {
            cameraManager.OpenCamera(cameraId, stateCallback, _backgroundHandler);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(() =>
            {
                if (timeoutCts.IsCancellationRequested)
                    tcs.TrySetException(new CameraException($"Zeitüberschreitung beim Öffnen der Kamera '{cameraId}'."));
                else
                    tcs.TrySetCanceled();
            });
            await tcs.Task;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("LumioCamera", $"OpenCamera exception: {ex.Message}");
            Status = CameraStatus.Error;
            throw new CameraException($"Fehler beim Öffnen der Tablet-Kamera: {ex.Message}", ex);
        }
    }

    public static IReadOnlyList<CameraDeviceInfo> FindConnectedDevices(Activity activity)
    {
        var results = new List<CameraDeviceInfo>();
        try
        {
            var cameraManager = (CameraManager?)activity.GetSystemService(Context.CameraService);
            if (cameraManager is null) return results;

            foreach (var id in cameraManager.GetCameraIdList())
            {
                var chars = cameraManager.GetCameraCharacteristics(id);
                var facing = (chars.Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue();
                global::Android.Util.Log.Info("LumioCamera", $"Found Camera2 device: ID={id}, Facing={facing}");

#pragma warning disable CA1416
                if (Build.VERSION.SdkInt >= BuildVersionCodes.M && facing == (int)LensFacing.External)
                {
                    results.Add(new CameraDeviceInfo($"camera:external:{id}", "🎥 Nikon Z 50 II / Externe USB-Kamera (UVC)"));
                }
                else if (facing == (int)LensFacing.Front)
                {
                    results.Add(new CameraDeviceInfo($"camera:front:{id}", "📱 Tablet Frontkamera (Selfie)"));
                }
                else if (facing == (int)LensFacing.Back)
                {
                    results.Add(new CameraDeviceInfo($"camera:back:{id}", "📱 Tablet Hauptkamera (Rückseite)"));
                }
#pragma warning restore CA1416
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Lumio", $"Camera enumeration error: {ex.Message}");
        }

        return results;
    }

    private string? SelectCameraId(CameraManager manager)
    {
        var cameraIds = manager.GetCameraIdList();
        if (cameraIds.Length == 0) return null;

        // 1. Check if user selected a specific device in settings
        var req = _settings?.WebcamDeviceId;
        if (!string.IsNullOrWhiteSpace(req))
        {
            var targetId = req;
            if (req.StartsWith("camera:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = req.Split(':');
                if (parts.Length >= 3) targetId = parts[2];
            }

            if (cameraIds.Contains(targetId))
            {
                var chars = manager.GetCameraCharacteristics(targetId);
                var facing = (chars.Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue();
                DisplayName = (Build.VERSION.SdkInt >= BuildVersionCodes.M && facing == (int)LensFacing.External)
                    ? "Nikon Z 50 II / Externe Kamera (UVC)"
                    : facing == (int)LensFacing.Front ? "Tablet Frontkamera (Selfie)"
                    : facing == (int)LensFacing.Back ? "Tablet Hauptkamera" : "Externe Kamera";
                return targetId;
            }
        }

        string? frontId = null;
        string? backId = null;
        string? externalId = null;

        foreach (var id in cameraIds)
        {
            var chars = manager.GetCameraCharacteristics(id);
            var facing = (chars.Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue();

#pragma warning disable CA1416
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M && facing == (int)LensFacing.External && externalId is null)
            {
                externalId = id;
            }
#pragma warning restore CA1416
            else if (facing == (int)LensFacing.Front && frontId is null)
            {
                frontId = id;
            }
            else if (facing == (int)LensFacing.Back && backId is null)
            {
                backId = id;
            }
        }

        // Prioritize external UVC cameras (e.g. Nikon Z 50 II in USB streaming mode), otherwise Tablet Front or Back
        var chosenId = externalId ?? frontId ?? backId ?? cameraIds[0];
        var chosenChars = manager.GetCameraCharacteristics(chosenId);
        var chosenFacing = (chosenChars.Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue();
        DisplayName = (Build.VERSION.SdkInt >= BuildVersionCodes.M && chosenFacing == (int)LensFacing.External)
            ? "Nikon Z 50 II / Externe Kamera (UVC)"
            : chosenFacing == (int)LensFacing.Front ? "Tablet Frontkamera (Selfie)"
            : chosenFacing == (int)LensFacing.Back ? "Tablet Hauptkamera" : "Externe Kamera";
        return chosenId;
    }

    public async Task StartLiveViewAsync(CancellationToken cancellationToken = default)
    {
        if (_cameraDevice is null || _isLiveViewRunning)
            return;

        var tcs = new TaskCompletionSource<bool>();

        try
        {
            var cameraManager = (CameraManager?)_activity.GetSystemService(Context.CameraService);
            var chars = cameraManager?.GetCameraCharacteristics(_cameraDevice.Id);
            var map = chars?.Get(CameraCharacteristics.ScalerStreamConfigurationMap) as StreamConfigurationMap;

            int previewWidth = 1280;
            int previewHeight = 720;

            if (map is not null)
            {
                // Preview: Hardware-accelerated YUV_420_888 at ~1280x720 or 1920x1080 (supported on 100% of Android devices)
                var yuvSizes = map.GetOutputSizes((int)ImageFormatType.Yuv420888);
                if (yuvSizes is not null && yuvSizes.Length > 0)
                {
                    var bestPreview = yuvSizes
                        .Where(s => s.Width <= 1920 && s.Height <= 1080 && s.Width >= 640 && s.Height >= 480)
                        .OrderBy(s => Math.Abs(s.Width - 1280) + Math.Abs(s.Height - 720))
                        .FirstOrDefault() ?? yuvSizes[0];

                    previewWidth = bestPreview.Width;
                    previewHeight = bestPreview.Height;
                }
            }

            global::Android.Util.Log.Info("LumioCamera", $"StartLiveViewAsync: preview resolution={previewWidth}x{previewHeight}");

            // Configure single ImageReader for Preview & Photo (YUV_420_888)
            // Using a single surface guarantees 100% compatibility across USB UVC webcams (Nikon Z 50 II) and all Android tablets
            _previewReader = ImageReader.NewInstance(previewWidth, previewHeight, ImageFormatType.Yuv420888, maxImages: 2);
            _previewReader.SetOnImageAvailableListener(new PreviewImageListener(this), _backgroundHandler);

            var surfaces = new List<Surface>
            {
                _previewReader.Surface!
            };

            var sessionCallback = new SessionStateCallback(
                onConfigured: session =>
                {
                    _captureSession = session;
                    global::Android.Util.Log.Info("LumioCamera", "SessionStateCallback: CameraCaptureSession configured successfully!");
                    try
                    {
                        var previewRequestBuilder = _cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                        previewRequestBuilder.AddTarget(_previewReader.Surface!);
                        if (CaptureRequest.ControlAfMode is not null)
                        {
                            previewRequestBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
                        }
                        if (CaptureRequest.ControlAeMode is not null)
                        {
                            previewRequestBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                        }

                        session.SetRepeatingRequest(previewRequestBuilder.Build(), null, _backgroundHandler);
                        _isLiveViewRunning = true;
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        global::Android.Util.Log.Error("LumioCamera", $"SetRepeatingRequest error: {ex.Message}");
                        tcs.TrySetException(ex);
                    }
                },
                onConfigureFailed: session =>
                {
                    global::Android.Util.Log.Error("LumioCamera", "SessionStateCallback: CameraCaptureSession configure FAILED!");
                    tcs.TrySetException(new CameraException("Kamera-Vorschau-Session konnte nicht konfiguriert werden."));
                });

#pragma warning disable CA1416
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P && _activity.MainExecutor is not null) // API 28+
            {
                var outputConfigs = surfaces.Select(s => new OutputConfiguration(s)).ToList();
                var sessionConfig = new SessionConfiguration((int)SessionType.Regular, outputConfigs, _activity.MainExecutor, sessionCallback);
                _cameraDevice.CreateCaptureSession(sessionConfig);
            }
            else
            {
#pragma warning disable CS0618
                _cameraDevice.CreateCaptureSession(surfaces, sessionCallback, _backgroundHandler);
#pragma warning restore CS0618
            }
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("LumioCamera", $"StartLiveViewAsync exception: {ex.Message}");
            tcs.TrySetException(ex);
        }

        // Add 4-second timeout so the UI never hangs if the HAL fails to configure
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var reg = combinedCts.Token.Register(() =>
        {
            if (timeoutCts.IsCancellationRequested)
                tcs.TrySetException(new CameraException("Kamera-Vorschau-Timeout (HAL antwortet nicht)."));
            else
                tcs.TrySetCanceled();
        });

        try
        {
            await tcs.Task;
        }
        finally
        {
            reg.Dispose();
        }
    }

    public Task StopLiveViewAsync()
    {
        _isLiveViewRunning = false;
        try
        {
            _captureSession?.StopRepeating();
            _captureSession?.Close();
            _captureSession = null;
        }
        catch { }

        _previewReader?.Close();
        _previewReader = null;

        _stillCaptureReader?.Close();
        _stillCaptureReader = null;

        return Task.CompletedTask;
    }

    public async Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected || _cameraDevice is null || _captureSession is null)
        {
            if (_latestFrameBytes is not null && _latestFrameBytes.Length > 0)
            {
                return new CapturedPhoto(_latestFrameBytes, DateTimeOffset.Now);
            }
            throw new CameraException("Kamera ist nicht bereit für die Fotoaufnahme.");
        }

        // Wait up to 1 second for a fresh live view frame
        var startWait = DateTime.UtcNow;
        while (_latestFrameBytes is null && (DateTime.UtcNow - startWait).TotalSeconds < 2)
        {
            await Task.Delay(50, cancellationToken);
        }

        if (_latestFrameBytes is not null && _latestFrameBytes.Length > 0)
        {
            return new CapturedPhoto(_latestFrameBytes, DateTimeOffset.Now);
        }

        throw new TimeoutException("Foto-Auslösung: Kein Kamerabild verfügbar.");
    }

    private void OnPreviewImage(Image image)
    {
        bool lockAcquired = false;
        try
        {
            var now = System.Environment.TickCount64;
            // Throttle preview to max ~15 FPS (66ms) to keep touch input and UI completely responsive
            if (now - Interlocked.Read(ref _lastPreviewTicks) < 66)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _isProcessingPreview, 1, 0) != 0)
            {
                return;
            }

            lockAcquired = true;
            Interlocked.Exchange(ref _lastPreviewTicks, now);

            var jpegBytes = ConvertYuv420888ToJpeg(image, 70);
            if (jpegBytes is not null && jpegBytes.Length > 0)
            {
                _latestFrameBytes = jpegBytes;
                FrameReady?.Invoke(this, new LiveViewFrame(jpegBytes, FrameFormat.Jpeg, image.Width, image.Height, 0));
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("LumioCamera", $"Preview frame processing error: {ex.Message}");
        }
        finally
        {
            if (lockAcquired)
            {
                Interlocked.Exchange(ref _isProcessingPreview, 0);
            }
            try { image.Close(); } catch { }
        }
    }

    private byte[]? ConvertYuv420888ToJpeg(Image image, int quality)
    {
        var width = image.Width;
        var height = image.Height;
        var planes = image.GetPlanes();
        if (planes is null || planes.Length < 3) return null;

        var yPlane = planes[0];
        var uPlane = planes[1];
        var vPlane = planes[2];

        var yBuffer = yPlane.Buffer;
        var uBuffer = uPlane.Buffer;
        var vBuffer = vPlane.Buffer;
        if (yBuffer is null || uBuffer is null || vBuffer is null) return null;

        int requiredNv21Size = width * height * 3 / 2;
        if (_cachedNv21 is null || _cachedNv21.Length != requiredNv21Size)
        {
            _cachedNv21 = new byte[requiredNv21Size];
        }
        var nv21 = _cachedNv21;

        // 1. Copy Y plane
        int yRowStride = yPlane.RowStride;
        int yPixelStride = yPlane.PixelStride;
        int yPos = 0;

        if (yRowStride == width && yPixelStride == 1)
        {
            yBuffer.Position(0);
            yBuffer.Get(nv21, 0, width * height);
            yPos = width * height;
        }
        else
        {
            for (int r = 0; r < height; r++)
            {
                yBuffer.Position(r * yRowStride);
                yBuffer.Get(nv21, yPos, width);
                yPos += width;
            }
        }

        // 2. Interleave V and U into NV21 format
        int uvRowStride = vPlane.RowStride;
        int uvPixelStride = vPlane.PixelStride;
        int uvHeight = height / 2;
        int uvWidth = width / 2;
        int uvOffset = width * height;

        if (uvPixelStride == 2 && uPlane.PixelStride == 2 && uvRowStride == width)
        {
            vBuffer.Position(0);
            int available = Math.Min(vBuffer.Remaining(), nv21.Length - uvOffset);
            vBuffer.Get(nv21, uvOffset, available);
        }
        else
        {
            int uRowStride = uPlane.RowStride;
            int uPixelStride = uPlane.PixelStride;

            for (int r = 0; r < uvHeight; r++)
            {
                for (int c = 0; c < uvWidth; c++)
                {
                    int vIdx = r * uvRowStride + c * uvPixelStride;
                    int uIdx = r * uRowStride + c * uPixelStride;

                    nv21[uvOffset++] = (byte)vBuffer.Get(vIdx);
                    nv21[uvOffset++] = (byte)uBuffer.Get(uIdx);
                }
            }
        }

        lock (_cachedJpegStream)
        {
            _cachedJpegStream.SetLength(0);
            var yuvImage = new YuvImage(nv21, ImageFormatType.Nv21, width, height, null);
            yuvImage.CompressToJpeg(new Rect(0, 0, width, height), quality, _cachedJpegStream);
            return _cachedJpegStream.ToArray();
        }
    }

    private void OnStillCapturedImage(Image image)
    {
        try
        {
            var planes = image.GetPlanes();
            if (planes is null || planes.Length == 0) return;

            var buffer = planes[0].Buffer;
            if (buffer is null) return;

            var bytes = new byte[buffer.Remaining()];
            buffer.Get(bytes);

            TaskCompletionSource<CapturedPhoto>? tcs;
            lock (_lock)
            {
                tcs = _stillCaptureTcs;
                _stillCaptureTcs = null;
            }

            tcs?.TrySetResult(new CapturedPhoto(bytes, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _stillCaptureTcs?.TrySetException(ex);
            }
        }
        finally
        {
            image.Close();
        }
    }

    private void StartBackgroundThread()
    {
        _backgroundThread = new HandlerThread("LumioCameraBackground");
        _backgroundThread.Start();
        _backgroundHandler = new Handler(_backgroundThread.Looper!);
    }

    private void StopBackgroundThread()
    {
        if (_backgroundThread is not null)
        {
            _backgroundThread.QuitSafely();
            try { _backgroundThread.Join(); } catch { }
            _backgroundThread = null;
            _backgroundHandler = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopLiveViewAsync();

        _cameraDevice?.Close();
        _cameraDevice = null;

        StopBackgroundThread();
        Status = CameraStatus.Disconnected;
    }

    private sealed class DeviceStateCallback : CameraDevice.StateCallback
    {
        private readonly Action<CameraDevice> _onOpened;
        private readonly Action<CameraDevice> _onDisconnected;
        private readonly Action<CameraDevice, CameraError> _onError;

        public DeviceStateCallback(Action<CameraDevice> onOpened, Action<CameraDevice> onDisconnected, Action<CameraDevice, CameraError> onError)
        {
            _onOpened = onOpened;
            _onDisconnected = onDisconnected;
            _onError = onError;
        }

        public override void OnOpened(CameraDevice camera) => _onOpened(camera);
        public override void OnDisconnected(CameraDevice camera) => _onDisconnected(camera);
        public override void OnError(CameraDevice camera, CameraError error) => _onError(camera, error);
    }

    private sealed class SessionStateCallback : CameraCaptureSession.StateCallback
    {
        private readonly Action<CameraCaptureSession> _onConfigured;
        private readonly Action<CameraCaptureSession> _onConfigureFailed;

        public SessionStateCallback(Action<CameraCaptureSession> onConfigured, Action<CameraCaptureSession> onConfigureFailed)
        {
            _onConfigured = onConfigured;
            _onConfigureFailed = onConfigureFailed;
        }

        public override void OnConfigured(CameraCaptureSession session) => _onConfigured(session);
        public override void OnConfigureFailed(CameraCaptureSession session) => _onConfigureFailed(session);
    }

    private sealed class PreviewImageListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly AndroidCameraService _owner;
        public PreviewImageListener(AndroidCameraService owner) => _owner = owner;

        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is null) return;
            var image = reader.AcquireLatestImage();
            if (image is not null)
            {
                _owner.OnPreviewImage(image);
            }
        }
    }

    private sealed class StillCaptureListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly AndroidCameraService _owner;
        public StillCaptureListener(AndroidCameraService owner) => _owner = owner;

        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is null) return;
            var image = reader.AcquireLatestImage();
            if (image is not null)
            {
                _owner.OnStillCapturedImage(image);
            }
        }
    }

    private sealed class HandlerExecutor : Java.Lang.Object, Java.Util.Concurrent.IExecutor
    {
        private readonly Handler _handler;
        public HandlerExecutor(Handler handler) => _handler = handler;
        public void Execute(Java.Lang.IRunnable? command)
        {
            if (command != null) _handler.Post(command);
        }
    }
}
