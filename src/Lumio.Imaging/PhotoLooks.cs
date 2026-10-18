using SkiaSharp;

namespace Lumio.Imaging;

/// <summary>Color looks guests can put on their photo in the review screen.</summary>
public enum PhotoLook
{
    Original,
    Sonnig,
    Strahlend,
    Glam,
    Vintage,
    Kino,
}

/// <summary>
/// Applies <see cref="PhotoLook"/>s: per-channel tone curves plus a saturation matrix (cheap color filters,
/// fast even for a full-size collage on a phone). Glam additionally gets a soft glow.
/// </summary>
public static class PhotoLooks
{
    public static IReadOnlyList<(PhotoLook Look, string Name)> All { get; } =
    [
        (PhotoLook.Original, "Original"),
        (PhotoLook.Sonnig, "Sonnig"),
        (PhotoLook.Strahlend, "Strahlend"),
        (PhotoLook.Glam, "Glam S/W"),
        (PhotoLook.Vintage, "Vintage"),
        (PhotoLook.Kino, "Kino"),
    ];

    /// <summary>Applies a look to a JPEG; <see cref="PhotoLook.Original"/> returns the input unchanged.</summary>
    public static byte[] Apply(byte[] jpeg, PhotoLook look, int jpegQuality = 95)
    {
        if (look == PhotoLook.Original)
            return jpeg;

        using var source = SKBitmap.Decode(jpeg) ?? throw new ArgumentException("Bild konnte nicht gelesen werden.", nameof(jpeg));
        return Render(source, look, jpegQuality);
    }

    /// <summary>Small preview of a look for the selection buttons.</summary>
    public static byte[] Preview(byte[] jpeg, PhotoLook look, int width)
    {
        using var source = SKBitmap.Decode(jpeg) ?? throw new ArgumentException("Bild konnte nicht gelesen werden.", nameof(jpeg));
        var height = Math.Max(1, source.Height * width / Math.Max(1, source.Width));
        using var small = source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return Render(small ?? source, look, 85);
    }

    private static byte[] Render(SKBitmap source, PhotoLook look, int jpegQuality)
    {
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        using (var paint = new SKPaint { ColorFilter = CreateFilter(look) })
            canvas.DrawBitmap(source, 0, 0, paint);

        if (look == PhotoLook.Glam)
            DrawGlow(canvas, source, look);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
        return data.ToArray();
    }

    /// <summary>Soft "glamour" glow: a blurred copy screened over the picture.</summary>
    private static void DrawGlow(SKCanvas canvas, SKBitmap source, PhotoLook look)
    {
        var sigma = Math.Max(2f, Math.Min(source.Width, source.Height) * 0.012f);
        using var blur = SKImageFilter.CreateBlur(sigma, sigma);
        using var paint = new SKPaint
        {
            ImageFilter = blur,
            ColorFilter = CreateFilter(look),
            BlendMode = SKBlendMode.Screen,
            Color = SKColors.White.WithAlpha(70),
        };
        canvas.DrawBitmap(source, 0, 0, paint);
    }

    private static SKColorFilter? CreateFilter(PhotoLook look) => look switch
    {
        // Warm late-afternoon light: a little more red, less blue, gentle contrast
        PhotoLook.Sonnig => Compose(
            Saturation(1.12f),
            Curves(
                red: x => SCurve(x, 0.25) * 0.94 + 0.06 * Math.Sqrt(x),
                green: x => SCurve(x, 0.25),
                blue: x => SCurve(x, 0.25) * 0.9 + 0.02)),

        // Punchy party colors
        PhotoLook.Strahlend => Compose(
            Saturation(1.3f),
            Curves(all: x => Lift(SCurve(x, 0.45), 0.03))),

        // Black and white with deep blacks and bright, soft highlights
        PhotoLook.Glam => Compose(
            Curves(all: x => Lift(SCurve(x, 0.6), 0.02)),
            Saturation(0f)),

        // Faded film: lifted blacks, softened whites, warm highlights, muted colors
        PhotoLook.Vintage => Compose(
            Saturation(0.75f),
            Curves(
                red: x => Fade(x, 0.10, 0.96) * 0.97 + 0.03,
                green: x => Fade(x, 0.08, 0.93),
                blue: x => Fade(x, 0.12, 0.84))),

        // Teal shadows, warm skin tones
        PhotoLook.Kino => Compose(
            Saturation(1.08f),
            Curves(
                red: x => SCurve(x, 0.35) * (0.9 + 0.1 * x),
                green: x => SCurve(x, 0.3) + 0.03 * (1 - x),
                blue: x => SCurve(x, 0.3) + 0.08 * (1 - x) - 0.06 * x)),

        _ => null,
    };

    private static SKColorFilter Compose(SKColorFilter outer, SKColorFilter inner) => SKColorFilter.CreateCompose(outer, inner);

    /// <summary>Saturation matrix (Rec. 709 luma weights); 0 = black and white.</summary>
    private static SKColorFilter Saturation(float s)
    {
        const float r = 0.2126f, g = 0.7152f, b = 0.0722f;
        var i = 1 - s;
        return SKColorFilter.CreateColorMatrix(
        [
            r * i + s, g * i,     b * i,     0, 0,
            r * i,     g * i + s, b * i,     0, 0,
            r * i,     g * i,     b * i + s, 0, 0,
            0,         0,         0,         1, 0,
        ]);
    }

    private static SKColorFilter Curves(Func<double, double>? all = null, Func<double, double>? red = null,
        Func<double, double>? green = null, Func<double, double>? blue = null)
    {
        static byte[] Table(Func<double, double> curve)
        {
            var table = new byte[256];
            for (var i = 0; i < 256; i++)
                table[i] = (byte)Math.Clamp(Math.Round(curve(i / 255.0) * 255), 0, 255);
            return table;
        }

        Func<double, double> identity = x => x;
        var alpha = Table(identity);
        return SKColorFilter.CreateTable(alpha, Table(red ?? all ?? identity), Table(green ?? all ?? identity), Table(blue ?? all ?? identity));
    }

    /// <summary>S-shaped contrast curve; <paramref name="amount"/> 0 = linear, 1 = full smoothstep.</summary>
    private static double SCurve(double x, double amount)
    {
        var smooth = x * x * (3 - 2 * x);
        return x + amount * (smooth - x);
    }

    /// <summary>Brightens mid tones a little without clipping the highlights.</summary>
    private static double Lift(double x, double amount) => x + amount * Math.Sin(Math.PI * x);

    /// <summary>Maps 0..1 to black..white (lifted blacks / dimmed whites for a faded film look).</summary>
    private static double Fade(double x, double black, double white) => black + (white - black) * SCurve(x, 0.2);
}
