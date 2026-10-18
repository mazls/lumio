using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;
using Lumio.Core.Camera;
using Lumio.Core.Settings;
using Log = Android.Util.Log;

namespace Lumio.App.Android;

/// <summary>
/// Native Android USB-OTG PTP driver for Nikon DSLRs and Mirrorless cameras (e.g. D5600, Z 50 II).
/// Communicates directly with the camera over USB Host bulk endpoints (zero external native libraries required).
/// </summary>
public sealed class AndroidNikonPtpCamera : ICameraService
{
    private const string LogTag = "LumioPtp";
    private const int NikonVendorId = 0x04B0; // 1200 decimal

    // Standard PTP Opcodes
    private const ushort PtpOcGetDeviceInfo = 0x1001;
    private const ushort PtpOcOpenSession = 0x1002;
    private const ushort PtpOcCloseSession = 0x1003;
    private const ushort PtpOcGetObjectHandles = 0x1007;
    private const ushort PtpOcGetObjectInfo = 0x1008;
    private const ushort PtpOcGetObject = 0x1009;
    private const ushort PtpFormatExifJpeg = 0x3801;
    private const ushort PtpOcInitiateCapture = 0x100E;

    // Nikon-specific PTP Opcodes
    private const ushort PtpOcNikonCapture = 0x90C0;
    private const ushort PtpOcNikonStartLiveView = 0x9201;
    private const ushort PtpOcNikonGetLiveViewImage = 0x9203;
    private const ushort PtpOcNikonEndLiveView = 0x9202;
    private const ushort PtpOcNikonDeviceReady = 0x90C8;
    private const ushort PtpOcNikonGetEvent = 0x90C7;
    private const ushort PtpOcNikonAfDrive = 0x90C1;
    private const ushort PtpRcNikonOutOfFocus = 0xA002;

    // PTP / Nikon event codes
    private const ushort PtpEcObjectAdded = 0x4002;
    private const ushort PtpEcNikonObjectAddedInSdram = 0xC101;

    // Nikon stores pictures in camera RAM (no SD card / "PC" destination) under this fixed handle
    private const uint NikonSdramObjectHandle = 0xFFFF0001;

    // PTP Response Codes
    private const ushort PtpRcOk = 0x2001;
    private const ushort PtpRcDeviceBusy = 0x2019;
    private const ushort PtpRcSessionAlreadyOpen = 0x201E;
    private const ushort PtpRcNotInLiveView = 0xA00B;

    private readonly Activity _activity;
    private readonly BoothSettings? _settings;
    private readonly SemaphoreSlim _usbLock = new(1, 1);

    private UsbDevice? _device;
    private UsbDeviceConnection? _connection;
    private UsbInterface? _interface;
    private UsbEndpoint? _bulkIn;
    private UsbEndpoint? _bulkOut;
    private UsbEndpoint? _interruptIn;

    private uint _transactionId = 1;
    private CameraStatus _status = CameraStatus.Disconnected;
    private CancellationTokenSource? _liveViewCts;
    private Task? _liveViewTask;
    private volatile bool _isLiveViewRunning;
    private volatile bool _isCapturing;

    public AndroidNikonPtpCamera(Activity activity, BoothSettings? settings = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _settings = settings;
    }

    public string DisplayName { get; private set; } = "Nikon Kamera (USB-OTG)";

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

    public static bool IsDeviceConnected(Activity activity)
    {
        try
        {
            var usbManager = (UsbManager?)activity.GetSystemService(Context.UsbService);
            return usbManager?.DeviceList?.Values.Any(IsNikonDevice) == true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<CameraDeviceInfo> FindConnectedDevices(Activity activity)
    {
        var results = new List<CameraDeviceInfo>();
        try
        {
            var usbManager = (UsbManager?)activity.GetSystemService(Context.UsbService);
            if (usbManager?.DeviceList != null)
            {
                foreach (var dev in usbManager.DeviceList.Values)
                {
                    if (IsNikonDevice(dev))
                    {
                        var name = GetNikonModelName(dev);
                        results.Add(new CameraDeviceInfo(GetDeviceKey(dev), $"📷 {name} (USB-OTG PTP)"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Error scanning Nikon USB devices: {ex.Message}");
        }

        return results;
    }

    /// <summary>Stable camera ID for the settings (Android's DeviceId changes every time the camera is plugged in).</summary>
    private static string GetDeviceKey(UsbDevice dev) => $"ptp:nikon:{dev.VendorId:X4}:{dev.ProductId:X4}";

    public static bool IsNikonDevice(UsbDevice dev)
    {
        // 1. Must be Nikon Vendor ID (0x04B0 = 1200)
        if (dev.VendorId != NikonVendorId)
        {
            return false;
        }

        // 2. Explicitly EXCLUDE Nikon Z 50 II (0x0455) and mirrorless Z-series.
        // The Z 50 II MUST run exclusively via UVC / UAC through AndroidCameraService!
        if (dev.ProductId == 0x0455 || (dev.ProductName?.Contains("Z 50", StringComparison.OrdinalIgnoreCase) == true))
        {
            return false;
        }

        // 3. If device has a Video Interface (Class 14 = CC_VIDEO / UVC), it is a UVC webcam
        // MUST NEVER be handled by PTP!
        for (int i = 0; i < dev.InterfaceCount; i++)
        {
            var iface = dev.GetInterface(i);
            if ((int)iface.InterfaceClass == 14) // UsbClass.Video / CC_VIDEO
            {
                return false;
            }
        }

        // 4. Nikon DSLRs with PTP Still Image Class (Class 6, Subclass 1, Protocol 1)
        for (int i = 0; i < dev.InterfaceCount; i++)
        {
            var iface = dev.GetInterface(i);
            if (iface.InterfaceClass == UsbClass.StillImage && (int)iface.InterfaceSubclass == 1 && (int)iface.InterfaceProtocol == 1)
            {
                return true;
            }
        }

        // 5. Fallback for known Nikon DSLR Product IDs (D5600, D3500, D5300, etc.)
        return dev.ProductId switch
        {
            0x0440 or 0x0441 => true, // D5600
            0x0448 => true,           // D3500
            0x043E => true,           // D3400
            0x0430 => true,           // D3300
            0x0438 => true,           // D5500
            0x042C => true,           // D5300
            0x0444 => true,           // D7500
            0x0432 => true,           // D7200
            0x042E => true,           // D7100
            0x0442 => true,           // D850
            0x0449 => true,           // D780
            0x043C => true,           // D500
            _ => false
        };
    }

    public static string GetNikonModelName(UsbDevice dev)
    {
        if (!string.IsNullOrWhiteSpace(dev.ProductName))
            return dev.ProductName;

        return dev.ProductId switch
        {
            0x0440 or 0x0441 => "Nikon D5600",
            0x0448 => "Nikon D3500",
            0x0438 => "Nikon D5500",
            0x042C => "Nikon D5300",
            0x042E => "Nikon D7200 / D7500",
            _ => "Nikon DSLR (PTP)"
        };
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Status == CameraStatus.Connected)
            return;

        Status = CameraStatus.Connecting;

        var usbManager = (UsbManager?)_activity.GetSystemService(Context.UsbService);
        if (usbManager is null)
        {
            Status = CameraStatus.Error;
            throw new CameraException("Android USB-Dienst nicht verfügbar.");
        }

        // Find Nikon device
        UsbDevice? targetDevice = null;
        if (usbManager.DeviceList != null)
        {
            var requested = _settings?.WebcamDeviceId;
            if (requested?.StartsWith("ptp:nikon:", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Current key is vendor/product ID (stable); older settings stored Android's per-plug device number
                targetDevice = usbManager.DeviceList.Values.FirstOrDefault(d => string.Equals(GetDeviceKey(d), requested, StringComparison.OrdinalIgnoreCase))
                               ?? (int.TryParse(requested["ptp:nikon:".Length..], out var legacyId)
                                   ? usbManager.DeviceList.Values.FirstOrDefault(d => d.DeviceId == legacyId)
                                   : null);
            }

            targetDevice ??= usbManager.DeviceList.Values.FirstOrDefault(IsNikonDevice);
        }

        if (targetDevice is null)
        {
            Status = CameraStatus.Error;
            throw new CameraException(
                "Keine Nikon-Kamera per USB-OTG gefunden.\n\n" +
                "Bitte prüfen:\n" +
                "1. Ist das USB-OTG-Kabel fest mit dem Tablet und der Kamera verbunden?\n" +
                "2. Ist die Kamera eingeschaltet?\n" +
                "3. Bei Nikon D5600: Kamera-Flugmodus auf AN stellen (damit SnapBridge Bluetooth den USB-Port nicht blockiert).\n" +
                "4. Bei Nikon Z 50 II: Direkt per USB-C verbinden.");
        }

        _device = targetDevice;
        DisplayName = GetNikonModelName(_device);

        // Request USB Permission if not already granted
        if (!await UsbPermission.RequestAsync(_activity, usbManager, _device, cancellationToken))
        {
            Status = CameraStatus.Error;
            throw new CameraException("USB-Zugriff auf die Nikon-Kamera wurde verweigert.");
        }

        // Open device connection
        _connection = usbManager.OpenDevice(_device);
        if (_connection is null)
        {
            Status = CameraStatus.Error;
            throw new CameraException("Konnte USB-Verbindung zur Nikon-Kamera nicht öffnen.");
        }

        // Locate PTP Interface and endpoints
        UsbInterface? ptpInterface = null;
        for (int i = 0; i < _device.InterfaceCount; i++)
        {
            var iface = _device.GetInterface(i);
            if (iface.InterfaceClass == UsbClass.StillImage && (int)iface.InterfaceSubclass == 1 && (int)iface.InterfaceProtocol == 1)
            {
                ptpInterface = iface;
                break;
            }
        }

        ptpInterface ??= _device.GetInterface(0);
        _interface = ptpInterface;

        if (_interface is null || !_connection.ClaimInterface(_interface, force: true))
        {
            Status = CameraStatus.Error;
            throw new CameraException("PTP-Schnittstelle der Kamera konnte nicht reserviert werden.");
        }

        for (int i = 0; i < _interface.EndpointCount; i++)
        {
            var ep = _interface.GetEndpoint(i);
            if (ep is not null && ep.Type == UsbAddressing.XferBulk)
            {
                if (ep.Direction == UsbAddressing.In)
                    _bulkIn = ep;
                else
                    _bulkOut = ep;
            }
            else if (ep is not null && ep.Type == UsbAddressing.XferInterrupt && ep.Direction == UsbAddressing.In)
            {
                _interruptIn = ep;
            }
        }

        if (_bulkIn is null || _bulkOut is null)
        {
            Status = CameraStatus.Error;
            throw new CameraException("Erforderliche USB-Bulk-Endpunkte wurden an der Kamera nicht gefunden.");
        }

        // Initialize PTP Session (OpenSession sessionId = 1)
        await _usbLock.WaitAsync(cancellationToken);
        try
        {
            // A previous app run (or Android's own MTP service) can leave a session, pending data or a
            // stalled endpoint behind - reset the PTP state machine first
            await ResetPtpStateAsync(cancellationToken);

            _transactionId = 1;
            ushort responseCode = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var (code, _) = await ExecuteCommandNoDataAsync(PtpOcOpenSession, 1);
                    responseCode = code;
                    if (code == PtpRcSessionAlreadyOpen)
                    {
                        // Restart the stale session so transaction IDs start fresh
                        await ExecuteCommandNoDataAsync(PtpOcCloseSession);
                        _transactionId = 1;
                        (code, _) = await ExecuteCommandNoDataAsync(PtpOcOpenSession, 1);
                        responseCode = code;
                    }
                    if (code == PtpRcOk || code == PtpRcSessionAlreadyOpen)
                    {
                        break;
                    }
                }
                catch when (attempt < 2)
                {
                    Log.Warn(LogTag, $"OpenSession attempt {attempt + 1} failed, resetting");
                    await Task.Delay(300, cancellationToken);
                    await ResetPtpStateAsync(cancellationToken);
                }
            }

            if (responseCode != PtpRcOk && responseCode != PtpRcSessionAlreadyOpen)
                throw new CameraException($"Kamera lehnt die PTP-Sitzung ab (Code 0x{responseCode:X4}).");

            Log.Info(LogTag, $"{DisplayName}: PTP session opened (response 0x{responseCode:X4})");
            Status = CameraStatus.Connected;
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, $"{DisplayName}: OpenSession failed: {ex}");
            Status = CameraStatus.Error;
            throw new CameraException($"Fehler beim Starten der PTP-Sitzung: {ex.Message}", ex);
        }
        finally
        {
            _usbLock.Release();
        }
    }

    public Task StartLiveViewAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected)
            throw new CameraException("Nikon-Kamera ist nicht verbunden.");

        if (_liveViewTask is not null || _isLiveViewRunning)
            return Task.CompletedTask;

        _liveViewCts = new CancellationTokenSource();
        var token = _liveViewCts.Token;

        _liveViewTask = Task.Run(async () =>
        {
            _isLiveViewRunning = true;

            // Start Nikon Live View mode
            await _usbLock.WaitAsync(CancellationToken.None);
            try
            {
                await StartNikonLiveViewAsync(token);
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Nikon StartLiveView error: {ex.Message}");
            }
            finally
            {
                _usbLock.Release();
            }

            var liveViewRequests = 0;
            var consecutiveErrors = 0;
            while (!token.IsCancellationRequested)
            {
                if (_isCapturing)
                {
                    await Task.Delay(50, token);
                    continue;
                }

                try
                {
                    byte[]? payload = null;

                    await _usbLock.WaitAsync(token);
                    try
                    {
                        var (code, data) = await ExecuteCommandWithDataAsync(PtpOcNikonGetLiveViewImage);
                        payload = data;
                        if (++liveViewRequests <= 5 || (code != PtpRcOk && liveViewRequests % 50 == 0))
                            Log.Info(LogTag, $"GetLiveViewImage #{liveViewRequests}: 0x{code:X4}, {data?.Length ?? 0} bytes");

                        // The camera left live view on its own (standby timer, menu, ...): start it again
                        if (code == PtpRcNotInLiveView)
                            await StartNikonLiveViewAsync(token);
                    }
                    finally
                    {
                        _usbLock.Release();
                    }

                    if (payload is null)
                    {
                        // No picture (camera busy or just restarting live view): back off instead of hammering it
                        await Task.Delay(300, token);
                        continue;
                    }

                    if (payload != null && payload.Length > 200)
                    {
                        var (start, end) = FindJpegMarkers(payload);
                        if (start >= 0 && end > start)
                        {
                            var length = end - start + 2;
                            var jpegBytes = new byte[length];
                            Buffer.BlockCopy(payload, start, jpegBytes, 0, length);

                            var frame = new LiveViewFrame(jpegBytes, FrameFormat.Jpeg, 0, 0, 0);
                            FrameReady?.Invoke(this, frame);
                        }
                    }

                    consecutiveErrors = 0;
                    await Task.Delay(35, token); // ~28 FPS
                }
                catch (System.OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn(LogTag, $"LiveView loop frame error: {ex.Message}");
                    if (++consecutiveErrors >= 10)
                    {
                        // Camera unplugged or switched off: report it so the booth reconnects
                        Status = CameraStatus.Disconnected;
                        break;
                    }
                    try { await Task.Delay(100, token); } catch (System.OperationCanceledException) { break; }
                }
            }

            // End Live View mode
            await _usbLock.WaitAsync(CancellationToken.None);
            try
            {
                await ExecuteCommandNoDataAsync(PtpOcNikonEndLiveView);
            }
            catch { }
            finally
            {
                _usbLock.Release();
            }

            _isLiveViewRunning = false;
        }, token);

        return Task.CompletedTask;
    }

    public async Task StopLiveViewAsync()
    {
        if (_liveViewTask is null)
            return;

        _liveViewCts?.Cancel();
        try
        {
            await _liveViewTask;
        }
        catch { }
        finally
        {
            _liveViewCts?.Dispose();
            _liveViewCts = null;
            _liveViewTask = null;
            _isLiveViewRunning = false;
        }
    }

    public async Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected)
            throw new CameraException("Nikon-Kamera ist nicht verbunden.");

        _isCapturing = true;
        var hadLiveView = _isLiveViewRunning;

        try
        {
            // Give in-flight live view frame a moment to finish
            await Task.Delay(100, cancellationToken);

            await _usbLock.WaitAsync(cancellationToken);
            try
            {
                // Stop live view so the DSLR mirror can flip down and the shutter can actuate cleanly
                if (hadLiveView)
                {
                    var (endCode, _) = await ExecuteCommandNoDataAsync(PtpOcNikonEndLiveView);
                    Log.Info(LogTag, $"EndLiveView: 0x{endCode:X4}");
                    await WaitDeviceReadyAsync(TimeSpan.FromSeconds(3), cancellationToken);
                }

                // Focus first: in AF-S the D5600 silently refuses to fire when it is not in focus
                var (afCode, _) = await ExecuteCommandNoDataAsync(PtpOcNikonAfDrive);
                var focusResult = afCode == PtpRcOk ? await WaitDeviceReadyAsync(TimeSpan.FromSeconds(4), cancellationToken) : afCode;
                Log.Info(LogTag, $"AfDrive: 0x{afCode:X4}, focus result 0x{focusResult:X4}");

                // Nikon "InitiateCaptureRecInSdram": the picture stays in the camera's RAM buffer
                var (captureCode, _) = await ExecuteCommandNoDataAsync(PtpOcNikonCapture, 0xFFFFFFFF);
                Log.Info(LogTag, $"Capture (SDRAM): 0x{captureCode:X4}");

                if (captureCode == PtpRcOk)
                {
                    // The shutter has fired: only fetch this picture, never trigger a second shot
                    var handles = await WaitForNikonPicturesAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    if (handles.Count == 0)
                    {
                        throw new CameraException(focusResult == PtpRcNikonOutOfFocus
                            ? "Die Kamera konnte nicht scharfstellen. Bitte Motiv/Licht prüfen oder das Objektiv auf manuellen Fokus (M) stellen."
                            : "Die Kamera hat nicht ausgelöst. Bitte Fokus und Kamera-Einstellungen prüfen.");
                    }

                    for (var attempt = 0; attempt < 25; attempt++)
                    {
                        await WaitDeviceReadyAsync(TimeSpan.FromSeconds(5), cancellationToken);

                        // RAW+JPEG delivers two pictures: take the JPEG. Like gphoto2, read the object info first;
                        // the picture is ready once it reports a size.
                        var rawOnly = true;
                        foreach (var handle in handles)
                        {
                            var (infoCode, info) = await ExecuteCommandWithDataAsync(PtpOcGetObjectInfo, handle);
                            var ready = infoCode == PtpRcOk && info is { Length: >= 12 };
                            var format = ready ? BinaryPrimitives.ReadUInt16LittleEndian(info!.AsSpan(4, 2)) : (ushort)0;
                            var size = ready ? BinaryPrimitives.ReadUInt32LittleEndian(info!.AsSpan(8, 4)) : 0u;
                            if (attempt % 5 == 0)
                                Log.Info(LogTag, $"Picture 0x{handle:X8}: info 0x{infoCode:X4}, format 0x{format:X4}, {size} bytes");

                            if (size == 0 || format == 0)
                            {
                                rawOnly = false; // not ready yet
                                continue;
                            }
                            if (format != PtpFormatExifJpeg)
                                continue;

                            rawOnly = false;
                            var (code, data) = await ExecuteCommandWithDataAsync(PtpOcGetObject, handle);
                            Log.Info(LogTag, $"GetObject (SDRAM 0x{handle:X8}, {size} bytes announced): 0x{code:X4}, {data?.Length ?? 0} bytes");
                            if (code == PtpRcOk && data is { Length: > 0 })
                                return new CapturedPhoto(data, DateTimeOffset.Now);
                        }

                        if (rawOnly)
                        {
                            throw new CameraException("Die Kamera speichert nur RAW. Bitte an der Kamera die Bildqualität auf JPEG oder RAW+JPEG stellen.");
                        }
                        await Task.Delay(200, cancellationToken);
                    }
                    throw new CameraException("Foto wurde ausgelöst, konnte aber nicht von der Kamera geladen werden.");
                }
            }
            finally
            {
                _usbLock.Release();
            }

            // Fallback: standard PTP capture onto the memory card, then download the new file
            return await CaptureToCardAsync(cancellationToken);
        }
        finally
        {
            _isCapturing = false;

            // Resume Live View if it was running
            if (hadLiveView && _liveViewTask != null && Status == CameraStatus.Connected)
            {
                await _usbLock.WaitAsync(CancellationToken.None);
                try
                {
                    await StartNikonLiveViewAsync(CancellationToken.None);
                }
                catch { }
                finally
                {
                    _usbLock.Release();
                }
            }
        }
    }

    private async Task<CapturedPhoto> CaptureToCardAsync(CancellationToken cancellationToken)
    {
        await _usbLock.WaitAsync(cancellationToken);
        try
        {
            var existingHandles = new HashSet<uint>(await GetObjectHandlesAsync());

            var (captureCode, _) = await ExecuteCommandNoDataAsync(PtpOcInitiateCapture, 0x00000000, 0x00000000);
            Log.Info(LogTag, $"InitiateCapture (card): 0x{captureCode:X4}");
            if (captureCode != PtpRcOk)
                throw new CameraException($"Die Kamera hat nicht ausgelöst (PTP Code 0x{captureCode:X4}).");

            // Wait until the camera has finished writing, then pick the new JPEG (skips new folders and RAW files)
            for (var attempt = 0; attempt < 35; attempt++) // up to ~10 seconds
            {
                await Task.Delay(300, cancellationToken);
                await WaitDeviceReadyAsync(TimeSpan.FromSeconds(5), cancellationToken);

                foreach (var handle in (await GetObjectHandlesAsync()).Where(h => !existingHandles.Contains(h)).Reverse())
                {
                    var (infoCode, info) = await ExecuteCommandWithDataAsync(PtpOcGetObjectInfo, handle);
                    var format = infoCode == PtpRcOk && info is { Length: >= 6 } ? BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(4, 2)) : (ushort)0;
                    Log.Info(LogTag, $"New object 0x{handle:X8}: format 0x{format:X4}");
                    if (format != PtpFormatExifJpeg)
                        continue;

                    var (code, data) = await ExecuteCommandWithDataAsync(PtpOcGetObject, handle);
                    Log.Info(LogTag, $"GetObject (card 0x{handle:X8}): 0x{code:X4}, {data?.Length ?? 0} bytes");
                    if (code != PtpRcOk || data is null || data.Length == 0)
                        throw new CameraException($"Foto-Übertragung von der Kamera fehlgeschlagen (PTP Code 0x{code:X4}).");
                    return new CapturedPhoto(data, DateTimeOffset.Now);
                }
            }

            throw new CameraException("Nikon-Fotoauslösung: Kein neues Foto auf der Kamera gefunden.");
        }
        finally
        {
            _usbLock.Release();
        }
    }

    /// <summary>
    /// Polls the Nikon event queue (GetEvent) until the camera reports the new picture(s)
    /// (ObjectAddedInSdram / ObjectAdded). With RAW+JPEG two objects arrive shortly after each other.
    /// </summary>
    private async Task<List<uint>> WaitForNikonPicturesAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var handles = new List<uint>();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (code, data) = await ExecuteCommandWithDataAsync(PtpOcNikonGetEvent);
            if (code == PtpRcOk && data is { Length: >= 2 })
            {
                var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2));
                for (var i = 0; i < count && 2 + (i + 1) * 6 <= data.Length; i++)
                {
                    var eventCode = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2 + i * 6, 2));
                    var param = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + i * 6, 4));
                    if (eventCode is not (PtpEcObjectAdded or PtpEcNikonObjectAddedInSdram))
                        continue;

                    Log.Info(LogTag, $"Event 0x{eventCode:X4} (0x{param:X8})");
                    var handle = param != 0 ? param : NikonSdramObjectHandle;
                    if (!handles.Contains(handle))
                        handles.Add(handle);

                    // Give a possible second picture (RAW+JPEG) a moment to show up
                    var shortDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(800);
                    if (shortDeadline < deadline)
                        deadline = shortDeadline;
                }
            }
            await Task.Delay(100, cancellationToken);
        }

        if (handles.Count == 0)
            Log.Warn(LogTag, "No ObjectAdded event - the camera did not take a picture");
        return handles;
    }

    /// <summary>Polls Nikon DeviceReady until the camera is no longer busy (e.g. after capture or live view changes).</summary>
    private async Task<ushort> WaitDeviceReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (code, _) = await ExecuteCommandNoDataAsync(PtpOcNikonDeviceReady);
            if (code != PtpRcDeviceBusy)
                return code;
            await Task.Delay(50, cancellationToken);
        }
        Log.Warn(LogTag, "Camera still busy after waiting for DeviceReady");
        return PtpRcDeviceBusy;
    }

    /// <summary>
    /// PTP/USB "Device Reset" class request plus clearing stalled endpoints and draining stale data,
    /// so the camera accepts a fresh command sequence.
    /// </summary>
    private async Task ResetPtpStateAsync(CancellationToken cancellationToken)
    {
        var connection = _connection!;
        const int ClearFeature = 0x01, EndpointHalt = 0x00;
        connection.ControlTransfer((UsbAddressing)0x02, ClearFeature, EndpointHalt, (int)_bulkIn!.Address, null, 0, 1000);
        connection.ControlTransfer((UsbAddressing)0x02, ClearFeature, EndpointHalt, (int)_bulkOut!.Address, null, 0, 1000);

        var reset = connection.ControlTransfer((UsbAddressing)0x21, 0x66 /* Device Reset */, 0, _interface!.Id, null, 0, 2000);
        await Task.Delay(300, cancellationToken);

        // PTP/USB spec: after a reset poll "Get Device Status" until the camera reports OK again
        var status = new byte[32];
        ushort statusCode = 0;
        for (var i = 0; i < 30; i++)
        {
            var len = connection.ControlTransfer((UsbAddressing)0xA1, 0x67 /* Get Device Status */, 0, _interface.Id, status, status.Length, 1000);
            if (len < 4) break;
            statusCode = BinaryPrimitives.ReadUInt16LittleEndian(status.AsSpan(2, 2));
            if (statusCode == PtpRcOk) break;
            if (statusCode == 0x2019 /* busy */ || len > 4)
            {
                // Status lists stalled endpoints after the header: clear them
                connection.ControlTransfer((UsbAddressing)0x02, ClearFeature, EndpointHalt, (int)_bulkIn.Address, null, 0, 1000);
                connection.ControlTransfer((UsbAddressing)0x02, ClearFeature, EndpointHalt, (int)_bulkOut.Address, null, 0, 1000);
            }
            await Task.Delay(100, cancellationToken);
        }
        Log.Info(LogTag, $"PTP device status after reset: 0x{statusCode:X4}");

        var drain = new byte[64 * 1024];
        var drained = 0;
        for (var i = 0; i < 20; i++)
        {
            var read = connection.BulkTransfer(_bulkIn, drain, drain.Length, 100);
            if (read <= 0) break;
            drained += read;
        }
        Log.Info(LogTag, $"PTP reset: device reset {(reset >= 0 ? "ok" : "not supported")}, drained {drained} bytes");
    }

    /// <summary>Starts Nikon live view and waits until the camera reports ready (mirror up, sensor running).</summary>
    private async Task StartNikonLiveViewAsync(CancellationToken token)
    {
        var (startCode, _) = await ExecuteCommandNoDataAsync(PtpOcNikonStartLiveView);
        Log.Info(LogTag, $"StartLiveView: 0x{startCode:X4}");

        for (var attempt = 0; attempt < 40; attempt++)
        {
            var (readyCode, _) = await ExecuteCommandNoDataAsync(PtpOcNikonDeviceReady);
            if (readyCode != PtpRcDeviceBusy)
            {
                Log.Info(LogTag, $"DeviceReady after {attempt} polls: 0x{readyCode:X4}");
                return;
            }
            await Task.Delay(50, token);
        }
        Log.Warn(LogTag, "Camera stayed busy after StartLiveView");
    }

    private async Task<List<uint>> GetObjectHandlesAsync()
    {
        var result = new List<uint>();
        // GetObjectHandles: StorageId = 0xFFFFFFFF (all), ObjectFormatCode = 0 (all), ObjectHandleAssociation = 0
        var (code, data) = await ExecuteCommandWithDataAsync(PtpOcGetObjectHandles, 0xFFFFFFFF, 0x00000000, 0x00000000);
        if (code == PtpRcOk && data != null && data.Length >= 4)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
            for (int i = 0; i < count && (4 + (i * 4) + 4) <= data.Length; i++)
            {
                var handle = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + (i * 4), 4));
                result.Add(handle);
            }
        }
        return result;
    }

    private static (int Start, int End) FindJpegMarkers(byte[] data)
    {
        int start = -1;
        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD8)
            {
                start = i;
                break;
            }
        }

        if (start < 0) return (-1, -1);

        int end = -1;
        for (int i = data.Length - 2; i > start; i--)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD9)
            {
                end = i;
                break;
            }
        }

        return (start, end);
    }

    private Task<(ushort Code, byte[]? Data)> ExecuteCommandNoDataAsync(ushort opcode, params uint[] parameters)
    {
        if (_connection is null || _bulkOut is null || _bulkIn is null)
            throw new CameraException("USB-Verbindung nicht initialisiert.");

        var cmdPacket = CreateCommandPacket(opcode, parameters);
        var sent = _connection.BulkTransfer(_bulkOut, cmdPacket, cmdPacket.Length, 3000);
        if (sent < 0)
        {
            throw new CameraException($"USB-Fehler beim Senden des Befehls 0x{opcode:X4}");
        }

        var respBuffer = new byte[512];
        var received = _connection.BulkTransfer(_bulkIn, respBuffer, respBuffer.Length, 5000);
        if (received < 12)
        {
            throw new CameraException($"USB-Fehler oder Timeout beim Empfang der Antwort auf 0x{opcode:X4}");
        }

        var responseCode = BinaryPrimitives.ReadUInt16LittleEndian(respBuffer.AsSpan(6, 2));
        return Task.FromResult<(ushort, byte[]?)>((responseCode, null));
    }

    private Task<(ushort Code, byte[]? Data)> ExecuteCommandWithDataAsync(ushort opcode, params uint[] parameters)
    {
        if (_connection is null || _bulkOut is null || _bulkIn is null)
            throw new CameraException("USB-Verbindung nicht initialisiert.");

        var cmdPacket = CreateCommandPacket(opcode, parameters);
        var sent = _connection.BulkTransfer(_bulkOut, cmdPacket, cmdPacket.Length, 3000);
        if (sent < 0)
        {
            throw new CameraException($"USB-Fehler beim Senden des Befehls 0x{opcode:X4}");
        }

        // Read Data packet header
        var initialChunk = new byte[64 * 1024];
        var bytesRead = _connection.BulkTransfer(_bulkIn, initialChunk, initialChunk.Length, 5000);
        if (bytesRead == 0)
        {
            // Stale zero-length packet from the previous transfer
            bytesRead = _connection.BulkTransfer(_bulkIn, initialChunk, initialChunk.Length, 5000);
        }
        if (bytesRead < 12)
        {
            return Task.FromResult<(ushort, byte[]?)>((0x2002, null));
        }

        var containerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(initialChunk.AsSpan(0, 4));
        var containerType = BinaryPrimitives.ReadUInt16LittleEndian(initialChunk.AsSpan(4, 2));

        // If camera returned response directly instead of data (e.g. error or busy)
        if (containerType == 3) // Response container
        {
            var respCode = BinaryPrimitives.ReadUInt16LittleEndian(initialChunk.AsSpan(6, 2));
            return Task.FromResult<(ushort, byte[]?)>((respCode, null));
        }

        // It is a Data container (containerType == 2)
        var payloadLength = containerLength - 12;
        var fullPayload = new byte[payloadLength];
        var payloadRead = Math.Min(payloadLength, bytesRead - 12);
        Buffer.BlockCopy(initialChunk, 12, fullPayload, 0, payloadRead);

        // Continue reading remaining data blocks if data is larger than first chunk
        while (payloadRead < payloadLength)
        {
            var chunk = new byte[Math.Min(128 * 1024, payloadLength - payloadRead)];
            var read = _connection.BulkTransfer(_bulkIn, chunk, chunk.Length, 8000);
            if (read <= 0) break;

            Buffer.BlockCopy(chunk, 0, fullPayload, payloadRead, read);
            payloadRead += read;
        }

        // After Data block, read the final Response Block (Type == 3).
        // A data phase that is an exact multiple of the packet size is followed by a zero-length packet - skip it,
        // otherwise the response stays in the pipe and every later command reads the previous answer.
        var finalResp = new byte[512];
        var respBytes = _connection.BulkTransfer(_bulkIn, finalResp, finalResp.Length, 3000);
        if (respBytes == 0)
            respBytes = _connection.BulkTransfer(_bulkIn, finalResp, finalResp.Length, 3000);
        ushort finalCode = PtpRcOk;
        if (respBytes >= 12)
        {
            finalCode = BinaryPrimitives.ReadUInt16LittleEndian(finalResp.AsSpan(6, 2));
        }

        return Task.FromResult<(ushort, byte[]?)>((finalCode, fullPayload));
    }

    private byte[] CreateCommandPacket(ushort opcode, params uint[] parameters)
    {
        var length = 12 + (parameters.Length * 4);
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4, 2), 1); // 1 = Command Container
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6, 2), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), _transactionId++);

        for (int i = 0; i < parameters.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12 + (i * 4), 4), parameters[i]);
        }

        return packet;
    }

    public async ValueTask DisposeAsync()
    {
        Log.Info(LogTag, "Disposing PTP connection");
        await StopLiveViewAsync();

        if (_connection is not null)
        {
            try
            {
                await _usbLock.WaitAsync(CancellationToken.None);
                try
                {
                    await ExecuteCommandNoDataAsync(PtpOcCloseSession);
                }
                catch { }
                finally
                {
                    _usbLock.Release();
                }

                if (_interface is not null)
                {
                    _connection.ReleaseInterface(_interface);
                }
                _connection.Close();
            }
            catch { }

            _connection = null;
        }

        Status = CameraStatus.Disconnected;
        Log.Info(LogTag, "PTP connection closed");
    }
}
