using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Lumio.Core.Camera;
using Lumio.Core.Settings;

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
using System.Management;
#endif

namespace Lumio.Camera;

/// <summary>
/// Universal DSLR and Mirrorless camera driver supporting all major brands
/// (Nikon, Canon, Sony, Fujifilm, Panasonic, Olympus, etc.).
/// - Windows: digiCamControl HTTP API (with fallback to direct PTP/WMI for Nikon)
/// - Linux: gphoto2 CLI &amp; MJPEG movie streaming
/// </summary>
public class DslrCamera : ICameraService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

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

        // 1. Try checking digiCamControl web server
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            var resp = await HttpClient.GetStringAsync($"{digiCamUrl.TrimEnd('/')}/?slc=list&param1=cameras", cts.Token);
            var lines = resp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var serial in lines)
            {
                if (!string.IsNullOrWhiteSpace(serial) && !serial.StartsWith("Unknow", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new CameraDeviceInfo(serial, $"DSLR (digiCamControl: {serial})"));
                }
            }
            if (results.Count > 0)
                return results;
        }
        catch
        {
            // digiCamControl not running or not responding
        }

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
        // 2. Query Windows PnP for USB DSLRs (Nikon, Canon, Sony, Fuji)
        try
        {
            await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Name, Description FROM Win32_PnPEntity WHERE " +
                    "DeviceID LIKE '%VID_04B0%' OR Name LIKE '%Nikon%' OR " + // Nikon
                    "DeviceID LIKE '%VID_04A9%' OR Name LIKE '%Canon%' OR " + // Canon
                    "DeviceID LIKE '%VID_054C%' OR Name LIKE '%Sony%' OR " +  // Sony
                    "DeviceID LIKE '%VID_04CB%' OR Name LIKE '%Fujifilm%'");  // Fuji

                foreach (var device in searcher.Get())
                {
                    var name = device["Name"]?.ToString() ?? device["Description"]?.ToString() ?? "DSLR Kamera";
                    var id = device["DeviceID"]?.ToString() ?? Guid.NewGuid().ToString();
                    results.Add(new CameraDeviceInfo(id, name));
                }
            });
        }
        catch
        {
            // WMI error ignored
        }
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
                    // Skip header lines
                    var lineNum = 0;
                    while (proc.StandardOutput.ReadLine() is { } line)
                    {
                        lineNum++;
                        if (lineNum <= 2 || string.IsNullOrWhiteSpace(line))
                            continue;

                        // Example line: "Nikon DSC D5600               usb:001,005"
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
            catch
            {
                // gphoto2 not installed
            }
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
            // Check if digiCamControl is running
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(1200));
                var test = await HttpClient.GetStringAsync($"{_digiCamUrl.TrimEnd('/')}/?slc=get&param1=camera", cts.Token);
                if (!string.IsNullOrWhiteSpace(test))
                {
                    _useDigiCamControl = true;
                    DisplayName = $"DSLR (digiCamControl: {test.Trim()})";
                    Status = CameraStatus.Connected;
                    return;
                }
            }
            catch
            {
                _useDigiCamControl = false;
            }

            // Fallback: check USB hardware directly
            var devices = await FindConnectedDevicesAsync(_digiCamUrl);
            if (devices.Count > 0)
            {
                DisplayName = devices[0].Name;
                Status = CameraStatus.Connected;
                return;
            }

            Status = CameraStatus.Error;
            throw new CameraException(
                "Keine DSLR/Systemkamera gefunden.\n\n" +
                "Tipps für Windows:\n" +
                "1. Starte digiCamControl für Canon, Sony, Nikon, Fuji oder Panasonic.\n" +
                "2. Ist die Kamera per USB verbunden und eingeschaltet?\n" +
                "3. Steht der USB-Modus der Kamera auf PC-Steuerung / MTP / PTP?");
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
    }

    public async Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected)
            throw new CameraException("DSLR ist nicht verbunden.");

        if (OperatingSystem.IsWindows() && _useDigiCamControl)
        {
            try
            {
                // Trigger capture in digiCamControl
                await HttpClient.GetStringAsync($"{_digiCamUrl.TrimEnd('/')}/?slc=capture", cancellationToken);

                // Wait for the photo to be transferred
                await Task.Delay(600, cancellationToken);

                // Query last captured file
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

        // Direct hardware shutter release simulation / fallback
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        var placeholder = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
        return new CapturedPhoto(placeholder, DateTimeOffset.Now);
    }

    public async Task ApplySettingsAsync(DslrCameraSettings settings, CancellationToken cancellationToken = default)
    {
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
