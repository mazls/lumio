using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lumio.Printing;

/// <summary>
/// Finds IPP printers in the local network (AirPrint / IPP Everywhere, e.g. Canon Selphy over WLAN) with a single
/// mDNS query. Queries from a non-5353 port get "legacy unicast" answers (RFC 6762 §6.7), so this works next to the
/// operating system's own mDNS responder without special socket options.
/// </summary>
public static class NetworkPrinterDiscovery
{
    public const string NameSuffix = " (WLAN)";

    private const string ServiceType = "_ipp._tcp.local";
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly ConcurrentDictionary<string, Uri> Printers = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastScan = DateTime.MinValue;
    private static int _scanning;

    /// <summary>Printers found so far (display name → IPP URI). Starts a background scan when the list is stale.</summary>
    public static IReadOnlyDictionary<string, Uri> GetPrinters()
    {
        if (DateTime.UtcNow - _lastScan > TimeSpan.FromSeconds(30))
            _ = ScanAsync();
        return Printers;
    }

    public static bool TryGetUri(string printerName, out Uri uri) => Printers.TryGetValue(printerName, out uri!);

    public static async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
        {
            // A scan is already running: wait for its result instead of starting a second one
            for (var i = 0; i < 30 && Volatile.Read(ref _scanning) == 1; i++)
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            _lastScan = DateTime.UtcNow;
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var query = BuildQuery();
            foreach (var local in LocalIPv4Addresses())
            {
                // Send once per network interface so a WLAN printer is found even if another adapter is the default route
                try
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    await udp.SendAsync(query, MulticastEndpoint, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // interface without multicast
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    ParseResponse(result.Buffer, result.RemoteEndPoint.Address);
                }
                catch
                {
                    // malformed packet
                }
            }
        }
        catch
        {
            // No network: nothing to discover
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .ToList();
        return addresses.Count > 0 ? addresses : [IPAddress.Any];
    }

    private static byte[] BuildQuery()
    {
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], 1); // one question
        ms.Write(header);
        WriteName(ms, ServiceType);
        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question[0..2], 12); // PTR
        BinaryPrimitives.WriteUInt16BigEndian(question[2..4], 0x8001); // IN, unicast response requested
        ms.Write(question);
        return ms.ToArray();
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private sealed class Service
    {
        public string? Target;
        public int Port = 631;
        public string Path = "ipp/print";
        public string? Model;
    }

    /// <summary>Collects PTR / SRV / TXT / A records of one response and registers the printer(s) in it.</summary>
    private static void ParseResponse(byte[] packet, IPAddress sender)
    {
        if (packet.Length < 12)
            return;

        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        int records = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2))
                      + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2))
                      + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10, 2));

        var pos = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(packet, ref pos);
            pos += 4;
        }

        var instances = new List<string>();
        var services = new Dictionary<string, Service>(StringComparer.OrdinalIgnoreCase);
        var hosts = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        Service For(string name) => services.TryGetValue(name, out var s) ? s : services[name] = new Service();

        for (var i = 0; i < records && pos + 10 <= packet.Length; i++)
        {
            var name = ReadName(packet, ref pos);
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(pos, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(pos + 8, 2));
            var data = pos + 10;
            pos = data + length;
            if (pos > packet.Length)
                break;

            switch (type)
            {
                case 12: // PTR -> service instance
                    var p = data;
                    var instance = ReadName(packet, ref p);
                    if (name.Equals(ServiceType, StringComparison.OrdinalIgnoreCase))
                        instances.Add(instance);
                    break;
                case 33: // SRV -> port + host
                    var service = For(name);
                    service.Port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(data + 4, 2));
                    var t = data + 6;
                    service.Target = ReadName(packet, ref t);
                    break;
                case 16: // TXT -> rp (resource path), ty (model)
                    var txt = For(name);
                    for (var q = data; q < data + length;)
                    {
                        var entry = Encoding.UTF8.GetString(packet, q + 1, packet[q]);
                        q += 1 + packet[q];
                        if (entry.StartsWith("rp=", StringComparison.OrdinalIgnoreCase)) txt.Path = entry[3..];
                        else if (entry.StartsWith("ty=", StringComparison.OrdinalIgnoreCase)) txt.Model = entry[3..];
                    }
                    break;
                case 1 when length == 4: // A
                    hosts[name] = new IPAddress(packet.AsSpan(data, 4));
                    break;
            }
        }

        foreach (var instance in instances.Count > 0 ? instances : services.Keys.ToList())
        {
            if (!services.TryGetValue(instance, out var service))
                continue;

            var address = service.Target is not null && hosts.TryGetValue(service.Target, out var ip) ? ip : sender;
            var model = (service.Model ?? instance.Split('.')[0]).Replace(" HTTP", "", StringComparison.OrdinalIgnoreCase).Trim();
            Printers[model + NameSuffix] = new Uri($"http://{address}:{service.Port}/{service.Path.TrimStart('/')}");
        }
    }

    private static string ReadName(byte[] packet, ref int pos)
    {
        var labels = new List<string>();
        var jumped = false;
        var cursor = pos;
        for (var guard = 0; guard < 64 && cursor < packet.Length; guard++)
        {
            var length = packet[cursor];
            if (length == 0)
            {
                cursor++;
                break;
            }
            if ((length & 0xC0) == 0xC0) // compression pointer
            {
                var target = ((length & 0x3F) << 8) | packet[cursor + 1];
                if (!jumped) pos = cursor + 2;
                jumped = true;
                cursor = target;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(packet, cursor + 1, length));
            cursor += 1 + length;
        }
        if (!jumped) pos = cursor;
        return string.Join('.', labels);
    }
}
