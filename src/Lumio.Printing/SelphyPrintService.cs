using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;

namespace Lumio.Printing;

public sealed record PrinterDeviceInfo(string Name, bool IsSelphy, bool IsDefault);

public sealed class SelphyPrintService
{
    private static readonly string[] SelphyKeywords = ["SELPHY", "CP1300", "CP1500", "CP1200", "CP1000", "CP910", "CP800", "CANON CP"];

    public static IReadOnlyList<PrinterDeviceInfo> GetInstalledPrinters()
    {
        var list = new List<PrinterDeviceInfo>();
        var defaultPrinter = new PrinterSettings().PrinterName;

        foreach (string printer in PrinterSettings.InstalledPrinters)
        {
            var isSelphy = SelphyKeywords.Any(k => printer.Contains(k, StringComparison.OrdinalIgnoreCase));
            var isDefault = string.Equals(printer, defaultPrinter, StringComparison.OrdinalIgnoreCase);
            list.Add(new PrinterDeviceInfo(printer, isSelphy, isDefault));
        }

        // Put Selphy printers first, then default printer, then others
        return list
            .OrderByDescending(p => p.IsSelphy)
            .ThenByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name)
            .ToList();
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
                // Canon Selphy postcard paper is "Postcard", "Postcard 100 x 148 mm", "4 x 6 in", or similar
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
}
