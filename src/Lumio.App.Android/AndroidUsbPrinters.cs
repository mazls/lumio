using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Lumio.Printing;
using Log = Android.Util.Log;

namespace Lumio.App.Android;

/// <summary>
/// Printers on the USB port that speak "IPP over USB" (USB printer class, protocol 4) - e.g. the Canon Selphy
/// CP1300 / CP1500. The same IPP requests as over Wi-Fi are sent through the cable, so a whole booth can run
/// from one USB hub: camera, printer and power.
/// </summary>
internal static class AndroidUsbPrinters
{
    private const string LogTag = "LumioUsbPrint";
    public const string NameSuffix = " (USB)";

    private const int UsbClassPrinter = 7;
    private const int SubclassPrinter = 1;
    private const int ProtocolIppOverUsb = 4;

    // IPP over USB uses a fixed local address; the printer only looks at the path
    private static readonly Uri PrinterUri = new("http://localhost/ipp/print");

    private static readonly SemaphoreSlim PrintLock = new(1, 1);

    public static IReadOnlyList<PrinterDeviceInfo> GetPrinters(Context context) =>
        FindDevices(context)
            .Select(d => new PrinterDeviceInfo(DisplayName(d), SelphyPrintService.IsSelphyName(DisplayName(d)), false))
            .ToList();

    public static bool IsUsbPrinterName(string? printerName) =>
        printerName?.EndsWith(NameSuffix, StringComparison.Ordinal) == true;

    public static async Task PrintAsync(Activity activity, byte[] jpeg, string printerName, int copies, bool borderless, CancellationToken cancellationToken)
    {
        var usbManager = (UsbManager?)activity.GetSystemService(Context.UsbService)
                         ?? throw new InvalidOperationException("Android USB-Dienst nicht verfügbar.");
        var device = FindDevices(activity).FirstOrDefault(d => DisplayName(d) == printerName)
                     ?? FindDevices(activity).FirstOrDefault()
                     ?? throw new InvalidOperationException("Kein Drucker am USB-Anschluss gefunden. Ist der Selphy eingeschaltet und per USB verbunden?");

        if (!await UsbPermission.RequestAsync(activity, usbManager, device, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"USB-Zugriff auf {DisplayName(device)} wurde verweigert.");

        await PrintLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transport = UsbHttpHandler.Open(usbManager, device);
            Log.Info(LogTag, $"Printing to {DisplayName(device)} over USB, borderless={borderless}, {jpeg.Length} bytes");
            await IppPrintClient.PrintJpegAsync(PrinterUri, jpeg, copies, borderless, cancellationToken, transport).ConfigureAwait(false);
            Log.Info(LogTag, "Print job accepted by the printer");
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"USB print failed: {ex.Message}");
            throw;
        }
        finally
        {
            PrintLock.Release();
        }
    }

    private static List<UsbDevice> FindDevices(Context context)
    {
        try
        {
            var usbManager = (UsbManager?)context.GetSystemService(Context.UsbService);
            return usbManager?.DeviceList?.Values.Where(d => FindIppInterface(d) is not null).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"USB printer scan failed: {ex.Message}");
            return [];
        }
    }

    private static string DisplayName(UsbDevice device)
    {
        var product = device.ProductName?.Trim();
        var manufacturer = device.ManufacturerName?.Trim();
        var name = string.IsNullOrEmpty(product) ? "USB-Drucker"
            : string.IsNullOrEmpty(manufacturer) || product.Contains(manufacturer, StringComparison.OrdinalIgnoreCase) ? product
            : $"{manufacturer} {product}";
        return name + NameSuffix;
    }

    internal static UsbInterface? FindIppInterface(UsbDevice device)
    {
        for (var i = 0; i < device.InterfaceCount; i++)
        {
            var iface = device.GetInterface(i);
            if ((int)iface.InterfaceClass == UsbClassPrinter && (int)iface.InterfaceSubclass == SubclassPrinter && iface.InterfaceProtocol == ProtocolIppOverUsb)
                return iface;
        }
        return null;
    }

    /// <summary>
    /// Plain HTTP/1.1 over the two bulk endpoints of an IPP-over-USB interface: one request at a time,
    /// response with Content-Length or chunked transfer encoding.
    /// </summary>
    private sealed class UsbHttpHandler : HttpMessageHandler
    {
        private const int ChunkSize = 16 * 1024;
        private const int ReadTimeoutMs = 2000;

        private readonly UsbDeviceConnection _connection;
        private readonly UsbInterface _interface;
        private readonly UsbEndpoint _out;
        private readonly UsbEndpoint _in;

        private UsbHttpHandler(UsbDeviceConnection connection, UsbInterface iface, UsbEndpoint outEndpoint, UsbEndpoint inEndpoint)
        {
            _connection = connection;
            _interface = iface;
            _out = outEndpoint;
            _in = inEndpoint;
        }

        public static UsbHttpHandler Open(UsbManager usbManager, UsbDevice device)
        {
            var iface = FindIppInterface(device) ?? throw new InvalidOperationException("Der Drucker unterstützt kein Drucken per USB (IPP over USB).");
            UsbEndpoint? outEndpoint = null, inEndpoint = null;
            for (var i = 0; i < iface.EndpointCount; i++)
            {
                var ep = iface.GetEndpoint(i);
                if (ep?.Type != UsbAddressing.XferBulk) continue;
                if (ep.Direction == UsbAddressing.Out) outEndpoint ??= ep;
                else inEndpoint ??= ep;
            }
            if (outEndpoint is null || inEndpoint is null)
                throw new InvalidOperationException("USB-Druckerschnittstelle ohne Datenkanäle.");

            var connection = usbManager.OpenDevice(device) ?? throw new InvalidOperationException("USB-Verbindung zum Drucker konnte nicht geöffnet werden.");
            if (!connection.ClaimInterface(iface, true))
            {
                connection.Close();
                throw new InvalidOperationException("Der Drucker ist belegt (USB-Schnittstelle konnte nicht reserviert werden).");
            }
            connection.SetInterface(iface); // the IPP-over-USB function is an alternate setting of the printer interface
            return new UsbHttpHandler(connection, iface, outEndpoint, inEndpoint);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.Run(async () =>
            {
                var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var header = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"{request.Method.Method} {request.RequestUri!.PathAndQuery} HTTP/1.1\r\n")
                    .Append("Host: localhost\r\n")
                    .Append(CultureInfo.InvariantCulture, $"Content-Type: {request.Content?.Headers.ContentType?.ToString() ?? "application/ipp"}\r\n")
                    .Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n")
                    .Append("\r\n")
                    .ToString();

                Write(Encoding.ASCII.GetBytes(header), cancellationToken);
                Write(body, cancellationToken);
                return ReadResponse(request, cancellationToken);
            }, cancellationToken);

        private void Write(byte[] data, CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < data.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(ChunkSize, data.Length - offset);
                var written = _connection.BulkTransfer(_out, data, offset, count, 10_000);
                if (written <= 0)
                    throw new IOException("Daten konnten nicht an den Drucker gesendet werden (USB).");
                offset += written;
            }
        }

        private HttpResponseMessage ReadResponse(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var reader = new UsbReader(this, cancellationToken);

            // Status line and headers
            var headerBytes = reader.ReadUntil("\r\n\r\n"u8.ToArray());
            var lines = Encoding.ASCII.GetString(headerBytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var statusParts = lines[0].Split(' ', 3);
            var response = new HttpResponseMessage((HttpStatusCode)int.Parse(statusParts[1], CultureInfo.InvariantCulture)) { RequestMessage = request };

            var headers = lines.Skip(1)
                .Select(l => l.Split(':', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

            byte[] content;
            if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using var buffer = new MemoryStream();
                while (true)
                {
                    var sizeLine = Encoding.ASCII.GetString(reader.ReadUntil("\r\n"u8.ToArray())).Trim();
                    var size = int.Parse(sizeLine.Split(';')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (size == 0)
                    {
                        reader.ReadUntil("\r\n"u8.ToArray()); // end of trailers
                        break;
                    }
                    buffer.Write(reader.ReadExactly(size));
                    reader.ReadExactly(2); // CRLF after each chunk
                }
                content = buffer.ToArray();
            }
            else if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length))
            {
                content = reader.ReadExactly(length);
            }
            else
            {
                content = [];
            }

            response.Content = new ByteArrayContent(content);
            if (headers.TryGetValue("Content-Type", out var type))
                response.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _connection.ReleaseInterface(_interface); } catch { }
                _connection.Close();
            }
            base.Dispose(disposing);
        }

        /// <summary>Buffered reading from the bulk IN endpoint (a response can arrive in several transfers).</summary>
        private sealed class UsbReader(UsbHttpHandler owner, CancellationToken cancellationToken)
        {
            private readonly byte[] _transfer = new byte[ChunkSize];
            private readonly List<byte> _pending = [];
            private readonly DateTime _deadline = DateTime.UtcNow.AddSeconds(120);

            public byte[] ReadUntil(byte[] delimiter)
            {
                while (true)
                {
                    var index = IndexOf(delimiter);
                    if (index >= 0)
                        return Take(index + delimiter.Length);
                    Fill();
                }
            }

            public byte[] ReadExactly(int count)
            {
                while (_pending.Count < count)
                    Fill();
                return Take(count);
            }

            private void Fill()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > _deadline)
                    throw new TimeoutException("Der Drucker hat nicht geantwortet (USB).");
                var read = owner._connection.BulkTransfer(owner._in, _transfer, _transfer.Length, ReadTimeoutMs);
                if (read > 0)
                    _pending.AddRange(_transfer.AsSpan(0, read).ToArray());
            }

            private int IndexOf(byte[] delimiter)
            {
                for (var i = 0; i <= _pending.Count - delimiter.Length; i++)
                {
                    var match = true;
                    for (var j = 0; j < delimiter.Length && match; j++)
                        match = _pending[i + j] == delimiter[j];
                    if (match)
                        return i;
                }
                return -1;
            }

            private byte[] Take(int count)
            {
                var result = _pending.GetRange(0, count).ToArray();
                _pending.RemoveRange(0, count);
                return result;
            }
        }
    }
}
