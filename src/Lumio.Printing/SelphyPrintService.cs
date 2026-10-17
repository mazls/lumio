using System.Diagnostics;
using System.IO;

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
#endif

namespace Lumio.Printing;

public sealed record PrinterDeviceInfo(string Name, bool IsSelphy, bool IsDefault);

public sealed class SelphyPrintService
{
    private static readonly string[] SelphyKeywords = ["SELPHY", "CP1300", "CP1500", "CP1200", "CP1000", "CP910", "CP800", "CANON CP"];

    private static IReadOnlyList<PrinterDeviceInfo>? _cachedPrinters;
    private static DateTime _lastPrinterCacheTime = DateTime.MinValue;
    private static readonly TimeSpan PrinterCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly object _printerCacheLock = new();

    public static Func<byte[], string?, int, CancellationToken, Task>? CustomPrintHandler { get; set; }
    public static Func<IReadOnlyList<PrinterDeviceInfo>>? CustomPrinterDiscovery { get; set; }

    public static IReadOnlyList<PrinterDeviceInfo> GetInstalledPrinters(bool forceRefresh = false)
    {
        if (CustomPrinterDiscovery?.Invoke() is { } customList)
            return customList;

        if (!forceRefresh)
        {
            lock (_printerCacheLock)
            {
                if (_cachedPrinters != null && DateTime.UtcNow - _lastPrinterCacheTime < PrinterCacheTtl)
                {
                    return _cachedPrinters;
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
            var list = new List<PrinterDeviceInfo>();
            try
            {
                var defaultPrinter = new PrinterSettings().PrinterName;

                foreach (string printer in PrinterSettings.InstalledPrinters)
                {
                    var isSelphy = SelphyKeywords.Any(k => printer.Contains(k, StringComparison.OrdinalIgnoreCase));
                    var isDefault = string.Equals(printer, defaultPrinter, StringComparison.OrdinalIgnoreCase);
                    list.Add(new PrinterDeviceInfo(printer, isSelphy, isDefault));
                }

                var sorted = list
                    .OrderByDescending(p => p.IsSelphy)
                    .ThenByDescending(p => p.IsDefault)
                    .ThenBy(p => p.Name)
                    .ToList();

                lock (_printerCacheLock)
                {
                    _cachedPrinters = sorted;
                    _lastPrinterCacheTime = DateTime.UtcNow;
                }

                return sorted;
            }
            catch
            {
                return list;
            }
#else
            return [];
#endif
        }

        // Linux / Raspberry Pi CUPS print discovery
        var linuxPrinters = GetLinuxInstalledPrinters();
        lock (_printerCacheLock)
        {
            _cachedPrinters = linuxPrinters;
            _lastPrinterCacheTime = DateTime.UtcNow;
        }
        return linuxPrinters;
    }

    private static IReadOnlyList<PrinterDeviceInfo> GetLinuxInstalledPrinters()
    {
        var list = new List<PrinterDeviceInfo>();
        try
        {
            var defaultPrinter = GetLinuxDefaultPrinter();
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "lpstat",
                Arguments = "-p",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (proc is not null)
            {
                while (proc.StandardOutput.ReadLine() is { } line)
                {
                    // Format: printer Canon_SELPHY_CP1300 is idle. enabled since...
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && string.Equals(parts[0], "printer", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = parts[1];
                        var isSelphy = SelphyKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
                        var isDef = string.Equals(name, defaultPrinter, StringComparison.OrdinalIgnoreCase);
                        list.Add(new PrinterDeviceInfo(name, isSelphy, isDef));
                    }
                }
                proc.WaitForExit();
            }
        }
        catch
        {
            // lpstat not available or failed
        }

        return list
            .OrderByDescending(p => p.IsSelphy)
            .ThenByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name)
            .ToList();
    }

    private static string? GetLinuxDefaultPrinter()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "lpstat",
                Arguments = "-d",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (proc is not null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                // Output: system default destination: Canon_SELPHY_CP1300
                var colonIndex = output.IndexOf(':');
                if (colonIndex >= 0)
                {
                    return output[(colonIndex + 1)..].Trim();
                }
            }
        }
        catch { }

        return null;
    }

    public static string? AutoDetectSelphyPrinter()
    {
        var printers = GetInstalledPrinters();
        return printers.FirstOrDefault(p => p.IsSelphy)?.Name ?? printers.FirstOrDefault(p => p.IsDefault)?.Name;
    }

    public async Task PrintPhotoAsync(byte[] jpegData, string? printerName = null, int copies = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpegData);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(copies);

        var targetPrinter = !string.IsNullOrWhiteSpace(printerName) ? printerName : AutoDetectSelphyPrinter();
        if (string.IsNullOrWhiteSpace(targetPrinter))
        {
            throw new InvalidOperationException("Kein Drucker gefunden oder ausgewählt.");
        }

        if (CustomPrintHandler is not null)
        {
            await CustomPrintHandler(jpegData, targetPrinter, copies, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
            await PrintWindowsAsync(jpegData, targetPrinter, copies, cancellationToken).ConfigureAwait(false);
            return;
#endif
        }

        await PrintLinuxAsync(jpegData, targetPrinter, copies, cancellationToken).ConfigureAwait(false);
    }

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
    private static async Task PrintWindowsAsync(byte[] jpegData, string targetPrinter, int copies, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var imageStream = new MemoryStream(jpegData);
            using var image = Image.FromStream(imageStream);

            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = targetPrinter;
            doc.PrinterSettings.Copies = (short)Math.Clamp(copies, 1, 10);

            // Configure Postcard / 4x6" / 10x15cm paper size if available
            ConfigurePaperSize(doc);

            // Match orientation to image aspect
            doc.DefaultPageSettings.Landscape = image.Width > image.Height;
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

            doc.PrintPage += (s, ev) =>
            {
                if (ev.Graphics is null)
                    return;

                ev.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                ev.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                ev.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                var pageBounds = ev.PageBounds;
                
                // Calculate fill or fit inside page
                var imgAspect = (float)image.Width / image.Height;
                var pageAspect = (float)pageBounds.Width / pageBounds.Height;

                Rectangle destRect;
                if (Math.Abs(imgAspect - pageAspect) < 0.05f)
                {
                    destRect = pageBounds;
                }
                else if (imgAspect > pageAspect)
                {
                    // Image wider: match width
                    var destHeight = (int)(pageBounds.Width / imgAspect);
                    var top = pageBounds.Top + (pageBounds.Height - destHeight) / 2;
                    destRect = new Rectangle(pageBounds.Left, top, pageBounds.Width, destHeight);
                }
                else
                {
                    // Image taller: match height
                    var destWidth = (int)(pageBounds.Height * imgAspect);
                    var left = pageBounds.Left + (pageBounds.Width - destWidth) / 2;
                    destRect = new Rectangle(left, pageBounds.Top, destWidth, pageBounds.Height);
                }

                ev.Graphics.DrawImage(image, destRect);
                ev.HasMorePages = false;
            };

            doc.Print();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void ConfigurePaperSize(PrintDocument doc)
    {
        try
        {
            foreach (PaperSize size in doc.PrinterSettings.PaperSizes)
            {
                var name = size.PaperName.ToLowerInvariant();
                if (name.Contains("postcard") || name.Contains("postkarte") || name.Contains("4x6") || name.Contains("4 x 6") || name.Contains("100 x 148") || name.Contains("10x15"))
                {
                    doc.DefaultPageSettings.PaperSize = size;
                    break;
                }
            }
        }
        catch
        {
            // Fallback to default paper size
        }
    }
#endif

    private static async Task PrintLinuxAsync(byte[] jpegData, string targetPrinter, int copies, CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"lumio_print_{Guid.NewGuid():N}.jpg");
        try
        {
            await File.WriteAllBytesAsync(tempFile, jpegData, cancellationToken).ConfigureAwait(false);
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "lp",
                Arguments = $"-d \"{targetPrinter}\" -n {Math.Clamp(copies, 1, 10)} -o media=Custom.100x148mm -o fit-to-page \"{tempFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });

            if (proc is not null)
            {
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (proc.ExitCode != 0)
                {
                    var err = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException($"CUPS Druckfehler (Code {proc.ExitCode}): {err}");
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch { }
        }
    }
}
