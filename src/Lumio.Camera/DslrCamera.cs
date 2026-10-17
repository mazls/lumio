using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Lumio.Core.Camera;
using Lumio.Core.Settings;

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
using System.Management;
using CameraControl.Devices;
using CameraControl.Devices.Classes;
#endif

namespace Lumio.Camera;

/// <summary>
/// Universal DSLR and Mirrorless camera driver supporting Nikon, Canon, Sony, Fuji, etc.
/// - Windows: In-process direct USB tethering engine via WPD / PTP (just like dslrBooth, zero external software)
/// - Linux: gphoto2 CLI &amp; MJPEG movie streaming
/// </summary>
public class DslrCamera : ICameraService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
    private static CameraDeviceManager? _deviceManager;
    private static readonly object _managerLock = new();
    private ICameraDevice? _cameraDevice;
#endif

    private readonly BoothSettings? _settings;
    private readonly string _digiCamUrl;
    private CancellationTokenSource? _liveViewCts;
    private Task? _liveViewTask;
    private CameraStatus _status = CameraStatus.Disconnected;
    private bool _useDigiCamControl;

    public string DisplayName { get; private set; } = "DSLR / Systemkamera";

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

    public DslrCamera(BoothSettings? settings = null)
    {
        _settings = settings;
        _digiCamUrl = settings?.DigiCamControlUrl ?? BoothSettings.DefaultDigiCamControlUrl;
    }

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
    private static CameraDeviceManager GetOrCreateDeviceManager()
    {
        lock (_managerLock)
        {
            if (_deviceManager == null)
            {
                _deviceManager = new CameraDeviceManager("");
                _deviceManager.DisableNativeDrivers = false;
                _deviceManager.UseExperimentalDrivers = true;
            }
            return _deviceManager;
        }
    }
#endif

    public static async Task<IReadOnlyList<CameraDeviceInfo>> FindConnectedDevicesAsync(string? digiCamUrl = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return await FindWindowsDevicesAsync(digiCamUrl ?? BoothSettings.DefaultDigiCamControlUrl);
        }

        return await FindLinuxDevicesAsync();
    }

    private static async Task<IReadOnlyList<CameraDeviceInfo>> FindWindowsDevicesAsync(string digiCamUrl)
    {
        var results = new List<CameraDeviceInfo>();

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
        // 1. Check in-process CameraDeviceManager
        try
        {
            await Task.Run(() =>
            {
                var mgr = GetOrCreateDeviceManager();
                mgr.ConnectToCamera();

                foreach (ICameraDevice dev in mgr.ConnectedDevices)
                {
                    if (dev.IsConnected)
                    {
                        var name = !string.IsNullOrWhiteSpace(dev.DisplayName)
                            ? dev.DisplayName
                            : (!string.IsNullOrWhiteSpace(dev.DeviceName) ? dev.DeviceName : "DSLR Kamera");

                        var id = !string.IsNullOrWhiteSpace(dev.SerialNumber)
                            ? dev.SerialNumber
                            : (!string.IsNullOrWhiteSpace(dev.PortName) ? dev.PortName : Guid.NewGuid().ToString());

                        results.Add(new CameraDeviceInfo(id, $"📷 {name}"));
                    }
                }
            });

            if (results.Count > 0)
                return results;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"In-process CameraDeviceManager scan error: {ex.Message}");
        }

        // 2. Query Windows PnP for USB DSLRs (Nikon, Canon, Sony, Fuji)
        try
        {
            await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Name, Description, PNPClass FROM Win32_PnPEntity WHERE " +
                    "DeviceID LIKE '%VID_04B0%' OR " + // Nikon
                    "DeviceID LIKE '%VID_04A9%' OR " + // Canon
                    "DeviceID LIKE '%VID_054C%' OR " + // Sony
                    "DeviceID LIKE '%VID_04CB%' OR " + // Fuji
                    "Name LIKE '%Nikon%' OR Name LIKE '%Z50%'");

                foreach (var device in searcher.Get())
                {
                    var pnpClass = device["PNPClass"]?.ToString() ?? "";
                    var name = device["Name"]?.ToString() ?? device["Description"]?.ToString() ?? "DSLR Kamera";
                    var id = device["DeviceID"]?.ToString() ?? Guid.NewGuid().ToString();

                    if (pnpClass.Equals("Printer", StringComparison.OrdinalIgnoreCase) ||
                        pnpClass.Equals("PrintQueue", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Printer", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Drucker", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Inkjet", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Class Driver", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (name.Contains("Z50", StringComparison.OrdinalIgnoreCase) || id.Contains("VID_04B0&PID_0455", StringComparison.OrdinalIgnoreCase))
                    {
                        name = "Nikon Z 50 II (USB)";
                    }
                    else if (!name.StartsWith("Nikon", StringComparison.OrdinalIgnoreCase) && id.Contains("VID_04B0", StringComparison.OrdinalIgnoreCase))
                    {
                        name = $"Nikon Kamera ({name})";
                    }

                    results.Add(new CameraDeviceInfo(id, name));
                }
            });
        }
        catch { }
#endif

        return results;
    }

    private static async Task<IReadOnlyList<CameraDeviceInfo>> FindLinuxDevicesAsync()
    {
        return await Task.Run(() =>
        {
            var results = new List<CameraDeviceInfo>();
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "gphoto2",
                    Arguments = "--auto-detect",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (proc is not null)
                {
                    var lineNum = 0;
                    while (proc.StandardOutput.ReadLine() is { } line)
                    {
                        lineNum++;
                        if (lineNum <= 2 || string.IsNullOrWhiteSpace(line))
                            continue;

                        var parts = line.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length > 0)
                        {
                            var name = parts[0];
                            var port = parts.Length > 1 ? parts[1] : "usb";
                            results.Add(new CameraDeviceInfo(port, name));
                        }
                    }
                    proc.WaitForExit();
                }
            }
            catch { }
            return (IReadOnlyList<CameraDeviceInfo>)results;
        });
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Status == CameraStatus.Connected)
            return;

        Status = CameraStatus.Connecting;

        if (OperatingSystem.IsWindows())
        {
#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
            try
            {
                var mgr = GetOrCreateDeviceManager();
                await Task.Run(() =>
                {
                    mgr.ConnectToCamera();
                }, cancellationToken);

                ICameraDevice? dev = mgr.SelectedCameraDevice;
                if (dev == null || !dev.IsConnected)
                {
                    dev = mgr.ConnectedDevices.Cast<ICameraDevice>().FirstOrDefault(d => d.IsConnected);
                }

                if (dev != null && dev.IsConnected)
                {
                    _cameraDevice = dev;
                    DisplayName = !string.IsNullOrWhiteSpace(dev.DisplayName)
                        ? dev.DisplayName
                        : (!string.IsNullOrWhiteSpace(dev.DeviceName) ? dev.DeviceName : "DSLR Kamera");
                    Status = CameraStatus.Connected;
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Direct DSLR connection error: {ex.Message}");
            }
#endif

            // Fallback: check if an external digiCamControl service is already running
            var status = await DigiCamControlManager.GetStatusAsync(_digiCamUrl);
            if (status.IsReachable)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(3));
                    var test = await HttpClient.GetStringAsync($"{_digiCamUrl.TrimEnd('/')}/?slc=get&param1=camera", cts.Token);
                    var camName = test?.Trim();
                    if (!string.IsNullOrWhiteSpace(camName) && !camName.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) && camName != "null")
                    {
                        _useDigiCamControl = true;
                        DisplayName = $"DSLR ({camName})";
                        Status = CameraStatus.Connected;
                        return;
                    }
                }
                catch
                {
                    _useDigiCamControl = false;
                }
            }

            Status = CameraStatus.Error;
            throw new CameraException(
                "Keine DSLR/Systemkamera per USB gefunden.\n\n" +
                "Bitte prüfen:\n" +
                "1. Ist die Kamera (Nikon, Canon, Sony) per USB verbunden und eingeschaltet?\n" +
                "2. Bei Nikon D5600: Kamera-Flugmodus auf AN stellen (damit SnapBridge Bluetooth den USB-Port nicht blockiert).\n" +
                "3. Bei Nikon Z 50 II: Direkt per USB anstecken (kann auch nativ per USB-Streaming UVC genutzt werden).");
        }
        else
        {
            // Linux via gphoto2
            var devices = await FindLinuxDevicesAsync();
            if (devices.Count > 0)
            {
                DisplayName = devices[0].Name;
                Status = CameraStatus.Connected;
                return;
            }

            Status = CameraStatus.Error;
            throw new CameraException(
                "Keine DSLR/Systemkamera per USB gefunden (gphoto2).\n\n" +
                "Prüfe:\n" +
                "1. Ist die Kamera (Nikon, Canon, Sony, Fuji) per USB verbunden und an?\n" +
                "2. Ist gphoto2 installiert? (sudo pacman -S gphoto2 oder sudo apt install gphoto2)\n" +
                "3. Blockiert ein Hintergrunddienst den USB-Port? (killall gvfs-gphoto2-volume-monitor)");
        }
    }

    public Task StartLiveViewAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected)
            throw new CameraException("DSLR ist nicht verbunden.");

        if (_liveViewTask is not null)
            return Task.CompletedTask;

        _liveViewCts = new CancellationTokenSource();
        var token = _liveViewCts.Token;

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
        if (_cameraDevice is not null)
        {
            try
            {
                _cameraDevice.StartLiveView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StartLiveView error: {ex.Message}");
            }
            _liveViewTask = Task.Run(() => RunNativeLiveViewLoopAsync(token), CancellationToken.None);
            return Task.CompletedTask;
        }
#endif

        if (OperatingSystem.IsWindows() && _useDigiCamControl)
        {
            _liveViewTask = Task.Run(() => RunDigiCamLiveViewLoopAsync(token), CancellationToken.None);
        }
        else if (OperatingSystem.IsLinux())
        {
            _liveViewTask = Task.Run(() => RunGPhoto2LiveViewLoopAsync(token), CancellationToken.None);
        }
        else
        {
            _liveViewTask = Task.Run(() => RunSimulatedLiveViewLoopAsync(token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopLiveViewAsync()
    {
        if (_liveViewTask is null)
            return;

        _liveViewCts?.Cancel();
        try
        {
            await _liveViewTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _liveViewCts?.Dispose();
            _liveViewCts = null;
            _liveViewTask = null;
        }

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
        if (_cameraDevice is not null)
        {
            try
            {
                _cameraDevice.StopLiveView();
            }
            catch { }
        }
#endif
    }

    public async Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected)
            throw new CameraException("DSLR ist nicht verbunden.");

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
        if (_cameraDevice is not null)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnPhotoCaptured(object sender, PhotoCapturedEventArgs e)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), $"lumio_capture_{Guid.NewGuid():N}.jpg");
                        e.CameraDevice.TransferFile(e.Handle, tempPath);
                        if (File.Exists(tempPath))
                        {
                            var bytes = await File.ReadAllBytesAsync(tempPath).ConfigureAwait(false);
                            try { File.Delete(tempPath); } catch { }
                            tcs.TrySetResult(bytes);
                        }
                        else
                        {
                            tcs.TrySetException(new CameraException("Foto konnte nicht von der Kamera übertragen werden."));
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });
            }

            _cameraDevice.PhotoCaptured += OnPhotoCaptured;
            try
            {
                _cameraDevice.CapturePhoto();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var bytes = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                return new CapturedPhoto(bytes, DateTimeOffset.Now);
            }
            finally
            {
                _cameraDevice.PhotoCaptured -= OnPhotoCaptured;
            }
        }
#endif

        if (OperatingSystem.IsWindows() && _useDigiCamControl)
        {
            try
            {
                await HttpClient.GetStringAsync($"{_digiCamUrl.TrimEnd('/')}/?slc=capture", cancellationToken);
                await Task.Delay(600, cancellationToken);

                var last = await HttpClient.GetStringAsync($"{_digiCamUrl.TrimEnd('/')}/?slc=get&param1=lastcaptured", cancellationToken);
                var filename = last.Trim().Trim('"', '\r', '\n');
                if (!string.IsNullOrWhiteSpace(filename) && filename != "-")
                {
                    var photoBytes = await HttpClient.GetByteArrayAsync($"{_digiCamUrl.TrimEnd('/')}/image/{Uri.EscapeDataString(filename)}", cancellationToken);
                    if (photoBytes.Length > 1000)
                    {
                        return new CapturedPhoto(photoBytes, DateTimeOffset.Now);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"digiCamControl capture error: {ex.Message}");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"lumio_dslr_{Guid.NewGuid():N}.jpg");
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "gphoto2",
                    Arguments = $"--capture-image-and-download --filename \"{tempFile}\" --force-overwrite",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (proc is not null)
                {
                    await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    if (File.Exists(tempFile))
                    {
                        var bytes = await File.ReadAllBytesAsync(tempFile, cancellationToken).ConfigureAwait(false);
                        return new CapturedPhoto(bytes, DateTimeOffset.Now);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new CameraException($"DSLR-Auslösung per gphoto2 fehlgeschlagen: {ex.Message}", ex);
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        var placeholder = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
        return new CapturedPhoto(placeholder, DateTimeOffset.Now);
    }

    public async Task ApplySettingsAsync(DslrCameraSettings settings, CancellationToken cancellationToken = default)
    {
#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
        if (_cameraDevice is not null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(settings.Iso) && settings.Iso != DslrOptions.Auto && _cameraDevice.IsoNumber != null)
                {
                    _cameraDevice.IsoNumber.SetValue(settings.Iso);
                }
                if (!string.IsNullOrWhiteSpace(settings.ShutterSpeed) && settings.ShutterSpeed != DslrOptions.Auto && _cameraDevice.ShutterSpeed != null)
                {
                    _cameraDevice.ShutterSpeed.SetValue(settings.ShutterSpeed);
                }
                if (!string.IsNullOrWhiteSpace(settings.Aperture) && settings.Aperture != DslrOptions.Auto && _cameraDevice.FNumber != null)
                {
                    _cameraDevice.FNumber.SetValue(settings.Aperture);
                }
                if (!string.IsNullOrWhiteSpace(settings.WhiteBalance) && settings.WhiteBalance != DslrOptions.Auto && _cameraDevice.WhiteBalance != null)
                {
                    _cameraDevice.WhiteBalance.SetValue(settings.WhiteBalance);
                }
                if (!string.IsNullOrWhiteSpace(settings.ExposureCompensation) && _cameraDevice.ExposureCompensation != null)
                {
                    _cameraDevice.ExposureCompensation.SetValue(settings.ExposureCompensation);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to apply in-process camera settings: {ex.Message}");
            }
            return;
        }
#endif

        if (OperatingSystem.IsWindows() && _useDigiCamControl)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(settings.Iso) && settings.Iso != DslrOptions.Auto)
                    await HttpClient.GetStringAsync($"{_digiCamUrl.TrimEnd('/')}/?slc=set&param1=iso&param2={Uri.EscapeDataString(settings.Iso)}", cancellationToken);

                if (!string.IsNullOrWhiteSpace(settings.ShutterSpeed) && settings.ShutterSpeed != DslrOptions.Auto)
                    await HttpClient.GetStringAsync($"{_digiCamUrl.TrimEnd('/')}/?slc=set&param1=shutterspeed&param2={Uri.EscapeDataString(settings.ShutterSpeed)}", cancellationToken);

                if (!string.IsNullOrWhiteSpace(settings.Aperture) && settings.Aperture != DslrOptions.Auto)
                    await HttpClient.GetStringAsync($"{_digiCamUrl.TrimEnd('/')}/?slc=set&param1=aperture&param2={Uri.EscapeDataString(settings.Aperture)}", cancellationToken);

                if (!string.IsNullOrWhiteSpace(settings.ExposureCompensation))
                    await HttpClient.GetStringAsync($"{_digiCamUrl.TrimEnd('/')}/?slc=set&param1=exposurecompensation&param2={Uri.EscapeDataString(settings.ExposureCompensation)}", cancellationToken);
            }
            catch { }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var args = new List<string>();
                if (!string.IsNullOrWhiteSpace(settings.Iso) && settings.Iso != DslrOptions.Auto)
                    args.Add($"--set-config-value iso={settings.Iso}");
                if (!string.IsNullOrWhiteSpace(settings.ShutterSpeed) && settings.ShutterSpeed != DslrOptions.Auto)
                    args.Add($"--set-config-value shutterspeed={settings.ShutterSpeed}");
                if (!string.IsNullOrWhiteSpace(settings.Aperture) && settings.Aperture != DslrOptions.Auto)
                    args.Add($"--set-config-value f-number={settings.Aperture}");

                if (args.Count > 0)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "gphoto2",
                        Arguments = string.Join(" ", args),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                }
            }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopLiveViewAsync().ConfigureAwait(false);
        Status = CameraStatus.Disconnected;
    }

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
    private async Task RunNativeLiveViewLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && Status == CameraStatus.Connected && _cameraDevice != null)
        {
            try
            {
                var liveView = _cameraDevice.GetLiveViewImage();
                if (liveView?.ImageData is { Length: > 100 } data)
                {
                    FrameReady?.Invoke(this, new LiveViewFrame(data, FrameFormat.Jpeg, liveView.ImageWidth, liveView.ImageHeight, 0));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"LiveView frame error: {ex.Message}");
            }

            await Task.Delay(33, token).ConfigureAwait(false); // ~30 fps
        }
    }
#endif

    private async Task RunDigiCamLiveViewLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(45)); // ~22 FPS
        while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try
            {
                var jpeg = await HttpClient.GetByteArrayAsync($"{_digiCamUrl.TrimEnd('/')}/liveview.jpg", token);
                if (jpeg.Length > 200 && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
                {
                    FrameReady?.Invoke(this, new LiveViewFrame(jpeg, FrameFormat.Jpeg, 1024, 680, 0));
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task RunGPhoto2LiveViewLoopAsync(CancellationToken token)
    {
        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = "gphoto2",
                Arguments = "--capture-movie --stdout",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (proc is null) return;

            var stream = proc.StandardOutput.BaseStream;
            var buffer = new byte[32 * 1024];
            var splitter = new MjpegFrameSplitter();

            while (!token.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read <= 0) break;

                var frames = splitter.Push(buffer.AsSpan(0, read));
                foreach (var f in frames)
                {
                    FrameReady?.Invoke(this, new LiveViewFrame(f, FrameFormat.Jpeg, 1024, 680, 0));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            try { proc?.Kill(); proc?.Dispose(); } catch { }
        }
    }

    private async Task RunSimulatedLiveViewLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            // Fallback tick
        }
    }
}
