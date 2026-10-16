using System.Runtime.InteropServices;
using Lumio.Core.Camera;
using SkiaSharp;

namespace Lumio.Imaging;

/// <summary>
/// High-performance, pure C# GIF89a encoder for animated GIFs and Boomerangs.
/// Generates standard, cross-platform animated GIF files without native dependencies.
/// </summary>
public static class GifEncoder
{
    public static SKBitmap FrameToBitmap(LiveViewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Format == FrameFormat.Jpeg)
        {
            return SKBitmap.Decode(frame.Data);
        }

        var bmp = new SKBitmap(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(frame.Data, 0, bmp.GetPixels(), Math.Min(frame.Data.Length, bmp.ByteCount));
        return bmp;
    }

    /// <summary>
    /// Encodes captured live view burst frames into a bouncy ping-pong Boomerang GIF.
    /// </summary>
    public static byte[] EncodeBoomerang(
        IReadOnlyList<LiveViewFrame> liveFrames,
        int frameDelayMs = 90,
        int maxWidth = 640,
        int maxHeight = 480,
        string? overlayText = null)
    {
        ArgumentNullException.ThrowIfNull(liveFrames);
        if (liveFrames.Count == 0)
            throw new ArgumentException("Mindestens ein Live-Frame erforderlich.", nameof(liveFrames));

        var forwardBitmaps = new List<SKBitmap>(liveFrames.Count);
        try
        {
            foreach (var frame in liveFrames)
            {
                using var original = FrameToBitmap(frame);
                if (original is null) continue;

                var scale = Math.Min((float)maxWidth / original.Width, (float)maxHeight / original.Height);
                if (scale > 1.0f) scale = 1.0f;

                var targetW = Math.Max(1, (int)(original.Width * scale));
                var targetH = Math.Max(1, (int)(original.Height * scale));

                var resized = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(resized))
                {
                    canvas.Clear(SKColors.Black);
                    var srcRect = new SKRect(0, 0, original.Width, original.Height);
                    var dstRect = new SKRect(0, 0, targetW, targetH);
                    using var originalImage = SKImage.FromBitmap(original);
                    canvas.DrawImage(originalImage, srcRect, dstRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), null);

                    if (!string.IsNullOrWhiteSpace(overlayText))
                    {
                        using var font = new SKFont(SKTypeface.Default, 18);
                        using var textPaint = new SKPaint
                        {
                            Color = new SKColor(255, 255, 255, 210),
                            IsAntialias = true,
                        };
                        using var shadowPaint = new SKPaint
                        {
                            Color = new SKColor(0, 0, 0, 180),
                            IsAntialias = true,
                        };
                        var x = targetW - 16;
                        var y = targetH - 14;
                        canvas.DrawText(overlayText, x + 1, y + 1, SKTextAlign.Right, font, shadowPaint);
                        canvas.DrawText(overlayText, x, y, SKTextAlign.Right, font, textPaint);
                    }
                }
                forwardBitmaps.Add(resized);
            }

            var boomerangList = CreateBoomerangSequence(forwardBitmaps);

            using var ms = new MemoryStream();
            Encode(boomerangList, frameDelayMs, ms, loop: true);
            return ms.ToArray();
        }
        finally
        {
            foreach (var bmp in forwardBitmaps)
                bmp.Dispose();
        }
    }

    /// <summary>
    /// Creates an animated GIF from a list of JPEG frame buffers.
    /// Frames are resized to max dimensions for optimal file size and fast sharing.
    /// </summary>
    public static byte[] EncodeFromJpegs(
        IReadOnlyList<byte[]> jpegFrames,
        int frameDelayMs,
        int maxWidth = 640,
        int maxHeight = 480,
        string? overlayText = null)
    {
        ArgumentNullException.ThrowIfNull(jpegFrames);
        if (jpegFrames.Count == 0)
            throw new ArgumentException("Mindestens ein Frame erforderlich.", nameof(jpegFrames));

        var bitmaps = new List<SKBitmap>(jpegFrames.Count);
        try
        {
            foreach (var bytes in jpegFrames)
            {
                using var original = SKBitmap.Decode(bytes);
                if (original is null)
                    continue;

                // Scale down keeping aspect ratio
                var scale = Math.Min((float)maxWidth / original.Width, (float)maxHeight / original.Height);
                if (scale > 1.0f) scale = 1.0f;

                var targetW = Math.Max(1, (int)(original.Width * scale));
                var targetH = Math.Max(1, (int)(original.Height * scale));

                var resized = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(resized))
                {
                    canvas.Clear(SKColors.Black);
                    var srcRect = new SKRect(0, 0, original.Width, original.Height);
                    var dstRect = new SKRect(0, 0, targetW, targetH);
                    using var originalImage = SKImage.FromBitmap(original);
                    canvas.DrawImage(originalImage, srcRect, dstRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), null);

                    // Optional subtle bottom branding overlay
                    if (!string.IsNullOrWhiteSpace(overlayText))
                    {
                        using var font = new SKFont(SKTypeface.Default, 18);
                        using var textPaint = new SKPaint
                        {
                            Color = new SKColor(255, 255, 255, 210),
                            IsAntialias = true,
                        };
                        using var shadowPaint = new SKPaint
                        {
                            Color = new SKColor(0, 0, 0, 180),
                            IsAntialias = true,
                        };

                        var x = targetW - 16;
                        var y = targetH - 14;
                        canvas.DrawText(overlayText, x + 1, y + 1, SKTextAlign.Right, font, shadowPaint);
                        canvas.DrawText(overlayText, x, y, SKTextAlign.Right, font, textPaint);
                    }
                }
                bitmaps.Add(resized);
            }

            using var ms = new MemoryStream();
            Encode(bitmaps, frameDelayMs, ms, loop: true);
            return ms.ToArray();
        }
        finally
        {
            foreach (var bmp in bitmaps)
                bmp.Dispose();
        }
    }

    /// <summary>
    /// Creates boomerang ping-pong sequence: [0, 1, ..., N-1, N-2, ..., 1].
    /// </summary>
    public static IReadOnlyList<T> CreateBoomerangSequence<T>(IReadOnlyList<T> forwardFrames)
    {
        ArgumentNullException.ThrowIfNull(forwardFrames);
        if (forwardFrames.Count <= 2)
            return forwardFrames;

        var result = new List<T>(forwardFrames.Count * 2 - 2);
        result.AddRange(forwardFrames);
        for (var i = forwardFrames.Count - 2; i > 0; i--)
        {
            result.Add(forwardFrames[i]);
        }
        return result;
    }

    /// <summary>
    /// Encodes a sequence of SKBitmaps into an animated GIF89a stream.
    /// </summary>
    public static void Encode(
        IReadOnlyList<SKBitmap> frames,
        int frameDelayMs,
        Stream output,
        bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(output);
        if (frames.Count == 0)
            throw new ArgumentException("Frames dürfen nicht leer sein.", nameof(frames));

        var width = frames[0].Width;
        var height = frames[0].Height;
        var delayHundredths = Math.Max(1, frameDelayMs / 10);

        // 1. Header: GIF89a
        output.Write("GIF89a"u8);

        // 2. Logical Screen Descriptor (16-bit width, 16-bit height, packed, bg, aspect)
        WriteShort(output, width);
        WriteShort(output, height);
        output.WriteByte(0x70); // No global color table, 8 bits/pixel color resolution
        output.WriteByte(0x00); // Background color index
        output.WriteByte(0x00); // Pixel aspect ratio

        // 3. Netscape 2.0 Loop Extension
        if (loop)
        {
            output.WriteByte(0x21); // Extension Introducer
            output.WriteByte(0xFF); // Application Extension Label
            output.WriteByte(0x0B); // Block Size (11 bytes)
            output.Write("NETSCAPE2.0"u8);
            output.WriteByte(0x03); // Sub-block length
            output.WriteByte(0x01); // Loop sub-block ID
            WriteShort(output, 0);  // 0 = Loop indefinitely
            output.WriteByte(0x00); // Block Terminator
        }

        // 4. Frames
        foreach (var frame in frames)
        {
            var (palette, indexedPixels) = QuantizeFrame(frame);

            // Graphics Control Extension (GCE)
            output.WriteByte(0x21); // Extension Introducer
            output.WriteByte(0xF9); // Graphic Control Label
            output.WriteByte(0x04); // Block Size
            output.WriteByte(0x04); // Packed: Disposal method 1 (do not dispose / overwrite)
            WriteShort(output, delayHundredths); // Frame delay in 1/100s
            output.WriteByte(0x00); // Transparent color index (unused)
            output.WriteByte(0x00); // Block Terminator

            // Image Descriptor
            output.WriteByte(0x2C); // Image Separator
            WriteShort(output, 0);  // Image Left
            WriteShort(output, 0);  // Image Top
            WriteShort(output, frame.Width);
            WriteShort(output, frame.Height);
            output.WriteByte(0x87); // Packed: Local Color Table present, 256 colors (0x80 | 0x07)

            // Local Color Table (256 * 3 bytes RGB)
            output.Write(palette);

            // Image Data (LZW compressed)
            LzwCompress(8, indexedPixels, output);
        }

        // 5. Trailer
        output.WriteByte(0x3B);
    }

    private static void WriteShort(Stream s, int val)
    {
        s.WriteByte((byte)(val & 0xFF));
        s.WriteByte((byte)((val >> 8) & 0xFF));
    }

    /// <summary>
    /// Fast Median-Cut color quantizer producing a 256-color palette (768 bytes RGB)
    /// and an indexed byte array for each pixel.
    /// </summary>
    private static (byte[] Palette, byte[] IndexedPixels) QuantizeFrame(SKBitmap frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        var totalPixels = width * height;
        var pixels = frame.Pixels;

        // Sample colors to find color box
        var uniqueColors = new HashSet<SKColor>();
        var step = Math.Max(1, totalPixels / 10000); // Sample for speed
        for (var i = 0; i < totalPixels; i += step)
        {
            uniqueColors.Add(pixels[i]);
        }

        // Median cut partition
        var boxes = new List<ColorBox> { new ColorBox(uniqueColors.ToList()) };
        while (boxes.Count < 256)
        {
            // Find box with greatest range
            var splitIndex = -1;
            var maxRange = -1;
            for (var i = 0; i < boxes.Count; i++)
            {
                var r = boxes[i].MaxRange;
                if (r > maxRange && boxes[i].Colors.Count > 1)
                {
                    maxRange = r;
                    splitIndex = i;
                }
            }

            if (splitIndex == -1)
                break;

            var (b1, b2) = boxes[splitIndex].Split();
            boxes[splitIndex] = b1;
            boxes.Add(b2);
        }

        // Build 256-color palette
        var palette = new byte[256 * 3];
        var paletteColors = new (int R, int G, int B)[256];
        for (var i = 0; i < boxes.Count; i++)
        {
            var avg = boxes[i].AverageColor;
            palette[i * 3 + 0] = avg.Red;
            palette[i * 3 + 1] = avg.Green;
            palette[i * 3 + 2] = avg.Blue;
            paletteColors[i] = (avg.Red, avg.Green, avg.Blue);
        }

        // Fill remainder if < 256 boxes
        for (var i = boxes.Count; i < 256; i++)
        {
            palette[i * 3 + 0] = 0;
            palette[i * 3 + 1] = 0;
            palette[i * 3 + 2] = 0;
            paletteColors[i] = (0, 0, 0);
        }

        // Fast color mapping cache (32x32x32 = 5-bit lookup table)
        var cache = new byte[32, 32, 32];
        var cached = new bool[32, 32, 32];

        var indexedPixels = new byte[totalPixels];
        for (var i = 0; i < totalPixels; i++)
        {
            var c = pixels[i];
            int r5 = c.Red >> 3;
            int g5 = c.Green >> 3;
            int b5 = c.Blue >> 3;

            if (cached[r5, g5, b5])
            {
                indexedPixels[i] = cache[r5, g5, b5];
            }
            else
            {
                // Find nearest color in palette
                var bestIndex = 0;
                var bestDist = int.MaxValue;
                for (var p = 0; p < boxes.Count; p++)
                {
                    var pc = paletteColors[p];
                    var dr = c.Red - pc.R;
                    var dg = c.Green - pc.G;
                    var db = c.Blue - pc.B;
                    var dist = dr * dr + dg * dg + db * db;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIndex = p;
                        if (dist == 0) break;
                    }
                }
                cache[r5, g5, b5] = (byte)bestIndex;
                cached[r5, g5, b5] = true;
                indexedPixels[i] = (byte)bestIndex;
            }
        }

        return (palette, indexedPixels);
    }

    private sealed class ColorBox
    {
        public List<SKColor> Colors { get; }
        public int MinR, MaxR, MinG, MaxG, MinB, MaxB;

        public ColorBox(List<SKColor> colors)
        {
            Colors = colors;
            CalculateBounds();
        }

        private void CalculateBounds()
        {
            MinR = MinG = MinB = 255;
            MaxR = MaxG = MaxB = 0;
            foreach (var c in Colors)
            {
                if (c.Red < MinR) MinR = c.Red;
                if (c.Red > MaxR) MaxR = c.Red;
                if (c.Green < MinG) MinG = c.Green;
                if (c.Green > MaxG) MaxG = c.Green;
                if (c.Blue < MinB) MinB = c.Blue;
                if (c.Blue > MaxB) MaxB = c.Blue;
            }
        }

        public int MaxRange => Math.Max(MaxR - MinR, Math.Max(MaxG - MinG, MaxB - MinB));

        public SKColor AverageColor
        {
            get
            {
                if (Colors.Count == 0) return SKColors.Black;
                long r = 0, g = 0, b = 0;
                foreach (var c in Colors)
                {
                    r += c.Red;
                    g += c.Green;
                    b += c.Blue;
                }
                return new SKColor((byte)(r / Colors.Count), (byte)(g / Colors.Count), (byte)(b / Colors.Count));
            }
        }

        public (ColorBox, ColorBox) Split()
        {
            var rangeR = MaxR - MinR;
            var rangeG = MaxG - MinG;
            var rangeB = MaxB - MinB;

            if (rangeR >= rangeG && rangeR >= rangeB)
                Colors.Sort((c1, c2) => c1.Red.CompareTo(c2.Red));
            else if (rangeG >= rangeR && rangeG >= rangeB)
                Colors.Sort((c1, c2) => c1.Green.CompareTo(c2.Green));
            else
                Colors.Sort((c1, c2) => c1.Blue.CompareTo(c2.Blue));

            var mid = Colors.Count / 2;
            var b1 = new ColorBox(Colors.GetRange(0, mid));
            var b2 = new ColorBox(Colors.GetRange(mid, Colors.Count - mid));
            return (b1, b2);
        }
    }

    private static void LzwCompress(int initCodeSize, byte[] data, Stream output)
    {
        output.WriteByte((byte)initCodeSize);

        var clearCode = 1 << initCodeSize;
        var eofCode = clearCode + 1;
        var firstFree = clearCode + 2;

        var codeSize = initCodeSize + 1;
        var maxCode = (1 << codeSize) - 1;
        var freeEnt = firstFree;
        var clearFlg = false;

        // Hash table for (prefix << 8) | c -> code
        // 5003 is prime for fast hash distribution
        const int hSize = 5003;
        var htab = new int[hSize];
        var codetab = new int[hSize];
        Array.Fill(htab, -1);

        var curAccum = 0;
        var curBits = 0;
        var packet = new byte[256];
        var packetLen = 0;

        void OutputCode(int code)
        {
            curAccum |= (code << curBits);
            curBits += codeSize;

            while (curBits >= 8)
            {
                packet[packetLen++] = (byte)(curAccum & 0xFF);
                curAccum >>= 8;
                curBits -= 8;

                if (packetLen >= 254)
                {
                    output.WriteByte((byte)packetLen);
                    output.Write(packet, 0, packetLen);
                    packetLen = 0;
                }
            }

            if (freeEnt > maxCode || clearFlg)
            {
                if (clearFlg)
                {
                    codeSize = initCodeSize + 1;
                    maxCode = (1 << codeSize) - 1;
                    clearFlg = false;
                }
                else
                {
                    codeSize++;
                    maxCode = codeSize == 12 ? 4096 : ((1 << codeSize) - 1);
                }
            }

            if (code == eofCode)
            {
                while (curBits > 0)
                {
                    packet[packetLen++] = (byte)(curAccum & 0xFF);
                    curAccum >>= 8;
                    curBits -= 8;
                }
                if (packetLen > 0)
                {
                    output.WriteByte((byte)packetLen);
                    output.Write(packet, 0, packetLen);
                    packetLen = 0;
                }
            }
        }

        void ClearTable()
        {
            Array.Fill(htab, -1);
            freeEnt = firstFree;
            clearFlg = true;
            OutputCode(clearCode);
        }

        // Start with Clear Code
        OutputCode(clearCode);

        if (data.Length == 0)
        {
            OutputCode(eofCode);
            output.WriteByte(0x00);
            return;
        }

        var ent = (int)data[0];

        for (var i = 1; i < data.Length; i++)
        {
            var c = (int)data[i];
            var fcode = (c << 12) + ent;
            var h = (c << 4) ^ ent;
            if (h >= hSize) h -= hSize;

            if (htab[h] == fcode)
            {
                ent = codetab[h];
                continue;
            }

            var disp = h == 0 ? 1 : hSize - h;
            var matched = false;

            if (htab[h] >= 0)
            {
                while (true)
                {
                    h -= disp;
                    if (h < 0) h += hSize;

                    if (htab[h] == fcode)
                    {
                        ent = codetab[h];
                        matched = true;
                        break;
                    }
                    if (htab[h] < 0)
                        break;
                }
            }

            if (matched)
                continue;

            OutputCode(ent);
            ent = c;

            if (freeEnt < 4096)
            {
                codetab[h] = freeEnt++;
                htab[h] = fcode;
            }
            else
            {
                ClearTable();
            }
        }

        OutputCode(ent);
        OutputCode(eofCode);
        output.WriteByte(0x00);
    }
}
