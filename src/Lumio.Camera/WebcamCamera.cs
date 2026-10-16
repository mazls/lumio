using System.Runtime.InteropServices.WindowsRuntime;
using Lumio.Core.Camera;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Lumio.Camera;

/// <summary>Webcam via Windows.Media.Capture (Media Foundation). Works on x64 and ARM64.</summary>
public sealed class WebcamCamera : ICameraService
{
    private const int MaxPreviewWidth = 1920;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    private readonly string? _requestedDeviceId;
    private readonly object _frameLock = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private TaskCompletionSource<LiveViewFrame>? _nextFrame;
    private bool _liveViewRunning;
    private CameraStatus _status;

    public WebcamCamera(string? deviceId)
    {
        _requestedDeviceId = deviceId;
    }

    public string DisplayName { get; private set; } = "Webcam";

    public CameraStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            StatusChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<CameraStatus>? StatusChanged;

    public event EventHandler<LiveViewFrame>? FrameReady;

    public static async Task<IReadOnlyList<CameraDeviceInfo>> FindDevicesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.Select(d =>
        {
            var name = d.Name;
            var panel = d.EnclosureLocation?.Panel;
            if (panel == Windows.Devices.Enumeration.Panel.Front && !name.Contains("Front", StringComparison.OrdinalIgnoreCase))
                name += " (Frontkamera)";
            else if (panel == Windows.Devices.Enumeration.Panel.Back && !name.Contains("Back", StringComparison.OrdinalIgnoreCase) && !name.Contains("Rear", StringComparison.OrdinalIgnoreCase))
                name += " (Rückkamera)";

            return new CameraDeviceInfo(d.Id, name);
        }).ToList();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Status == CameraStatus.Connected)
            return;

        Status = CameraStatus.Connecting;
        try
        {
            var devices = await FindDevicesAsync();
            var device = devices.FirstOrDefault(d => d.Id == _requestedDeviceId) ?? devices.FirstOrDefault()
                         ?? throw new CameraException("Keine Webcam gefunden.");
            DisplayName = device.Name;

            _capture = new MediaCapture();
            _capture.Failed += OnCaptureFailed;
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            }).AsTask(cancellationToken);

            var source = SelectColorSource(_capture);
            var format = SelectFormat(source);
            if (format is not null)
                await source.SetFormatAsync(format).AsTask(cancellationToken);

            _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask(cancellationToken);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += OnFrameArrived;

            Status = CameraStatus.Connected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CleanupAsync().ConfigureAwait(false);
            Status = CameraStatus.Error;
            throw ex as CameraException
                  ?? new CameraException($"Webcam konnte nicht gestartet werden ({ex.Message.Trim()})", ex);
        }
    }

    public async Task StartLiveViewAsync(CancellationToken cancellationToken = default)
    {
        var reader = _reader ?? throw new CameraException("Webcam ist nicht verbunden.");
        if (_liveViewRunning)
            return;

        var result = await reader.StartAsync().AsTask(cancellationToken);
        if (result != MediaFrameReaderStartStatus.Success)
            throw new CameraException($"Live-Vorschau konnte nicht gestartet werden ({result}).");

        _liveViewRunning = true;
    }

    public async Task StopLiveViewAsync()
    {
        if (!_liveViewRunning || _reader is null)
            return;

        _liveViewRunning = false;
        await _reader.StopAsync();
    }

    public async Task<Lumio.Core.Camera.CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        var wasRunning = _liveViewRunning;
        if (!wasRunning)
            await StartLiveViewAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Always use a fresh frame taken after the trigger, not one from the countdown.
            LiveViewFrame frame;
            try
            {
                frame = await WaitForNextFrameAsync().WaitAsync(FrameTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new CameraException("Die Webcam liefert keine Bilder.", ex);
            }

            var jpeg = await Task.Run(() => JpegEncoder.EncodeBgra(frame), cancellationToken).ConfigureAwait(false);
            return new Lumio.Core.Camera.CapturedPhoto(jpeg, DateTimeOffset.Now);
        }
        finally
        {
            if (!wasRunning)
                await StopLiveViewAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync().ConfigureAwait(false);
        Status = CameraStatus.Disconnected;
    }

    public Task ApplySettingsAsync(Lumio.Core.Settings.DslrCameraSettings settings, CancellationToken cancellationToken = default)
    {
        if (_capture?.VideoDeviceController is null)
            return Task.CompletedTask;

        try
        {
            var controller = _capture.VideoDeviceController;

            // Exposure / Belichtung
            if (controller.Exposure.Capabilities.Supported)
            {
                if (settings.ShutterSpeed.StartsWith("Auto", StringComparison.OrdinalIgnoreCase) || settings.ExposureCompensation.StartsWith("0", StringComparison.OrdinalIgnoreCase))
                {
                    controller.Exposure.TrySetAuto(true);
                }
                else
                {
                    var step = controller.Exposure.Capabilities.Step;
                    var min = controller.Exposure.Capabilities.Min;
                    var max = controller.Exposure.Capabilities.Max;
                    var factor = settings.ExposureCompensation.Contains("+") ? 1.0 : (settings.ExposureCompensation.Contains("-") ? -1.0 : 0.0);
                    controller.Exposure.TrySetValue(Math.Clamp(factor * step * 2, min, max));
                }
            }

            // White Balance / Weißabgleich
            if (controller.WhiteBalance.Capabilities.Supported)
            {
                if (settings.WhiteBalance.StartsWith("Auto", StringComparison.OrdinalIgnoreCase))
                {
                    controller.WhiteBalance.TrySetAuto(true);
                }
                else
                {
                    uint temp = settings.WhiteBalance switch
                    {
                        var s when s.Contains("Kunstlicht") => 3200,
                        var s when s.Contains("Leuchtstoff") => 4000,
                        var s when s.Contains("Tageslicht") => 5400,
                        var s when s.Contains("Blitz") => 5600,
                        var s when s.Contains("Bewölkt") => 6500,
                        var s when s.Contains("Schatten") => 7500,
                        _ => 5400
                    };
                    controller.WhiteBalance.TrySetValue(temp);
                }
            }
        }
        catch
        {
            // Ignore devices without manual controller support
        }

        return Task.CompletedTask;
    }

    private static MediaFrameSource SelectColorSource(MediaCapture capture)
    {
        var colorSources = capture.FrameSources.Values
            .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            .ToList();

        return colorSources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
               ?? colorSources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
               ?? colorSources.FirstOrDefault()
               ?? throw new CameraException("Die Webcam liefert kein Farbbild.");
    }

    private static MediaFrameFormat? SelectFormat(MediaFrameSource source) =>
        source.SupportedFormats
            .Where(f => f.VideoFormat.Width <= MaxPreviewWidth && FrameRate(f) >= 15)
            .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
            .ThenByDescending(FrameRate)
            .FirstOrDefault();

    private static double FrameRate(MediaFrameFormat format) =>
        format.FrameRate.Denominator == 0 ? 0 : (double)format.FrameRate.Numerator / format.FrameRate.Denominator;

    private Task<LiveViewFrame> WaitForNextFrameAsync()
    {
        lock (_frameLock)
        {
            _nextFrame ??= new TaskCompletionSource<LiveViewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _nextFrame.Task;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frameReference = sender.TryAcquireLatestFrame();
        var bitmap = frameReference?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
            return;

        SoftwareBitmap? converted = null;
        try
        {
            var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? bitmap
                : converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

            var width = bgra.PixelWidth;
            var height = bgra.PixelHeight;
            var data = new byte[width * height * 4];
            bgra.CopyToBuffer(data.AsBuffer());

            var frame = new LiveViewFrame(data, FrameFormat.Bgra32, width, height, width * 4);

            TaskCompletionSource<LiveViewFrame>? waiter;
            lock (_frameLock)
            {
                waiter = _nextFrame;
                _nextFrame = null;
            }

            waiter?.TrySetResult(frame);
            FrameReady?.Invoke(this, frame);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        Status = CameraStatus.Error;
        lock (_frameLock)
        {
            _nextFrame?.TrySetException(new CameraException($"Webcam-Fehler: {args.Message}"));
            _nextFrame = null;
        }
    }

    private async Task CleanupAsync()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            if (_liveViewRunning)
            {
                _liveViewRunning = false;
                await _reader.StopAsync();
            }

            _reader.Dispose();
            _reader = null;
        }

        if (_capture is not null)
        {
            _capture.Failed -= OnCaptureFailed;
            _capture.Dispose();
            _capture = null;
        }
    }
}
