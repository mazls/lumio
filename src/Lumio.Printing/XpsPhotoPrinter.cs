#if WINDOWS || NET10_0_WINDOWS10_0_19041_0
using System.IO;
using System.Printing;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;

namespace Lumio.Printing;

/// <summary>
/// Prints a photo through Windows' XPS print path with a print ticket. Unlike classic GDI printing this honors
/// "borderless" on modern (v4 / IPP class) drivers such as the Selphy's: the page is rendered at full card size,
/// so the printer can bleed over the edges like when printing directly from the Selphy.
/// </summary>
internal static class XpsPhotoPrinter
{
    // 100 x 148 mm in device independent units (1/96 inch)
    private const double PostcardShort = 100 / 25.4 * 96;
    private const double PostcardLong = 148 / 25.4 * 96;
    private const double SizeTolerance = 12; // ~3 mm

    /// <returns>false if this printer/driver can't be used via XPS (caller falls back to GDI printing).</returns>
    public static Task<bool> TryPrintAsync(byte[] jpegData, string printerName, int copies, bool borderless, CancellationToken cancellationToken)
    {
        // WPF visuals need an STA thread
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(Print(jpegData, printerName, copies, borderless));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"XPS printing failed, falling back to GDI: {ex}");
                tcs.TrySetResult(false);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task.WaitAsync(cancellationToken);
    }

    private static bool Print(byte[] jpegData, string printerName, int copies, bool borderless)
    {
        using var server = new LocalPrintServer();
        using var queue = server.GetPrintQueue(printerName);
        var capabilities = queue.GetPrintCapabilities();

        var postcard = capabilities.PageMediaSizeCapability.FirstOrDefault(IsPostcard);
        if (postcard is null)
            return false; // no 100x148 mm paper: not a Selphy-like printer, let GDI handle it

        var ticket = queue.UserPrintTicket?.Clone() ?? new PrintTicket();
        ticket.PageMediaSize = postcard;
        ticket.PageOrientation = PageOrientation.Portrait;
        ticket.CopyCount = Math.Clamp(copies, 1, 10);
        if (capabilities.PageBorderlessCapability.Contains(PageBorderless.Borderless))
        {
            ticket.PageBorderless = borderless ? PageBorderless.Borderless : PageBorderless.None;
        }

        var validated = queue.MergeAndValidatePrintTicket(queue.UserPrintTicket ?? new PrintTicket(), ticket).ValidatedPrintTicket;
        var pageWidth = Math.Min(postcard.Width ?? PostcardShort, postcard.Height ?? PostcardLong);
        var pageHeight = Math.Max(postcard.Width ?? PostcardShort, postcard.Height ?? PostcardLong);
        var printable = queue.GetPrintCapabilities(validated).PageImageableArea;
        var isBorderless = borderless && validated.PageBorderless == PageBorderless.Borderless;

        var page = BuildPage(jpegData, pageWidth, pageHeight, printable, isBorderless);
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);
        document.Pages.Add(new PageContent { Child = page });

        XpsDocumentWriter writer = PrintQueue.CreateXpsDocumentWriter(queue);
        writer.Write(document, validated);
        return true;
    }

    private static FixedPage BuildPage(byte[] jpegData, double pageWidth, double pageHeight, PageImageableArea? printable, bool borderless)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(jpegData);
        bitmap.EndInit();
        bitmap.Freeze();

        // The card is portrait: turn landscape photos instead of the paper
        BitmapSource source = bitmap;
        if (bitmap.PixelWidth > bitmap.PixelHeight)
        {
            source = new TransformedBitmap(bitmap, new RotateTransform(90));
            source.Freeze();
        }

        var page = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
        var image = new Image { Source = source };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        if (borderless || printable is null)
        {
            // Fill the whole card (minimal crop to the card's aspect ratio)
            image.Width = pageWidth;
            image.Height = pageHeight;
            image.Stretch = Stretch.UniformToFill;
            page.ClipToBounds = true;
        }
        else
        {
            // Complete photo inside the printable area
            image.Width = printable.ExtentWidth;
            image.Height = printable.ExtentHeight;
            image.Stretch = Stretch.Uniform;
            FixedPage.SetLeft(image, printable.OriginWidth);
            FixedPage.SetTop(image, printable.OriginHeight);
        }

        page.Children.Add(image);
        var size = new Size(pageWidth, pageHeight);
        page.Measure(size);
        page.Arrange(new Rect(size));
        page.UpdateLayout();
        return page;
    }

    private static bool IsPostcard(PageMediaSize size)
    {
        if (size.Width is not { } width || size.Height is not { } height)
            return false;
        var shortSide = Math.Min(width, height);
        var longSide = Math.Max(width, height);
        return Math.Abs(shortSide - PostcardShort) <= SizeTolerance && Math.Abs(longSide - PostcardLong) <= SizeTolerance;
    }
}
#endif
