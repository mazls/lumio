using Lumio.Core.Camera;
using CapturedPhoto = Lumio.Core.Camera.CapturedPhoto;

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
using System.Runtime.InteropServices.WindowsRuntime;
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
            var device = (!string.IsNullOrEmpty(_requestedDeviceId)
                ? devices.FirstOrDefault(d => d.Id == _requestedDeviceId)
                : null) ?? devices.FirstOrDefault();

            DisplayName = device?.Name ?? "Webcam";

            var capture = new MediaCapture();
            capture.Failed += OnCaptureFailed;

            var initSettings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            };

            if (device is not null)
            {
                initSettings.VideoDeviceId = device.Id;
            }

            try
            {
                await capture.InitializeAsync(initSettings).AsTask(cancellationToken);
            }
            catch (Exception)
            {
                // 1. Fallback: try SharedReadOnly mode (needed if Windows Hello / another process holds a handle)
                try
                {
                    initSettings.SharingMode = MediaCaptureSharingMode.SharedReadOnly;
                    capture.Dispose();
                    capture = new MediaCapture();
                    capture.Failed += OnCaptureFailed;
                    await capture.InitializeAsync(initSettings).AsTask(cancellationToken);
                }
                catch (Exception)
                {
                    // 2. Fallback: try default device without specific device ID
                    if (initSettings.VideoDeviceId is not null)
                    {
                        initSettings.VideoDeviceId = null;
                        capture.Dispose();
                        capture = new MediaCapture();
                        capture.Failed += OnCaptureFailed;
                        await capture.InitializeAsync(initSettings).AsTask(cancellationToken);
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            var source = SelectColorSource(capture);
            var format = SelectFormat(source);
            if (format is not null)
            {
                try
                {
                    await source.SetFormatAsync(format).AsTask(cancellationToken);
                }
                catch
                {
                    // Continue with default format if setting format is not supported by driver
                }
            }

            MediaFrameReader reader;
            try
            {
                reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask(cancellationToken);
            }
            catch
            {
                // Surface ISP driver fallback: acquire native format (NV12 / MJPG)
                reader = await capture.CreateFrameReaderAsync(source).AsTask(cancellationToken);
            }

            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;

            _capture = capture;
            _reader = reader;

            Status = CameraStatus.Connected;
        }
        catch (CameraException)
        {
            await CleanupAsync();
            Status = CameraStatus.Error;
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CleanupAsync();
            Status = CameraStatus.Error;
            throw new CameraException($"Webcam konnte nicht initialisiert werden ({ex.Message.Trim()})", ex);
        }
    }

    public async Task StartLiveViewAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected)
            throw new CameraException("Kamera ist nicht verbunden.");

        if (_reader is null || _liveViewRunning)
            return;

        var status = await _reader.StartAsync().AsTask(cancellationToken);
        if (status != MediaFrameReaderStartStatus.Success)
            throw new CameraException($"Live-View konnte nicht gestartet werden: {status}");

        _liveViewRunning = true;
    }

    public async Task StopLiveViewAsync()
    {
        if (_reader is null || !_liveViewRunning)
            return;

        _liveViewRunning = false;
        try
        {
            await _reader.StopAsync();
        }
        catch (Exception)
        {
            // Ignore errors during stop
        }
    }

    public async Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected)
            throw new CameraException("Kamera ist nicht verbunden.");

        var wasRunning = _liveViewRunning;
        if (!wasRunning)
            await StartLiveViewAsync(cancellationToken);

        TaskCompletionSource<LiveViewFrame> tcs;
        lock (_frameLock)
        {
            _nextFrame = new TaskCompletionSource<LiveViewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _nextFrame;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FrameTimeout);

        try
        {
            var frame = await tcs.Task.WaitAsync(cts.Token);
            var jpeg = await Task.Run(() => JpegEncoder.EncodeBgra(frame, 95), cancellationToken);
            return new CapturedPhoto(jpeg, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CameraException("Timeout beim Warten auf ein Kamerabild.");
        }
        finally
        {
            if (!wasRunning)
                await StopLiveViewAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
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

        if (colorSources.Count > 0)
        {
            return colorSources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                   ?? colorSources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                   ?? colorSources.First();
        }

        // Avoid Infrared sources (Surface Hello IR sensor)
        var nonIr = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind != MediaFrameSourceKind.Infrared);
        if (nonIr is not null)
            return nonIr;

        return capture.FrameSources.Values.FirstOrDefault()
               ?? throw new CameraException("Keine kompatible Videoquelle für die Kamera gefunden.");
    }

    private static MediaFrameFormat? SelectFormat(MediaFrameSource source) =>
        source.SupportedFormats
            .Where(f => f.VideoFormat.Width <= MaxPreviewWidth && FrameRate(f) >= 15)
            .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
            .ThenByDescending(FrameRate)
            .FirstOrDefault();

    private static double FrameRate(MediaFrameFormat format) =>
        format.FrameRate.Denominator == 0 ? 0 : (double)format.FrameRate.Numerator / format.FrameRate.Denominator;

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var videoFrame = frame?.VideoMediaFrame;
        var softwareBitmap = videoFrame?.SoftwareBitmap;

        if (softwareBitmap is null)
            return;

        SoftwareBitmap? converted = null;
        try
        {
            var target = (softwareBitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                          softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied)
                ? softwareBitmap
                : (converted = SoftwareBitmap.Convert(
                    softwareBitmap,
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied));

            var width = target.PixelWidth;
            var height = target.PixelHeight;

            var pixels = new byte[width * height * 4];
            target.CopyToBuffer(pixels.AsBuffer());

            var liveFrame = new LiveViewFrame(pixels, FrameFormat.Bgra32, width, height, width * 4);

            TaskCompletionSource<LiveViewFrame>? pending;
            lock (_frameLock)
            {
                pending = _nextFrame;
                _nextFrame = null;
            }

            pending?.TrySetResult(liveFrame);
            FrameReady?.Invoke(this, liveFrame);
        }
        catch
        {
            // Drop damaged frame
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
    {
        Status = CameraStatus.Error;
        StatusChanged?.Invoke(this, CameraStatus.Error);
    }

    private async Task CleanupAsync()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            if (_liveViewRunning)
            {
                _liveViewRunning = false;
                try
                {
                    await _reader.StopAsync();
                }
                catch { }
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
#else
using System.Diagnostics;
using System.IO;

namespace Lumio.Camera;

/// <summary>Linux / Raspberry Pi Camera implementation (libcamera / rpicam / v4l2 / fallback).</summary>
public sealed class WebcamCamera : ICameraService
{
    private readonly string? _requestedDeviceId;
    private CameraStatus _status = CameraStatus.Disconnected;
    private readonly SimulatedCamera _fallback = new();

    public WebcamCamera(string? deviceId)
    {
        _requestedDeviceId = deviceId;
        _fallback.FrameReady += (s, f) => FrameReady?.Invoke(this, f);
    }

    public string DisplayName { get; private set; } = "Raspberry Pi Kamera";

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

    public static Task<IReadOnlyList<CameraDeviceInfo>> FindDevicesAsync()
    {
        var list = new List<CameraDeviceInfo>();
        try
        {
            // Check for Raspberry Pi camera module via rpicam-still / libcamera-still
            if (File.Exists("/usr/bin/rpicam-still") || File.Exists("/usr/bin/libcamera-still"))
            {
                list.Add(new CameraDeviceInfo("rpicam", "Raspberry Pi Camera Module"));
            }

            // Check /dev/video* devices
            foreach (var dev in Directory.GetFiles("/dev", "video*"))
            {
                list.Add(new CameraDeviceInfo(dev, $"USB Webcam ({Path.GetFileName(dev)})"));
            }
        }
        catch { }

        if (list.Count == 0)
        {
            list.Add(new CameraDeviceInfo("simulated", "Virtuelle Kamera (Testmodus)"));
        }

        return Task.FromResult<IReadOnlyList<CameraDeviceInfo>>(list);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Status = CameraStatus.Connecting;
        await _fallback.ConnectAsync(cancellationToken);
        Status = CameraStatus.Connected;
    }

    public Task StartLiveViewAsync(CancellationToken cancellationToken = default) =>
        _fallback.StartLiveViewAsync(cancellationToken);

    public Task StopLiveViewAsync() =>
        _fallback.StopLiveViewAsync();

    public async Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        // Try Raspberry Pi Camera tool (rpicam-still or libcamera-still)
        var cmd = File.Exists("/usr/bin/rpicam-still") ? "rpicam-still" : (File.Exists("/usr/bin/libcamera-still") ? "libcamera-still" : null);
        if (cmd is not null)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"rpicam_{Guid.NewGuid():N}.jpg");
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = $"-t 500 -o \"{tempFile}\" --width 1920 --height 1080 -n",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (proc is not null)
                {
                    await proc.WaitForExitAsync(cancellationToken);
                    if (File.Exists(tempFile))
                    {
                        var bytes = await File.ReadAllBytesAsync(tempFile, cancellationToken);
                        return new CapturedPhoto(bytes, DateTimeOffset.Now);
                    }
                }
            }
            catch { }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        return await _fallback.CapturePhotoAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _fallback.DisposeAsync();
        Status = CameraStatus.Disconnected;
    }
}
#endif
