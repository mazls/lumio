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
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                PhotoCaptureSource = PhotoCaptureSource.Photo,
            };

            if (!string.IsNullOrEmpty(_requestedDeviceId))
                settings.VideoDeviceId = _requestedDeviceId;

            var capture = new MediaCapture();
            capture.Failed += OnCaptureFailed;

            await capture.InitializeAsync(settings);

            var frameSource = FindColorFrameSource(capture);
            if (frameSource is null)
                throw new CameraException("Keine kompatible Videoquelle für Live-View gefunden.");

            var reader = await capture.CreateFrameReaderAsync(frameSource, MediaEncodingSubtypes.Bgra8);
            reader.FrameArrived += OnFrameArrived;

            _capture = capture;
            _reader = reader;

            var devices = await FindDevicesAsync();
            var info = devices.FirstOrDefault(d => d.Id == _requestedDeviceId);
            DisplayName = info?.Name ?? "Webcam";

            Status = CameraStatus.Connected;
        }
        catch (CameraException)
        {
            await CleanupAsync();
            Status = CameraStatus.Error;
            throw;
        }
        catch (Exception ex)
        {
            await CleanupAsync();
            Status = CameraStatus.Error;
            throw new CameraException($"Webcam konnte nicht initialisiert werden: {ex.Message}", ex);
        }
    }

    public async Task StartLiveViewAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected)
            throw new CameraException("Kamera ist nicht verbunden.");

        if (_reader is null || _liveViewRunning)
            return;

        var status = await _reader.StartAsync();
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

        if (!_liveViewRunning)
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
            var jpeg = JpegEncoder.EncodeBgra(frame, 95);
            return new CapturedPhoto(jpeg, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CameraException("Timeout beim Warten auf ein Kamerabild.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
        Status = CameraStatus.Disconnected;
    }

    private static MediaFrameSource? FindColorFrameSource(MediaCapture capture)
    {
        foreach (var source in capture.FrameSources.Values)
        {
            if (source.Info.SourceKind == MediaFrameSourceKind.Color)
                return source;
        }

        return capture.FrameSources.Values.FirstOrDefault();
    }

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
            if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                converted = SoftwareBitmap.Convert(
                    softwareBitmap,
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
            }

            var target = converted ?? softwareBitmap;
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
