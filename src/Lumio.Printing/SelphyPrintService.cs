using System.Diagnostics;
using System.IO;

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;
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

    // Selphy postcard / KP-108IN paper: 100 x 148 mm, in 1/100 inch as used by System.Drawing.Printing
    private const int PostcardShortSide = 394;
    private const int PostcardLongSide = 583;
    private const int PaperSizeTolerance = 15; // ~4 mm

    /// <summary>Platform print hook (e.g. Android): JPEG, printer name, copies, borderless.</summary>
    public static Func<byte[], string?, int, bool, CancellationToken, Task>? CustomPrintHandler { get; set; }
    public static Func<IReadOnlyList<PrinterDeviceInfo>>? CustomPrinterDiscovery { get; set; }

    /// <summary>
    /// Spooler / CUPS printers plus IPP printers found in the network (listed with a " (WLAN)" suffix and printed
    /// directly via IPP, without a printer driver).
    /// </summary>
    public static IReadOnlyList<PrinterDeviceInfo> GetInstalledPrinters(bool forceRefresh = false)
    {
        if (CustomPrinterDiscovery?.Invoke() is { } customList)
            return customList;

        var network = NetworkPrinterDiscovery.GetPrinters().Keys
            .Select(name => new PrinterDeviceInfo(name, IsSelphyName(name), false));
        return GetSystemPrinters(forceRefresh)
            .Concat(network)
            .OrderByDescending(p => p.IsSelphy)
            .ThenByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<PrinterDeviceInfo> GetSystemPrinters(bool forceRefresh)
    {
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

    public static bool IsSelphyName(string printerName) =>
        SelphyKeywords.Any(k => printerName.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static string? AutoDetectSelphyPrinter()
    {
        var printers = GetInstalledPrinters();
        return printers.FirstOrDefault(p => p.IsSelphy)?.Name ?? printers.FirstOrDefault(p => p.IsDefault)?.Name;
    }

    /// <param name="borderless">
    /// Fill the whole 100x148 mm card edge to edge (the picture is cropped minimally to the card's aspect ratio);
    /// otherwise the complete picture is fitted into the printable area.
    /// </param>
    public async Task PrintPhotoAsync(byte[] jpegData, string? printerName = null, int copies = 1, CancellationToken cancellationToken = default, bool borderless = true)
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
            await CustomPrintHandler(jpegData, targetPrinter, copies, borderless, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (targetPrinter.EndsWith(NetworkPrinterDiscovery.NameSuffix, StringComparison.Ordinal)
            && !NetworkPrinterDiscovery.TryGetUri(targetPrinter, out _))
        {
            // Saved WLAN printer not seen since the app started: look for it once more
            await NetworkPrinterDiscovery.ScanAsync(cancellationToken).ConfigureAwait(false);
            if (!NetworkPrinterDiscovery.TryGetUri(targetPrinter, out _))
                throw new InvalidOperationException($"'{targetPrinter}' wurde im WLAN nicht gefunden. Ist der Drucker eingeschaltet und im selben Netzwerk?");
        }

        if (NetworkPrinterDiscovery.TryGetUri(targetPrinter, out var ippUri))
        {
            await IppPrintClient.PrintJpegAsync(ippUri, jpegData, copies, borderless, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
            await PrintWindowsAsync(jpegData, targetPrinter, copies, borderless, cancellationToken).ConfigureAwait(false);
            return;
#endif
        }

        await PrintLinuxAsync(jpegData, targetPrinter, copies, borderless, cancellationToken).ConfigureAwait(false);
    }

#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
    private static async Task PrintWindowsAsync(byte[] jpegData, string targetPrinter, int copies, bool borderless, CancellationToken cancellationToken)
    {
        // Preferred: XPS print path with a print ticket - the only way real borderless reaches modern drivers
        if (await XpsPhotoPrinter.TryPrintAsync(jpegData, targetPrinter, copies, borderless, cancellationToken).ConfigureAwait(false))
            return;

        await Task.Run(() =>
        {
            using var imageStream = new MemoryStream(jpegData);
            using var image = Image.FromStream(imageStream);

            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = targetPrinter;
            if (borderless)
            {
                ApplyBorderlessPrintTicket(doc, targetPrinter);
            }
            doc.PrinterSettings.Copies = (short)Math.Clamp(copies, 1, 10);
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            ConfigurePaperSize(doc, borderless);

            // Keep the paper in the driver's natural orientation: many drivers (Selphy included) ignore a switch to
            // landscape, which used to print only the top-left part of the picture. We rotate the picture instead.
            doc.DefaultPageSettings.Landscape = false;

            doc.PrintPage += (s, ev) =>
            {
                if (ev.Graphics is null)
                    return;

                ev.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                ev.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                ev.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // All values in 1/100 inch. The graphics origin sits at the printer's hard margin.
                var page = ev.PageBounds;
                var printable = ev.PageSettings.PrintableArea;
                var hardMarginX = ev.PageSettings.HardMarginX;
                var hardMarginY = ev.PageSettings.HardMarginY;
                if (ev.PageSettings.Landscape)
                {
                    printable = new RectangleF(printable.Y, printable.X, printable.Height, printable.Width);
                }

                var pageIsPortrait = page.Height > page.Width;
                var imageIsPortrait = image.Height > image.Width;
                using var oriented = (Image)image.Clone();
                if (pageIsPortrait != imageIsPortrait)
                {
                    oriented.RotateFlip(RotateFlipType.Rotate90FlipNone);
                }

                RectangleF target;
                RectangleF source;
                if (borderless)
                {
                    // Whole sheet (bleeds into the non-printable margin), cropped to the sheet's aspect ratio
                    target = new RectangleF(-hardMarginX, -hardMarginY, page.Width, page.Height);
                    source = CropToAspect(oriented.Width, oriented.Height, target.Width / target.Height);
                }
                else
                {
                    // Complete picture fitted into the printable area, centered
                    var area = new RectangleF(printable.X - hardMarginX, printable.Y - hardMarginY, printable.Width, printable.Height);
                    var scale = Math.Min(area.Width / oriented.Width, area.Height / oriented.Height);
                    var width = oriented.Width * scale;
                    var height = oriented.Height * scale;
                    target = new RectangleF(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
                    source = new RectangleF(0, 0, oriented.Width, oriented.Height);
                }

                ev.Graphics.DrawImage(oriented, target, source, GraphicsUnit.Pixel);
                ev.HasMorePages = false;
            };

            doc.Print();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drivers like Windows' IPP class driver (used for the Selphy) offer borderless printing only as a print ticket
    /// option, not as a paper size. Converts such a ticket into the driver's DEVMODE so System.Drawing printing uses it.
    /// </summary>
    private static void ApplyBorderlessPrintTicket(PrintDocument doc, string printerName)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(printerName);
            if (!queue.GetPrintCapabilities().PageBorderlessCapability.Contains(PageBorderless.Borderless))
                return;

            var ticket = queue.UserPrintTicket?.Clone() ?? new PrintTicket();
            ticket.PageBorderless = PageBorderless.Borderless;

            using var converter = new PrintTicketConverter(printerName, queue.ClientPrintSchemaVersion);
            var devMode = converter.ConvertPrintTicketToDevMode(ticket, BaseDevModeType.UserDefault);

            var handle = Marshal.AllocHGlobal(devMode.Length);
            try
            {
                Marshal.Copy(devMode, 0, handle, devMode.Length);
                doc.PrinterSettings.SetHdevmode(handle);
                doc.DefaultPageSettings.SetHdevmode(handle);
            }
            finally
            {
                Marshal.FreeHGlobal(handle);
            }
        }
        catch
        {
            // Driver without print ticket support: the paper size choice and bleed drawing still apply
        }
    }

    /// <summary>Largest centered source rectangle with the given aspect ratio (width / height).</summary>
    private static RectangleF CropToAspect(int width, int height, float aspect)
    {
        if ((float)width / height > aspect)
        {
            var cropWidth = height * aspect;
            return new RectangleF((width - cropWidth) / 2, 0, cropWidth, height);
        }

        var cropHeight = width / aspect;
        return new RectangleF(0, (height - cropHeight) / 2, width, cropHeight);
    }

    /// <summary>
    /// Picks the 100x148 mm postcard paper by its dimensions (driver paper names differ by language and model);
    /// prefers the borderless variant if requested and offered by the driver.
    /// </summary>
    private static void ConfigurePaperSize(PrintDocument doc, bool borderless)
    {
        try
        {
            var sizes = doc.PrinterSettings.PaperSizes.Cast<PaperSize>().ToList();

            static bool IsPostcard(PaperSize size)
            {
                var shortSide = Math.Min(size.Width, size.Height);
                var longSide = Math.Max(size.Width, size.Height);
                return Math.Abs(shortSide - PostcardShortSide) <= PaperSizeTolerance && Math.Abs(longSide - PostcardLongSide) <= PaperSizeTolerance;
            }

            static bool IsBorderlessName(PaperSize size)
            {
                var name = size.PaperName.ToLowerInvariant();
                return name.Contains("borderless") || name.Contains("randlos") || name.Contains("ohne rand") || name.Contains("full bleed");
            }

            static bool IsPostcardName(PaperSize size)
            {
                var name = size.PaperName.ToLowerInvariant();
                return name.Contains("postcard") || name.Contains("postkarte") || name.Contains("4x6") || name.Contains("4 x 6") || name.Contains("100 x 148") || name.Contains("100x148") || name.Contains("10x15");
            }

            var candidates = sizes.Where(IsPostcard).ToList();
            if (candidates.Count == 0)
                candidates = sizes.Where(IsPostcardName).ToList();
            if (candidates.Count == 0)
                return; // keep the driver's default paper

            doc.DefaultPageSettings.PaperSize =
                candidates.FirstOrDefault(size => IsBorderlessName(size) == borderless) ?? candidates[0];
        }
        catch
        {
            // Fallback to default paper size
        }
    }
#endif

    private static async Task PrintLinuxAsync(byte[] jpegData, string targetPrinter, int copies, bool borderless, CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"lumio_print_{Guid.NewGuid():N}.jpg");
        try
        {
            await File.WriteAllBytesAsync(tempFile, jpegData, cancellationToken).ConfigureAwait(false);
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "lp",
                // Borderless: fill the card and crop the overhang; otherwise fit the whole picture onto the card
                Arguments = $"-d \"{targetPrinter}\" -n {Math.Clamp(copies, 1, 10)} -o media=Custom.100x148mm -o print-scaling={(borderless ? "fill" : "fit")} \"{tempFile}\"",
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
