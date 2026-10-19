using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using SkiaSharp;

namespace Lumio.Printing;

/// <summary>
/// Minimal IPP (Internet Printing Protocol) client: sends a JPEG straight to a network printer such as the
/// Canon Selphy (AirPrint / IPP Everywhere) without any printer driver - used where no print spooler exists (Android).
/// </summary>
public static class IppPrintClient
{
    private const ushort OperationPrintJob = 0x0002;
    private const ushort OperationValidateJob = 0x0004;

    // Delimiter and value tags (RFC 8010)
    private const byte OperationAttributesTag = 0x01;
    private const byte JobAttributesTag = 0x02;
    private const byte EndOfAttributesTag = 0x03;
    private const byte TagInteger = 0x21;
    private const byte TagBegCollection = 0x34;
    private const byte TagEndCollection = 0x37;
    private const byte TagKeyword = 0x44;
    private const byte TagUri = 0x45;
    private const byte TagCharset = 0x47;
    private const byte TagNaturalLanguage = 0x48;
    private const byte TagMimeMediaType = 0x49;
    private const byte TagMemberAttrName = 0x4A;
    private const byte TagNameWithoutLanguage = 0x42;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>
    /// Borderless IPP prints are enlarged by the printer slightly beyond the card (Canon Selphy: a few percent),
    /// so the outer edge of the photo would be lost. This share of the width/height is added around the picture
    /// (stretched edge pixels), which lands outside the card - the actual photo, frame and text fit completely.
    /// </summary>
    public static float BorderlessBleed { get; set; } = 0.025f;

    /// <summary>Prints a JPEG on a 100x148 mm postcard.</summary>
    /// <param name="printerUri">e.g. <c>http://192.168.2.32:631/ipp/print</c> (ipp:// is accepted too)</param>
    /// <param name="borderless">Edge to edge with the picture cropped to the card; otherwise fitted inside the printer's margins.</param>
    /// <param name="transport">
    /// Optional connection to the printer instead of the network, e.g. IPP over USB (the same HTTP requests,
    /// sent through the USB cable to <c>http://localhost/ipp/print</c>).
    /// </param>
    public static Task PrintJpegAsync(Uri printerUri, byte[] jpegData, int copies, bool borderless, CancellationToken cancellationToken = default,
        HttpMessageHandler? transport = null)
    {
        ArgumentNullException.ThrowIfNull(jpegData);
        return SendAsync(OperationPrintJob, printerUri, jpegData, copies, borderless, cancellationToken, transport);
    }

    /// <summary>Lets the printer check the job settings (format, 100x148 mm, borderless) without printing anything.</summary>
    public static Task ValidateJobAsync(Uri printerUri, bool borderless, CancellationToken cancellationToken = default) =>
        SendAsync(OperationValidateJob, printerUri, document: null, copies: 1, borderless, cancellationToken);

    private static async Task SendAsync(ushort operation, Uri printerUri, byte[]? document, int copies, bool borderless, CancellationToken cancellationToken,
        HttpMessageHandler? transport = null)
    {
        ArgumentNullException.ThrowIfNull(printerUri);

        var httpUri = new UriBuilder(printerUri) { Scheme = "http", Port = printerUri.IsDefaultPort ? 631 : printerUri.Port }.Uri;
        var ippUri = new UriBuilder(httpUri) { Scheme = "ipp" }.Uri.ToString();

        using var request = new MemoryStream();
        WriteHeader(request, operation, requestId: 1);

        request.WriteByte(OperationAttributesTag);
        WriteAttribute(request, TagCharset, "attributes-charset", "utf-8");
        WriteAttribute(request, TagNaturalLanguage, "attributes-natural-language", "de");
        WriteAttribute(request, TagUri, "printer-uri", ippUri);
        WriteAttribute(request, TagNameWithoutLanguage, "requesting-user-name", "Lumio");
        WriteAttribute(request, TagNameWithoutLanguage, "job-name", "Lumio Foto");
        WriteAttribute(request, TagMimeMediaType, "document-format", "image/jpeg");

        request.WriteByte(JobAttributesTag);
        if (copies > 1)
        {
            WriteAttribute(request, TagInteger, "copies", Math.Clamp(copies, 1, 10));
        }
        WriteAttribute(request, TagKeyword, "print-scaling", borderless ? "fill" : "fit");
        WriteMediaCol(request, borderless);

        request.WriteByte(EndOfAttributesTag);
        if (document is not null)
        {
            request.Write(borderless ? AddBleed(document, BorderlessBleed) : document);
        }

        using var content = new ByteArrayContent(request.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
        using var client = transport is null ? null : new HttpClient(transport, disposeHandler: false) { Timeout = Http.Timeout };
        using var response = await (client ?? Http).PostAsync(httpUri, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length < 8)
            throw new InvalidOperationException("Ungültige Antwort vom Drucker.");

        // 0x0000-0x00FF = successful-ok (with or without ignored/substituted attributes)
        var status = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2, 2));
        if (status >= 0x0400)
        {
            throw new InvalidOperationException($"Der Drucker hat den Auftrag abgelehnt (IPP-Status 0x{status:X4}{FindStatusMessage(body)}).");
        }
    }

    /// <summary>Extends the picture by <paramref name="bleed"/> on every side by stretching its edge pixels.</summary>
    internal static byte[] AddBleed(byte[] jpeg, float bleed)
    {
        if (bleed <= 0)
            return jpeg;
        try
        {
            using var image = SKImage.FromEncodedData(jpeg);
            if (image is null)
                return jpeg;

            var padX = (int)Math.Round(image.Width * bleed);
            var padY = (int)Math.Round(image.Height * bleed);
            using var surface = SKSurface.Create(new SKImageInfo(image.Width + 2 * padX, image.Height + 2 * padY));
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateImage(image, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.CreateTranslation(padX, padY)),
            };
            surface.Canvas.DrawPaint(paint);
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 95);
            return data.ToArray();
        }
        catch
        {
            return jpeg; // print as is rather than not at all
        }
    }

    /// <summary>media-col: 100x148 mm postcard, all margins 0 for borderless printing.</summary>
    private static void WriteMediaCol(Stream stream, bool borderless)
    {
        WriteRaw(stream, TagBegCollection, "media-col", []);

        WriteRaw(stream, TagMemberAttrName, "", Encoding.ASCII.GetBytes("media-size"));
        WriteRaw(stream, TagBegCollection, "", []);
        WriteRaw(stream, TagMemberAttrName, "", Encoding.ASCII.GetBytes("x-dimension"));
        WriteRaw(stream, TagInteger, "", Int32(10000)); // hundredths of a millimeter
        WriteRaw(stream, TagMemberAttrName, "", Encoding.ASCII.GetBytes("y-dimension"));
        WriteRaw(stream, TagInteger, "", Int32(14800));
        WriteRaw(stream, TagEndCollection, "", []);

        if (borderless)
        {
            foreach (var margin in new[] { "media-top-margin", "media-bottom-margin", "media-left-margin", "media-right-margin" })
            {
                WriteRaw(stream, TagMemberAttrName, "", Encoding.ASCII.GetBytes(margin));
                WriteRaw(stream, TagInteger, "", Int32(0));
            }
        }

        WriteRaw(stream, TagEndCollection, "", []);
    }

    private static void WriteHeader(Stream stream, ushort operation, int requestId)
    {
        Span<byte> header = stackalloc byte[8];
        header[0] = 2; // IPP 2.0
        header[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], operation);
        BinaryPrimitives.WriteInt32BigEndian(header[4..8], requestId);
        stream.Write(header);
    }

    private static void WriteAttribute(Stream stream, byte tag, string name, string value) =>
        WriteRaw(stream, tag, name, Encoding.UTF8.GetBytes(value));

    private static void WriteAttribute(Stream stream, byte tag, string name, int value) =>
        WriteRaw(stream, tag, name, Int32(value));

    private static void WriteRaw(Stream stream, byte tag, string name, byte[] value)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        Span<byte> length = stackalloc byte[2];

        stream.WriteByte(tag);
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)nameBytes.Length);
        stream.Write(length);
        stream.Write(nameBytes);
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>Extracts the optional "status-message" from an IPP response for a readable error.</summary>
    private static string FindStatusMessage(byte[] body)
    {
        var marker = Encoding.ASCII.GetBytes("status-message");
        var index = body.AsSpan().IndexOf(marker);
        if (index < 0 || index + marker.Length + 2 > body.Length)
            return "";

        var valueLength = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(index + marker.Length, 2));
        var start = index + marker.Length + 2;
        return start + valueLength <= body.Length ? ": " + Encoding.UTF8.GetString(body, start, valueLength) : "";
    }
}
