using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Usb;
using Lumio.Core.Camera;
using Lumio.Core.Settings;
using Log = Android.Util.Log;

namespace Lumio.App.Android;

/// <summary>
/// User-space USB Video Class (UVC) driver for cameras in "USB webcam" mode (e.g. Nikon Z 50 II via UVC/UAC).
/// Many phones (e.g. Google Pixel) do not expose external UVC cameras through Camera2, so the video stream is
/// negotiated and read directly over the USB host API. Supports bulk streaming (e.g. Nikon Z 50 II) and isochronous
/// streaming (most webcams, e.g. Anker PowerConf C200, via <see cref="UsbIsoStream"/>) with MJPEG or YUY2.
/// </summary>
public sealed class AndroidUvcCamera : ICameraService
{
    private const string LogTag = "LumioUvc";

    // USB / UVC constants
    private const int UsbClassVideo = 14;
    private const int SubclassVideoControl = 1;
    private const int SubclassVideoStreaming = 2;
    private const byte CsInterface = 0x24;
    private const byte VcHeader = 0x01;
    private const byte VsFormatUncompressed = 0x04;
    private const byte VsFrameUncompressed = 0x05;
    private const byte VsFormatMjpeg = 0x06;
    private const byte VsFrameMjpeg = 0x07;
    private const byte SetCur = 0x01;
    private const byte GetCur = 0x81;
    private const int VsProbeControl = 0x01;
    private const int VsCommitControl = 0x02;

    // Live view/photo resolution cap: 1080p keeps MJPEG decoding fast enough for a fluid live view.
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1080;

    private readonly Activity _activity;
    private readonly BoothSettings? _settings;
    private readonly object _frameLock = new();

    private UsbDevice? _device;
    private UsbDeviceConnection? _connection;
    private UsbInterface? _controlInterface;
    private UsbInterface? _streamInterface;
    private UsbEndpoint? _streamEndpoint;
    private StreamFormat? _format;
    private int _maxPayloadSize;
    private int _uvcVersion;
    private bool _streamStopped;

    // Isochronous cameras: alternate settings with the video endpoint (alternate setting 0 = no bandwidth)
    private sealed record IsoSetting(UsbInterface Interface, UsbEndpoint Endpoint, int PacketSize);
    private readonly List<IsoSetting> _isoSettings = [];
    private readonly HashSet<int> _stalledAltSettings = [];
    private IsoSetting? _isoActive;
    private UsbIsoStream? _pendingIsoStream;
    private bool UsesIso => _streamEndpoint is null && _isoSettings.Count > 0;

    // No frame for this long with an alternate setting means it does not work for this camera
    private const int IsoStallTimeoutMs = 5000;

    // A format that had to be lowered (bandwidth) is kept for reconnects of the same camera model
    private static readonly Dictionary<string, (byte FormatIndex, byte FrameIndex)> FormatOverrides = [];
    private List<StreamFormat> _formats = [];
    private string _deviceKey = "";
    private long _lastFrameTicks;

    private CameraStatus _status = CameraStatus.Disconnected;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private byte[]? _latestJpeg;
    private TaskCompletionSource<byte[]>? _nextFrameTcs;

    public AndroidUvcCamera(Activity activity, BoothSettings? settings = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _settings = settings;
    }

    public string DisplayName { get; private set; } = "USB-Kamera (UVC)";

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

    private sealed record StreamFormat(bool IsMjpeg, byte FormatIndex, byte FrameIndex, int Width, int Height, uint FrameInterval);

    #region Discovery

    public static bool IsUvcDevice(UsbDevice dev)
    {
        for (var i = 0; i < dev.InterfaceCount; i++)
        {
            var iface = dev.GetInterface(i);
            if ((int)iface.InterfaceClass == UsbClassVideo && (int)iface.InterfaceSubclass == SubclassVideoStreaming)
                return true;
        }
        return false;
    }

    public static bool IsDeviceConnected(Activity activity) => FindDevices(activity).Count > 0;

    public static IReadOnlyList<CameraDeviceInfo> FindConnectedDevices(Activity activity) =>
        FindDevices(activity)
            .Select(d => new CameraDeviceInfo($"uvc:{d.VendorId:X4}:{d.ProductId:X4}", $"🎥 {GetModelName(d)} (USB-Webcam / UVC)"))
            .ToList();

    private static List<UsbDevice> FindDevices(Activity activity)
    {
        try
        {
            var usbManager = (UsbManager?)activity.GetSystemService(Context.UsbService);
            return usbManager?.DeviceList?.Values.Where(IsUvcDevice).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"UVC scan failed: {ex.Message}");
            return [];
        }
    }

    private static string GetModelName(UsbDevice dev)
    {
        if (dev.VendorId == 0x04B0 && dev.ProductId == 0x0455)
            return "Nikon Z 50 II";

        var manufacturer = dev.ManufacturerName?.Trim();
        var product = dev.ProductName?.Trim();
        var name = !string.IsNullOrEmpty(product) && !string.IsNullOrEmpty(manufacturer) && product.Contains(manufacturer, StringComparison.OrdinalIgnoreCase)
            ? product
            : string.Join(" ", new[] { manufacturer, product }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(name) ? "USB-Kamera" : name.Trim();
    }

    #endregion

    #region Connection

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Status == CameraStatus.Connected)
            return;

        Status = CameraStatus.Connecting;
        try
        {
            var usbManager = (UsbManager?)_activity.GetSystemService(Context.UsbService)
                             ?? throw new CameraException("Android USB-Dienst nicht verfügbar.");

            _device = SelectDevice(usbManager) ?? throw new CameraException(
                "Keine USB-Webcam (UVC) gefunden.\n\n" +
                "Bitte prüfen:\n" +
                "1. Ist die Kamera eingeschaltet und per USB-C verbunden?\n" +
                "2. Nikon Z 50 II: Im Kameramenü unter USB die Option 'UVC/UAC' (USB-Webcam) wählen.");
            DisplayName = GetModelName(_device);

            if (!await UsbPermission.RequestAsync(_activity, usbManager, _device, cancellationToken).ConfigureAwait(false))
                throw new CameraException($"USB-Zugriff auf {DisplayName} wurde verweigert.");

            _connection = usbManager.OpenDevice(_device) ?? throw new CameraException($"USB-Verbindung zu {DisplayName} konnte nicht geöffnet werden.");

            var raw = _connection.GetRawDescriptors() ?? [];
            var isoPacketSizes = ParseIsoPacketSizes(raw);
            UsbInterface? streamingBase = null;
            for (var i = 0; i < _device.InterfaceCount; i++)
            {
                var iface = _device.GetInterface(i);
                if ((int)iface.InterfaceClass != UsbClassVideo) continue;
                if ((int)iface.InterfaceSubclass == SubclassVideoControl) _controlInterface ??= iface;
                if ((int)iface.InterfaceSubclass != SubclassVideoStreaming) continue;

                if (iface.AlternateSetting == 0)
                    streamingBase ??= iface;
                if (FindInEndpoint(iface, UsbAddressing.XferBulk) is { } bulk)
                {
                    _streamInterface = iface;
                    _streamEndpoint = bulk;
                }
                else if (FindInEndpoint(iface, UsbAddressing.XferIsochronous) is { } iso)
                {
                    var packetSize = isoPacketSizes.TryGetValue((iface.Id, iface.AlternateSetting), out var size) ? size : IsoPacketSize(iso);
                    _isoSettings.Add(new IsoSetting(iface, iso, packetSize));
                }
            }

            if (_streamEndpoint is null && _isoSettings.Count > 0)
                _streamInterface = streamingBase ?? _isoSettings[0].Interface;

            if (_streamInterface is null || (_streamEndpoint is null && _isoSettings.Count == 0))
                throw new CameraException($"{DisplayName}: Kein unterstützter Video-Stream gefunden.");

            if (_controlInterface is not null)
                _connection.ClaimInterface(_controlInterface, true);
            if (!_connection.ClaimInterface(_streamInterface, true))
                throw new CameraException($"{DisplayName}: Video-Schnittstelle konnte nicht reserviert werden.");

            var (uvcVersion, formats) = ParseDescriptors(raw, _streamInterface.Id);
            _formats = formats;
            _deviceKey = $"{_device.VendorId:X4}:{_device.ProductId:X4}";
            _format = PreferredFormat(formats) ?? throw new CameraException($"{DisplayName}: Kein MJPEG- oder YUY2-Videoformat verfügbar.");
            Log.Info(LogTag, $"{DisplayName}: UVC {uvcVersion:X4}, {formats.Count} Formate, gewählt {(_format.IsMjpeg ? "MJPEG" : "YUY2")} {_format.Width}x{_format.Height} @ {10_000_000.0 / Math.Max(1, _format.FrameInterval):0.#} fps");

            _uvcVersion = uvcVersion;
            _maxPayloadSize = NegotiateStream(uvcVersion, _format);
            Status = CameraStatus.Connected;
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, $"Connect failed: {ex}");
            CloseConnection();
            Status = CameraStatus.Error;
            throw ex as CameraException ?? new CameraException($"USB-Webcam konnte nicht verbunden werden: {ex.Message}", ex);
        }
    }

    private UsbDevice? SelectDevice(UsbManager usbManager)
    {
        var devices = usbManager.DeviceList?.Values.Where(IsUvcDevice).ToList() ?? [];
        var requested = _settings?.WebcamDeviceId;
        if (requested?.StartsWith("uvc:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var match = devices.FirstOrDefault(d => string.Equals(requested, $"uvc:{d.VendorId:X4}:{d.ProductId:X4}", StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return devices.FirstOrDefault();
    }

    /// <summary>
    /// Bytes per (micro)frame of an isochronous endpoint. Android returns the raw wMaxPacketSize, where bits 11-12
    /// hold the extra transactions per microframe of high-bandwidth endpoints (e.g. 0x0C00 = 2 x 1024 bytes).
    /// </summary>
    private static int IsoPacketSize(UsbEndpoint endpoint)
    {
        var raw = endpoint.MaxPacketSize;
        return (raw & 0x7FF) * (((raw >> 11) & 0x3) + 1);
    }

    /// <summary>
    /// Bytes per service interval of every isochronous IN endpoint, keyed by (interface, alternate setting). Unlike
    /// <see cref="IsoPacketSize"/> this also covers SuperSpeed cameras, whose burst size is only in the endpoint
    /// companion descriptor that Android's UsbEndpoint does not expose.
    /// </summary>
    private static Dictionary<(int Interface, int AltSetting), int> ParseIsoPacketSizes(byte[] raw)
    {
        var sizes = new Dictionary<(int, int), int>();
        int currentInterface = -1, currentAlt = -1, pendingMaxPacket = -1;

        for (var pos = 0; pos + 2 <= raw.Length;)
        {
            int len = raw[pos];
            if (len < 2 || pos + len > raw.Length) break;
            var type = raw[pos + 1];
            var d = raw.AsSpan(pos, len);

            if (type == 0x04 && len >= 9) // INTERFACE
            {
                currentInterface = d[2];
                currentAlt = d[3];
                pendingMaxPacket = -1;
            }
            else if (type == 0x05 && len >= 7) // ENDPOINT
            {
                var isIsoIn = (d[2] & 0x80) != 0 && (d[3] & 0x03) == 0x01;
                pendingMaxPacket = isIsoIn ? BinaryPrimitives.ReadUInt16LittleEndian(d[4..6]) : -1;
                if (isIsoIn)
                    sizes[(currentInterface, currentAlt)] = (pendingMaxPacket & 0x7FF) * (((pendingMaxPacket >> 11) & 0x3) + 1);
            }
            else if (type == 0x30 && len >= 6 && pendingMaxPacket >= 0) // SUPERSPEED ENDPOINT COMPANION
            {
                var bytesPerInterval = BinaryPrimitives.ReadUInt16LittleEndian(d[4..6]);
                var computed = (pendingMaxPacket & 0x7FF) * (d[2] + 1) * ((d[3] & 0x03) + 1);
                sizes[(currentInterface, currentAlt)] = bytesPerInterval > 0 ? Math.Min(bytesPerInterval, computed) : computed;
            }
            else if (type == 0x31 && len >= 8 && pendingMaxPacket >= 0) // SUPERSPEEDPLUS ISOCHRONOUS COMPANION
            {
                sizes[(currentInterface, currentAlt)] = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[4..8]);
            }

            pos += len;
        }
        return sizes;
    }

    private static UsbEndpoint? FindInEndpoint(UsbInterface iface, UsbAddressing type)
    {
        for (var i = 0; i < iface.EndpointCount; i++)
        {
            var ep = iface.GetEndpoint(i);
            if (ep is not null && ep.Type == type && ep.Direction == UsbAddressing.In)
                return ep;
        }
        return null;
    }

    /// <summary>Parses the UVC class-specific descriptors of the configuration.</summary>
    private static (int UvcVersion, List<StreamFormat> Formats) ParseDescriptors(byte[] raw, int streamInterfaceNumber)
    {
        var formats = new List<StreamFormat>();
        var uvcVersion = 0x0100;
        int currentClass = -1, currentSubclass = -1, currentInterface = -1;
        byte currentFormatIndex = 0;
        var currentIsMjpeg = false;
        var currentIsYuy2 = false;

        for (var pos = 0; pos + 2 <= raw.Length;)
        {
            int len = raw[pos];
            if (len < 2 || pos + len > raw.Length) break;
            var type = raw[pos + 1];
            var d = raw.AsSpan(pos, len);

            if (type == 0x04 && len >= 9) // INTERFACE descriptor
            {
                currentInterface = d[2];
                currentClass = d[5];
                currentSubclass = d[6];
            }
            else if (type == CsInterface && len >= 3 && currentClass == UsbClassVideo)
            {
                var subtype = d[2];
                if (currentSubclass == SubclassVideoControl && subtype == VcHeader && len >= 5)
                {
                    uvcVersion = BinaryPrimitives.ReadUInt16LittleEndian(d[3..5]);
                }
                else if (currentSubclass == SubclassVideoStreaming && currentInterface == streamInterfaceNumber)
                {
                    switch (subtype)
                    {
                        case VsFormatMjpeg when len >= 5:
                            currentFormatIndex = d[3];
                            currentIsMjpeg = true;
                            currentIsYuy2 = false;
                            break;
                        case VsFormatUncompressed when len >= 21:
                            currentFormatIndex = d[3];
                            currentIsMjpeg = false;
                            currentIsYuy2 = d[5] == (byte)'Y' && d[6] == (byte)'U' && d[7] == (byte)'Y' && d[8] == (byte)'2';
                            break;
                        case VsFrameMjpeg or VsFrameUncompressed when len >= 26:
                            if ((subtype == VsFrameMjpeg && currentIsMjpeg) || (subtype == VsFrameUncompressed && currentIsYuy2))
                            {
                                var width = BinaryPrimitives.ReadUInt16LittleEndian(d[5..7]);
                                var height = BinaryPrimitives.ReadUInt16LittleEndian(d[7..9]);
                                var interval = ChooseFrameInterval(d);
                                formats.Add(new StreamFormat(currentIsMjpeg, currentFormatIndex, d[3], width, height, interval));
                            }
                            break;
                        default:
                            if (subtype is 0x10 or 0x0C or 0x12) // frame-based / stream-based formats (H.264 etc.) are not supported
                            {
                                currentIsMjpeg = false;
                                currentIsYuy2 = false;
                            }
                            break;
                    }
                }
            }

            pos += len;
        }

        return (uvcVersion, formats);
    }

    /// <summary>Picks ~30 fps if offered, otherwise the default interval of the frame descriptor.</summary>
    private static uint ChooseFrameInterval(ReadOnlySpan<byte> frameDescriptor)
    {
        var defaultInterval = BinaryPrimitives.ReadUInt32LittleEndian(frameDescriptor[21..25]);
        var intervalType = frameDescriptor[25];
        const uint thirtyFps = 333_333;

        if (intervalType == 0)
        {
            // Continuous range: min, max, step
            if (frameDescriptor.Length >= 38)
            {
                var min = BinaryPrimitives.ReadUInt32LittleEndian(frameDescriptor[26..30]);
                var max = BinaryPrimitives.ReadUInt32LittleEndian(frameDescriptor[30..34]);
                if (thirtyFps >= min && thirtyFps <= max) return thirtyFps;
            }
            return defaultInterval;
        }

        uint best = defaultInterval;
        for (var i = 0; i < intervalType && 26 + (i + 1) * 4 <= frameDescriptor.Length; i++)
        {
            var interval = BinaryPrimitives.ReadUInt32LittleEndian(frameDescriptor.Slice(26 + i * 4, 4));
            if (Math.Abs((long)interval - thirtyFps) < Math.Abs((long)best - thirtyFps))
                best = interval;
        }
        return best;
    }

    /// <summary>MJPEG is preferred (small transfers), largest resolution within the 1080p cap.</summary>
    private static StreamFormat? ChooseFormat(List<StreamFormat> formats) =>
        formats
            .Where(f => f.Width <= MaxWidth && f.Height <= MaxHeight)
            .OrderByDescending(f => f.IsMjpeg)
            .ThenByDescending(f => f.Width * f.Height)
            .ThenBy(f => f.FrameInterval)
            .FirstOrDefault()
        ?? formats.OrderByDescending(f => f.IsMjpeg).ThenBy(f => f.Width * f.Height).FirstOrDefault();

    private StreamFormat? PreferredFormat(List<StreamFormat> formats)
    {
        lock (FormatOverrides)
        {
            if (FormatOverrides.TryGetValue(_deviceKey, out var known)
                && formats.FirstOrDefault(f => f.FormatIndex == known.FormatIndex && f.FrameIndex == known.FrameIndex) is { } format)
            {
                return format;
            }
        }
        return ChooseFormat(formats);
    }

    /// <summary>
    /// Switches to the next smaller video format (less USB bandwidth), e.g. when a USB hub or the phone cannot
    /// reserve enough bandwidth for 1080p. Returns false if there is no smaller format.
    /// </summary>
    private bool TryLowerFormat()
    {
        var current = _format!;
        var next = _formats
            .Where(f => f.Width * f.Height < current.Width * current.Height)
            .OrderByDescending(f => f.IsMjpeg)
            .ThenByDescending(f => f.Width * f.Height)
            .ThenBy(f => f.FrameInterval)
            .FirstOrDefault();
        if (next is null)
            return false;

        Log.Warn(LogTag, $"Falling back from {current.Width}x{current.Height} to {(next.IsMjpeg ? "MJPEG" : "YUY2")} {next.Width}x{next.Height}");
        _format = next;
        _stalledAltSettings.Clear();
        _maxPayloadSize = NegotiateStream(_uvcVersion, next);
        lock (FormatOverrides)
            FormatOverrides[_deviceKey] = (next.FormatIndex, next.FrameIndex);
        return true;
    }

    /// <summary>UVC probe/commit negotiation. Returns the maximum payload transfer size.</summary>
    private int NegotiateStream(int uvcVersion, StreamFormat format)
    {
        var length = uvcVersion >= 0x0150 ? 48 : uvcVersion >= 0x0110 ? 34 : 26;
        var probe = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(probe.AsSpan(0, 2), 0x0001); // bmHint: keep dwFrameInterval fixed
        probe[2] = format.FormatIndex;
        probe[3] = format.FrameIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(probe.AsSpan(4, 4), format.FrameInterval);

        var iface = _streamInterface!.Id;
        var connection = _connection!;

        if (connection.ControlTransfer((UsbAddressing)0x21, SetCur, VsProbeControl << 8, iface, probe, length, 2000) < 0)
            throw new CameraException($"{DisplayName}: Video-Format wurde abgelehnt (UVC Probe).");

        var negotiated = new byte[length];
        if (connection.ControlTransfer((UsbAddressing)0xA1, GetCur, VsProbeControl << 8, iface, negotiated, length, 2000) < 26)
            negotiated = probe;

        if (connection.ControlTransfer((UsbAddressing)0x21, SetCur, VsCommitControl << 8, iface, negotiated, length, 2000) < 0)
            throw new CameraException($"{DisplayName}: Video-Stream konnte nicht gestartet werden (UVC Commit).");

        var maxFrameSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(negotiated.AsSpan(18, 4));
        var maxPayload = (int)BinaryPrimitives.ReadUInt32LittleEndian(negotiated.AsSpan(22, 4));
        Log.Info(LogTag, $"Negotiated: frame {maxFrameSize} B, payload {maxPayload} B");

        // Isochronous: the alternate setting (bandwidth) is selected when the stream starts, see OpenIsoStream
        if (_streamEndpoint is null)
            return maxPayload;

        // Selecting the (bulk) alternate setting starts the stream on many cameras; also clear a stale endpoint stall
        connection.SetInterface(_streamInterface);
        connection.ControlTransfer((UsbAddressing)0x02, 0x01 /* CLEAR_FEATURE */, 0 /* ENDPOINT_HALT */, (int)_streamEndpoint.Address, null, 0, 1000);

        // Bulk payloads are read in one transfer each; fall back to a sane buffer if the camera reports nothing useful.
        return maxPayload is > 0 and <= 8 * 1024 * 1024 ? maxPayload : Math.Max(maxFrameSize, 512 * 1024);
    }

    #endregion

    #region Streaming

    public Task StartLiveViewAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected)
            throw new CameraException($"{DisplayName} ist nicht verbunden.");
        if (_streamTask is not null)
            return Task.CompletedTask;

        // After a stop the camera ended its stream: negotiate again to restart it
        if (_streamStopped)
        {
            _maxPayloadSize = NegotiateStream(_uvcVersion, _format!);
            _streamStopped = false;
        }

        // Isochronous transfers are started here, not in the background: if no setting works at all the caller
        // gets a clear error instead of the camera reporting "disconnected" (which would reconnect forever)
        if (UsesIso)
        {
            try
            {
                _pendingIsoStream = OpenIsoStream();
            }
            catch
            {
                _streamStopped = true;
                throw;
            }
        }

        _streamCts = new CancellationTokenSource();
        var token = _streamCts.Token;
        _streamTask = Task.Factory.StartNew(() => StreamLoop(token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    private void StreamLoop(CancellationToken token)
    {
        if (UsesIso)
        {
            IsoStreamLoop(token);
            return;
        }

        var connection = _connection!;
        var endpoint = _streamEndpoint!;
        var format = _format!;
        var buffer = new byte[_maxPayloadSize];
        var assembler = new FrameAssembler(this, format);
        var lastDataTicks = Environment.TickCount64;
        var errorCount = 0;

        while (!token.IsCancellationRequested)
        {
            var read = connection.BulkTransfer(endpoint, buffer, buffer.Length, 1000);
            if (read < 0)
            {
                // Errors/timeouts are normal while the camera (re)starts the stream; 5 s without data means it is gone.
                if (++errorCount % 50 == 1)
                    Log.Warn(LogTag, $"Bulk read failed ({errorCount}x)");
                if (Environment.TickCount64 - lastDataTicks > 5000)
                {
                    Log.Warn(LogTag, "Stream stopped: no data from camera");
                    Status = CameraStatus.Disconnected;
                    break;
                }
                Thread.Sleep(20);
                continue;
            }

            lastDataTicks = Environment.TickCount64;
            assembler.AddPayload(buffer, read);
        }
    }

    /// <summary>
    /// Starts isochronous streaming. Webcams offer several alternate settings (bandwidths): the smallest one whose
    /// packets fit the negotiated payload is tried first, then the larger ones. If none can be started (e.g. not
    /// enough bandwidth behind a hub), the next smaller video format is negotiated and tried the same way.
    /// </summary>
    private UsbIsoStream OpenIsoStream()
    {
        var connection = _connection!;
        string? lastError = null;
        while (true)
        {
            var fitting = _isoSettings.Where(s => s.PacketSize >= _maxPayloadSize).OrderBy(s => s.PacketSize).ToList();
            // Some cameras report a bogus payload size: then the largest setting is the best guess
            var candidates = (fitting.Count > 0 ? fitting : [_isoSettings.MaxBy(s => s.PacketSize)!])
                .Where(s => !_stalledAltSettings.Contains(s.Interface.AlternateSetting))
                .ToList();

            foreach (var setting in candidates)
            {
                var alt = setting.Interface.AlternateSetting;
                if (!connection.SetInterface(setting.Interface))
                {
                    lastError = "nicht genug USB-Bandbreite";
                    Log.Warn(LogTag, $"Alt setting {alt}: bandwidth not available");
                    continue;
                }

                var stream = new UsbIsoStream(connection.FileDescriptor, (int)setting.Endpoint.Address, setting.PacketSize);
                try
                {
                    stream.Start();
                    _isoActive = setting;
                    Log.Info(LogTag, $"Isochronous alt setting {alt}, {setting.PacketSize} B per interval, payload {_maxPayloadSize} B");
                    return stream;
                }
                catch (Exception ex)
                {
                    stream.Dispose();
                    lastError = ex.Message;
                    Log.Warn(LogTag, $"Alt setting {alt} failed: {ex.Message}");
                }
            }

            connection.SetInterface(_streamInterface!);
            if (!TryLowerFormat())
                throw new CameraException($"{DisplayName}: Videostream konnte nicht gestartet werden ({lastError ?? "keine passende USB-Einstellung"}).");
        }
    }

    private void IsoStreamLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UsbIsoStream stream;
            try
            {
                stream = Interlocked.Exchange(ref _pendingIsoStream, null) ?? OpenIsoStream();
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"Isochronous stream failed: {ex.Message}");
                Status = CameraStatus.Error;
                return;
            }

            using (stream)
            {
                // Some cameras accept a setting but then only send empty packets: no frame for a few seconds means
                // the next larger setting (or a smaller format) is tried
                using var stalled = CancellationTokenSource.CreateLinkedTokenSource(token);
                Interlocked.Exchange(ref _lastFrameTicks, Environment.TickCount64);
                using var watchdog = new System.Threading.Timer(_ =>
                {
                    if (Environment.TickCount64 - Interlocked.Read(ref _lastFrameTicks) > IsoStallTimeoutMs)
                    {
                        try { stalled.Cancel(); } catch (ObjectDisposedException) { }
                    }
                }, null, 1000, 1000);

                var assembler = new FrameAssembler(this, _format!);
                try
                {
                    if (!stream.Run(assembler.AddPayload, assembler.MarkDamaged, stalled.Token))
                    {
                        Log.Warn(LogTag, "Isochronous stream ended: camera gone");
                        Status = CameraStatus.Disconnected;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(LogTag, $"Isochronous transfer failed: {ex.Message}");
                }
            }

            if (token.IsCancellationRequested)
                return;

            var alt = _isoActive?.Interface.AlternateSetting ?? -1;
            Log.Warn(LogTag, $"No frames with alt setting {alt}, trying the next one");
            _stalledAltSettings.Add(alt);
            try
            {
                _connection!.SetInterface(_streamInterface!);
                _maxPayloadSize = NegotiateStream(_uvcVersion, _format!);
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"Renegotiation failed: {ex.Message}");
                Status = CameraStatus.Error;
                return;
            }
        }
    }

    /// <summary>Builds complete frames from UVC payloads (each starts with a UVC payload header).</summary>
    private sealed class FrameAssembler(AndroidUvcCamera owner, StreamFormat format)
    {
        private readonly MemoryStream _frame = new(format.IsMjpeg ? 512 * 1024 : format.Width * format.Height * 2);
        private int _lastFid = -1;
        private bool _damaged;

        /// <summary>A packet of the current frame was lost: still fine for the live view, but not for a photo.</summary>
        public void MarkDamaged() => _damaged = true;

        private void Publish()
        {
            owner.PublishFrame(_frame, format, complete: !_damaged);
            _frame.SetLength(0);
            _damaged = false;
        }

        public void AddPayload(byte[] buffer, int length)
        {
            if (length < 2) return;

            int headerLength = buffer[0];
            if (headerLength < 2 || headerLength > length) return;
            var info = buffer[1];
            var fid = info & 0x01;
            var endOfFrame = (info & 0x02) != 0;
            var error = (info & 0x40) != 0;

            // A toggled frame ID starts a new frame (some cameras never set the EOF bit)
            if (_lastFid >= 0 && fid != _lastFid && _frame.Length > 0)
                Publish();
            _lastFid = fid;

            if (error)
            {
                _frame.SetLength(0);
                _damaged = false;
                return;
            }

            _frame.Write(buffer, headerLength, length - headerLength);

            if (endOfFrame && _frame.Length > 0)
                Publish();
        }
    }

    /// <param name="complete">False if packets of the frame were lost: shown in the live view, never used as photo.</param>
    private void PublishFrame(MemoryStream frame, StreamFormat format, bool complete = true)
    {
        byte[]? jpeg;
        try
        {
            jpeg = format.IsMjpeg
                ? MjpegToJpeg(frame.GetBuffer().AsSpan(0, (int)frame.Length))
                : Yuy2ToJpeg(frame.ToArray(), format.Width, format.Height);
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Dropped frame: {ex.Message}");
            return;
        }
        if (jpeg is null) return;
        Interlocked.Exchange(ref _lastFrameTicks, Environment.TickCount64);

        TaskCompletionSource<byte[]>? waiter = null;
        if (complete)
        {
            lock (_frameLock)
            {
                _latestJpeg = jpeg;
                waiter = _nextFrameTcs;
                _nextFrameTcs = null;
            }
        }
        waiter?.TrySetResult(jpeg);

        FrameReady?.Invoke(this, new LiveViewFrame(jpeg, FrameFormat.Jpeg, format.Width, format.Height, 0));
    }

    private static byte[]? MjpegToJpeg(ReadOnlySpan<byte> data)
    {
        // Must be a complete JPEG (SOI ... ); incomplete frames happen right after stream start
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return null;
        return MjpegHuffman.EnsureHuffmanTables(data);
    }

    private static byte[]? Yuy2ToJpeg(byte[] yuy2, int width, int height)
    {
        if (yuy2.Length < width * height * 2)
            return null;
        using var yuv = new YuvImage(yuy2, ImageFormatType.Yuy2, width, height, null);
        using var output = new MemoryStream();
        yuv.CompressToJpeg(new Rect(0, 0, width, height), 90, output);
        return output.ToArray();
    }

    public async Task StopLiveViewAsync()
    {
        if (_streamTask is null)
            return;

        _streamCts?.Cancel();
        try { await _streamTask.ConfigureAwait(false); } catch { }
        _streamCts?.Dispose();
        _streamCts = null;
        _streamTask = null;
        Interlocked.Exchange(ref _pendingIsoStream, null)?.Dispose();

        // UVC bulk streaming: CLEAR_FEATURE(ENDPOINT_HALT) tells the camera to stop sending video;
        // isochronous: switching back to alternate setting 0 releases the bandwidth and stops the stream
        if (_connection is not null && _streamEndpoint is not null)
        {
            _connection.ControlTransfer((UsbAddressing)0x02, 0x01, 0, (int)_streamEndpoint.Address, null, 0, 1000);
            _streamStopped = true;
        }
        else if (_connection is not null && _streamInterface is not null && UsesIso)
        {
            _connection.SetInterface(_streamInterface);
            _streamStopped = true;
        }
    }

    /// <summary>A UVC camera has no shutter: the photo is the next full-resolution stream frame.</summary>
    public async Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        if (Status != CameraStatus.Connected)
            throw new CameraException($"{DisplayName} ist nicht verbunden.");

        if (_streamTask is null)
            await StartLiveViewAsync(cancellationToken).ConfigureAwait(false);

        Task<byte[]> next;
        lock (_frameLock)
        {
            _nextFrameTcs ??= new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            next = _nextFrameTcs.Task;
        }

        byte[] jpeg;
        try
        {
            jpeg = await next.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            jpeg = _latestJpeg ?? throw new CameraException($"{DisplayName} liefert keine Bilder.");
        }

        return new CapturedPhoto(jpeg, DateTimeOffset.Now);
    }

    #endregion

    private void CloseConnection()
    {
        if (_connection is null)
            return;

        try
        {
            // Stop streaming: a zero-bandwidth alternate setting 0 / releasing the interface ends the stream
            if (_streamInterface is not null)
                _connection.ReleaseInterface(_streamInterface);
            if (_controlInterface is not null)
                _connection.ReleaseInterface(_controlInterface);
            _connection.Close();
        }
        catch { }

        _connection = null;
        _streamInterface = null;
        _controlInterface = null;
        _streamEndpoint = null;
        _isoSettings.Clear();
        _stalledAltSettings.Clear();
        _isoActive = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopLiveViewAsync().ConfigureAwait(false);
        CloseConnection();
        Status = CameraStatus.Disconnected;
    }
}
