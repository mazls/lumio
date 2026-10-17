using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Debug = System.Diagnostics.Debug;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;
using Lumio.Core.Camera;
using Lumio.Core.Settings;

namespace Lumio.App.Android;

/// <summary>
/// Native Android USB-OTG PTP driver for Nikon DSLRs and Mirrorless cameras (e.g. D5600, Z 50 II).
/// Communicates directly with the camera over USB Host bulk endpoints (zero external native libraries required).
/// </summary>
public sealed class AndroidNikonPtpCamera : ICameraService
{
    private const int NikonVendorId = 0x04B0; // 1200 decimal

    // Standard PTP Opcodes
    private const ushort PtpOcGetDeviceInfo = 0x1001;
    private const ushort PtpOcOpenSession = 0x1002;
    private const ushort PtpOcCloseSession = 0x1003;
    private const ushort PtpOcGetObjectHandles = 0x1007;
    private const ushort PtpOcGetObject = 0x1009;
    private const ushort PtpOcInitiateCapture = 0x100E;

    // Nikon-specific PTP Opcodes
    private const ushort PtpOcNikonCapture = 0x90C0;
    private const ushort PtpOcNikonStartLiveView = 0x9201;
    private const ushort PtpOcNikonGetLiveViewImage = 0x9202;
    private const ushort PtpOcNikonEndLiveView = 0x9203;
    private const ushort PtpOcNikonDeviceReady = 0x9207;

    // Nikon stores pictures in camera RAM (no SD card / "PC" destination) under this fixed handle
    private const uint NikonSdramObjectHandle = 0xFFFF0001;

    // PTP Response Codes
    private const ushort PtpRcOk = 0x2001;
    private const ushort PtpRcDeviceBusy = 0x2019;

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
                        results.Add(new CameraDeviceInfo($"ptp:nikon:{dev.DeviceId}", $"📷 {name} (USB-OTG PTP)"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error scanning Nikon USB devices: {ex.Message}");
        }

        return results;
    }

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
            if (!string.IsNullOrWhiteSpace(_settings?.WebcamDeviceId) &&
                _settings.WebcamDeviceId.StartsWith("ptp:nikon:", StringComparison.OrdinalIgnoreCase))
            {
                var idStr = _settings.WebcamDeviceId.Substring("ptp:nikon:".Length);
                if (int.TryParse(idStr, out var requestedId))
                {
                    targetDevice = usbManager.DeviceList.Values.FirstOrDefault(d => d.DeviceId == requestedId);
                }
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
            _transactionId = 1;
            ushort responseCode = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var (code, _) = await ExecuteCommandNoDataAsync(PtpOcOpenSession, 1);
                    responseCode = code;
                    if (code == PtpRcOk || code == 0x201E) // 0x201E: SessionAlreadyOpen
                    {
                        break;
                    }
                }
                catch when (attempt < 2)
                {
                    await Task.Delay(300, cancellationToken);
                }
            }

            Status = CameraStatus.Connected;
        }
        catch (Exception ex)
        {
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
                await ExecuteCommandNoDataAsync(PtpOcNikonStartLiveView);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Nikon StartLiveView error: {ex.Message}");
            }
            finally
            {
                _usbLock.Release();
            }

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
                        var (_, data) = await ExecuteCommandWithDataAsync(PtpOcNikonGetLiveViewImage);
                        payload = data;
                    }
                    finally
                    {
                        _usbLock.Release();
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

                    await Task.Delay(35, token); // ~28 FPS
                }
                catch (System.OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LiveView loop frame error: {ex.Message}");
                    await Task.Delay(100, token);
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

        try
        {
            // Give in-flight live view frame a moment to finish
            await Task.Delay(100, cancellationToken);

            // Temporarily stop live view so DSLR mirror can flip down and shutter can actuate cleanly
            bool hadLiveView = _isLiveViewRunning;
            if (hadLiveView)
            {
                await _usbLock.WaitAsync(cancellationToken);
                try
                {
                    await ExecuteCommandNoDataAsync(PtpOcNikonEndLiveView);
                }
                catch { }
                finally
                {
                    _usbLock.Release();
                }

                await Task.Delay(150, cancellationToken);
            }

            // Fetch known object handles before capture
            var existingHandles = new HashSet<uint>();
            await _usbLock.WaitAsync(cancellationToken);
            try
            {
                var handles = await GetObjectHandlesAsync();
                foreach (var h in handles) existingHandles.Add(h);
            }
            finally
            {
                _usbLock.Release();
            }

            // Trigger shutter / photo capture
            await _usbLock.WaitAsync(cancellationToken);
            try
            {
                // Try Nikon Capture command (0x90C0)
                var (code, _) = await ExecuteCommandNoDataAsync(PtpOcNikonCapture, 0xFFFFFFFF, 0x00000000);
                if (code != PtpRcOk && code != PtpRcDeviceBusy)
                {
                    // Fallback to standard PTP InitiateCapture (0x100E)
                    await ExecuteCommandNoDataAsync(PtpOcInitiateCapture, 0x00000000, 0x00000000);
                }
            }
            finally
            {
                _usbLock.Release();
            }

            // Wait for camera to write picture to storage and find newly created object handle
            uint newObjectHandle = 0;
            for (int attempt = 0; attempt < 35; attempt++) // up to 10.5 seconds
            {
                await Task.Delay(300, cancellationToken);

                await _usbLock.WaitAsync(cancellationToken);
                try
                {
                    var currentHandles = await GetObjectHandlesAsync();
                    var newHandles = currentHandles.Where(h => !existingHandles.Contains(h)).ToList();
                    if (newHandles.Count > 0)
                    {
                        newObjectHandle = newHandles.Last();
                        break;
                    }
                }
                catch { }
                finally
                {
                    _usbLock.Release();
                }
            }

            // No new file on the card: the camera may have kept the picture in its RAM buffer
            newObjectHandle = newObjectHandle == 0 ? NikonSdramObjectHandle : newObjectHandle;

            // Download the high-resolution JPEG (GetObject 0x1009)
            byte[]? photoBytes;
            await _usbLock.WaitAsync(cancellationToken);
            try
            {
                var (code, data) = await ExecuteCommandWithDataAsync(PtpOcGetObject, newObjectHandle);
                if (code != PtpRcOk || data is null || data.Length == 0)
                {
                    throw newObjectHandle == NikonSdramObjectHandle
                        ? new CameraException("Nikon-Fotoauslösung: Kein neues Foto auf der Kamera gefunden.")
                        : new CameraException($"Foto-Übertragung von der Kamera fehlgeschlagen (PTP Code 0x{code:X4}).");
                }
                photoBytes = data;
            }
            finally
            {
                _usbLock.Release();
            }

            return new CapturedPhoto(photoBytes, DateTimeOffset.Now);
        }
        finally
        {
            _isCapturing = false;

            // Resume Live View if it was running
            if (_liveViewTask != null && Status == CameraStatus.Connected)
            {
                await _usbLock.WaitAsync(CancellationToken.None);
                try
                {
                    await ExecuteCommandNoDataAsync(PtpOcNikonStartLiveView);
                }
                catch { }
                finally
                {
                    _usbLock.Release();
                }
            }
        }
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

        // After Data block, read the final Response Block (Type == 3)
        var finalResp = new byte[512];
        var respBytes = _connection.BulkTransfer(_bulkIn, finalResp, finalResp.Length, 3000);
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
    }
}
