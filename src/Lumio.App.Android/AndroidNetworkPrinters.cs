using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Net.Nsd;
using Lumio.Printing;
using Log = Android.Util.Log;

namespace Lumio.App.Android;

/// <summary>
/// Finds IPP printers (AirPrint / IPP Everywhere, e.g. Canon Selphy over WLAN) via mDNS and prints JPEGs to them
/// directly - Android has no printer drivers, and this avoids the system print dialog in front of guests.
/// </summary>
internal static class AndroidNetworkPrinters
{
    private const string LogTag = "LumioPrint";
    private const string ServiceType = "_ipp._tcp";

    private static readonly ConcurrentDictionary<string, Uri> Printers = new();
    private static readonly ConcurrentQueue<NsdServiceInfo> ResolveQueue = new();
    private static NsdManager? _nsd;
    private static DiscoveryListener? _discovery;
    private static int _resolving;

    public static void Start(Context context)
    {
        if (_nsd is not null)
            return;

        try
        {
            _nsd = (NsdManager?)context.GetSystemService(Context.NsdService);
            _discovery = new DiscoveryListener();
            _nsd?.DiscoverServices(ServiceType, NsdProtocol.DnsSd, _discovery);
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Printer discovery unavailable: {ex.Message}");
        }
    }

    public static IReadOnlyList<PrinterDeviceInfo> GetPrinters() =>
        Printers.Keys
            .OrderByDescending(SelphyPrintService.IsSelphyName)
            .ThenBy(name => name)
            .Select(name => new PrinterDeviceInfo(name, SelphyPrintService.IsSelphyName(name), false))
            .ToList();

    public static Task PrintAsync(byte[] jpeg, string? printerName, int copies, bool borderless, CancellationToken cancellationToken)
    {
        if (printerName is null || !Printers.TryGetValue(printerName, out var uri))
        {
            throw new InvalidOperationException(
                "Kein Drucker im WLAN gefunden. Bitte den Selphy einschalten und mit demselben WLAN wie das Gerät verbinden.");
        }

        Log.Info(LogTag, $"Printing to {printerName} ({uri}), borderless={borderless}, {jpeg.Length} bytes");
        return IppPrintClient.PrintJpegAsync(uri, jpeg, copies, borderless, cancellationToken);
    }

    // Older Android versions resolve only one service at a time
    private static void ResolveNext()
    {
        if (_nsd is null || !ResolveQueue.TryDequeue(out var service))
            return;
        if (Interlocked.CompareExchange(ref _resolving, 1, 0) != 0)
        {
            ResolveQueue.Enqueue(service);
            return;
        }

#pragma warning disable CA1422 // ResolveService is replaced by service info callbacks on Android 14+, still works there
        _nsd.ResolveService(service, new ResolveListener());
#pragma warning restore CA1422
    }

    private static void OnResolveFinished()
    {
        Interlocked.Exchange(ref _resolving, 0);
        ResolveNext();
    }

    private sealed class DiscoveryListener : Java.Lang.Object, NsdManager.IDiscoveryListener
    {
        public void OnServiceFound(NsdServiceInfo? serviceInfo)
        {
            if (serviceInfo is null) return;
            ResolveQueue.Enqueue(serviceInfo);
            ResolveNext();
        }

        public void OnServiceLost(NsdServiceInfo? serviceInfo)
        {
            var lost = serviceInfo?.ServiceName;
            foreach (var name in Printers.Keys.Where(n => lost is not null && n.Contains(lost, StringComparison.OrdinalIgnoreCase)))
                Printers.TryRemove(name, out _);
        }

        public void OnDiscoveryStarted(string? serviceType) => Log.Info(LogTag, "Printer discovery started");
        public void OnDiscoveryStopped(string? serviceType) { }
        public void OnStartDiscoveryFailed(string? serviceType, NsdFailure errorCode) => Log.Warn(LogTag, $"Printer discovery failed: {errorCode}");
        public void OnStopDiscoveryFailed(string? serviceType, NsdFailure errorCode) { }
    }

    private sealed class ResolveListener : Java.Lang.Object, NsdManager.IResolveListener
    {
        public void OnServiceResolved(NsdServiceInfo? serviceInfo)
        {
            try
            {
#pragma warning disable CA1422
                var host = serviceInfo?.Host?.HostAddress;
#pragma warning restore CA1422
                if (serviceInfo is null || string.IsNullOrEmpty(host))
                    return;

                var attributes = serviceInfo.Attributes;
                var path = TxtValue(attributes, "rp") ?? "ipp/print";
                var model = (TxtValue(attributes, "ty") ?? serviceInfo.ServiceName ?? "Netzwerkdrucker").Replace(" HTTP", "", StringComparison.OrdinalIgnoreCase).Trim();
                var hostPart = host.Contains(':') ? $"[{host}]" : host; // IPv6
                var uri = new Uri($"http://{hostPart}:{serviceInfo.Port}/{path.TrimStart('/')}");

                Printers[$"{model} (WLAN)"] = uri;
                Log.Info(LogTag, $"Found printer {model} at {uri}");
            }
            finally
            {
                OnResolveFinished();
            }
        }

        public void OnResolveFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode)
        {
            Log.Warn(LogTag, $"Could not resolve printer {serviceInfo?.ServiceName}: {errorCode}");
            OnResolveFinished();
        }

        private static string? TxtValue(IDictionary<string, byte[]>? attributes, string key) =>
            attributes is not null && attributes.TryGetValue(key, out var value) && value is { Length: > 0 }
                ? Encoding.UTF8.GetString(value)
                : null;
    }
}
