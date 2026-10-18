using SkiaSharp;

namespace Lumio.Imaging;

/// <summary>
/// Optional skin smoothing ("beauty filter"): blends skin-colored areas with a blurred copy of the picture.
/// Edges (eyes, lips, hair, outlines) differ strongly from the blur and are left sharp, as are all colors
/// outside the skin tone range - no face detection needed.
/// </summary>
public static class BeautyFilter
{
    /// <param name="strength">0 = off, 1 = light, 2 = medium, 3 = strong.</param>
    public static byte[] Apply(byte[] jpeg, int strength, int jpegQuality = 95)
    {
        if (strength <= 0)
            return jpeg;

        using var source = SKBitmap.Decode(jpeg);
        if (source is null)
            return jpeg;

        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var original = new SKBitmap(info);
        using var blurred = new SKBitmap(info);
        if (!source.CopyTo(original, SKColorType.Rgba8888))
            return jpeg;

        // Blur radius relative to the picture so the effect looks the same at every resolution
        var sigma = Math.Max(1.5f, Math.Min(info.Width, info.Height) * (0.0035f + 0.0015f * strength));
        using (var canvas = new SKCanvas(blurred))
        using (var blur = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { ImageFilter = blur })
        {
            canvas.DrawBitmap(original, 0, 0, paint);
        }

        Smooth(original, blurred, maxBlend: 0.35f + 0.17f * Math.Min(strength, 3));

        using var image = SKImage.FromBitmap(original);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
        return data.ToArray();
    }

    private static unsafe void Smooth(SKBitmap target, SKBitmap blurred, float maxBlend)
    {
        var width = target.Width;
        var rowBytes = target.RowBytes;
        var targetPixels = (byte*)target.GetPixels();
        var blurPixels = (byte*)blurred.GetPixels();
        var targetBase = (nint)targetPixels;
        var blurBase = (nint)blurPixels;

        Parallel.For(0, target.Height, y =>
        {
            var row = (byte*)targetBase + y * rowBytes;
            var blurRow = (byte*)blurBase + y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                var q = blurRow + x * 4;
                int r = p[0], g = p[1], b = p[2];

                var skin = SkinLikelihood(r, g, b);
                if (skin <= 0f)
                    continue;

                // Keep details: the more a pixel differs from its blurred surroundings, the less it is smoothed
                var lumaDiff = Math.Abs((q[0] - r) * 0.3f + (q[1] - g) * 0.59f + (q[2] - b) * 0.11f);
                var detail = Math.Clamp(1f - lumaDiff / 22f, 0f, 1f);
                var w = maxBlend * skin * detail;
                if (w <= 0.01f)
                    continue;

                p[0] = (byte)(r + (q[0] - r) * w);
                p[1] = (byte)(g + (q[1] - g) * w);
                p[2] = (byte)(b + (q[2] - b) * w);
            }
        });
    }

    /// <summary>0..1: how much a color looks like skin (YCbCr skin range with soft borders, all skin tones).</summary>
    private static float SkinLikelihood(int r, int g, int b)
    {
        var y = 0.299f * r + 0.587f * g + 0.114f * b;
        if (y < 45f)
            return 0f;

        var cb = 128f - 0.168736f * r - 0.331264f * g + 0.5f * b;
        var cr = 128f + 0.5f * r - 0.418688f * g - 0.081312f * b;

        static float Range(float v, float low, float high, float soft) =>
            Math.Clamp(Math.Min(v - low, high - v) / soft + 1f, 0f, 1f);

        return Range(cb, 80f, 125f, 6f) * Range(cr, 135f, 172f, 6f);
    }
}
