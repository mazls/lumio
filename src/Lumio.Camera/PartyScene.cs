using SkiaSharp;

namespace Lumio.Camera;

/// <summary>
/// Evening party backdrop in Lumio's colors: soft glows, depth-of-field bokeh lights and a string of fairy lights.
/// Used animated by the test camera and as a still background on the start screen while the
/// battery saver keeps the real camera's live view switched off.
/// </summary>
public static class PartyScene
{
    private static readonly SKColor[] BokehColors =
    [
        new(255, 210, 122), // warm gold
        new(255, 111, 168), // pink
        new(255, 160, 110), // peach
        new(180, 156, 255), // lavender
        new(255, 236, 200), // soft white
    ];

    /// <summary>Renders the still backdrop (no animation) as PNG for the start screen.</summary>
    public static byte[] RenderStillPng(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        DrawBackdrop(surface.Canvas, width, height, 0f, animated: false);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <param name="time">Seconds; drives the drifting lights and twinkling bulbs when <paramref name="animated"/>.</param>
    public static void DrawBackdrop(SKCanvas canvas, int width, int height, float time, bool animated)
    {
        var t = animated ? time : 0f;
        var size = MathF.Max(width, height);

        DrawSky(canvas, width, height, size, t);
        DrawBokeh(canvas, width, height, size, t);
        DrawFairyLights(canvas, width, height, size, t, animated);
        DrawVignette(canvas, width, height, size);
    }

    private static void DrawSky(SKCanvas canvas, int width, int height, float size, float t)
    {
        using var paint = new SKPaint { IsAntialias = true, IsDither = true };

        // Deep evening gradient
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, height),
            [new SKColor(30, 12, 48), new SKColor(44, 16, 60), new SKColor(14, 12, 26)],
            [0f, 0.45f, 1f],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, width, height, paint);

        // Large colored glows (slowly breathing when animated)
        var breathe = 1f + 0.06f * MathF.Sin(t * 0.6f);
        DrawGlow(canvas, paint, width * 0.5f, height * 0.38f, size * 0.55f * breathe, new SKColor(255, 77, 141, 70));
        DrawGlow(canvas, paint, width * 0.12f, height * 0.85f, size * 0.45f, new SKColor(255, 138, 77, 55));
        DrawGlow(canvas, paint, width * 0.9f, height * 0.7f, size * 0.4f, new SKColor(120, 90, 255, 45));
    }

    private static void DrawGlow(SKCanvas canvas, SKPaint paint, float x, float y, float radius, SKColor color)
    {
        paint.Shader = SKShader.CreateRadialGradient(
            new SKPoint(x, y), radius,
            [color, color.WithAlpha(0)],
            [0f, 1f],
            SKShaderTileMode.Clamp);
        canvas.DrawCircle(x, y, radius, paint);
    }

    private static void DrawBokeh(SKCanvas canvas, int width, int height, float size, float t)
    {
        // Fixed seed: the same pleasant layout every time
        var random = new Random(1337);
        using var fill = new SKPaint { IsAntialias = true, IsDither = true };
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        for (var i = 0; i < 46; i++)
        {
            var depth = (float)random.NextDouble(); // 0 = far (small, blurry), 1 = near (big, very blurry)
            var radius = size * (0.008f + 0.045f * depth * depth + 0.01f * (float)random.NextDouble());
            var baseX = (float)random.NextDouble();
            var baseY = 0.15f + 0.8f * (float)random.NextDouble();
            var phase = (float)random.NextDouble() * MathF.Tau;
            var color = BokehColors[random.Next(BokehColors.Length)];

            // Gentle drift, nearer lights move more (parallax)
            var x = width * baseX + size * 0.02f * (0.3f + depth) * MathF.Sin(t * 0.25f + phase);
            var y = height * baseY + size * 0.012f * (0.3f + depth) * MathF.Cos(t * 0.2f + phase);
            var alpha = (byte)(35 + 70 * (1f - depth * 0.6f) * (0.75f + 0.25f * MathF.Sin(t * 0.9f + phase)));

            var blur = radius * (0.12f + 0.45f * depth);
            using var mask = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur);
            fill.MaskFilter = mask;
            fill.Color = color.WithAlpha(alpha);
            canvas.DrawCircle(x, y, radius, fill);

            // Lens-like bright rim on sharper lights
            if (depth < 0.55f)
            {
                ring.MaskFilter = mask;
                ring.StrokeWidth = MathF.Max(1f, radius * 0.08f);
                ring.Color = color.WithAlpha((byte)(alpha * 0.9f));
                canvas.DrawCircle(x, y, radius * 0.96f, ring);
            }
        }
    }

    private static void DrawFairyLights(SKCanvas canvas, int width, int height, float size, float t, bool animated)
    {
        using var wire = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1f, size * 0.0016f),
            Color = new SKColor(20, 10, 25, 200),
        };
        using var glow = new SKPaint { IsAntialias = true };
        using var bulb = new SKPaint { IsAntialias = true };
        using var glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.012f);
        glow.MaskFilter = glowBlur;

        // Two sagging strings across the upper part of the picture
        (float Left, float Right, float Sag)[] strings = [(0.04f, 0.38f, 0.07f), (0.03f, 0.2f, 0.05f)];
        var stringIndex = 0;
        foreach (var (left, right, sag) in strings)
        {
            var y0 = height * left;
            var y1 = height * right;
            var sagHeight = height * sag + size * 0.04f;

            using var builder = new SKPathBuilder();
            const int Segments = 60;
            for (var s = 0; s <= Segments; s++)
            {
                var u = s / (float)Segments;
                var point = StringPoint(width, y0, y1, sagHeight, u, stringIndex);
                if (s == 0) builder.MoveTo(point); else builder.LineTo(point);
            }
            using var path = builder.Detach();
            canvas.DrawPath(path, wire);

            var bulbCount = 14 + stringIndex * 4;
            for (var b = 1; b < bulbCount; b++)
            {
                var u = b / (float)bulbCount;
                var p = StringPoint(width, y0, y1, sagHeight, u, stringIndex);
                var twinkle = animated ? 0.7f + 0.3f * MathF.Sin(t * 2.2f + b * 1.7f + stringIndex) : 1f;
                var r = size * 0.0055f;

                glow.Color = new SKColor(255, 196, 110, (byte)(150 * twinkle));
                canvas.DrawCircle(p.X, p.Y + r, r * 3.2f, glow);
                bulb.Color = new SKColor(255, 238, 200, (byte)(235 * twinkle));
                canvas.DrawCircle(p.X, p.Y + r, r, bulb);
            }
            stringIndex++;
        }
    }

    /// <summary>Point on a hanging string between the left and right edge (catenary-like parabola).</summary>
    private static SKPoint StringPoint(int width, float y0, float y1, float sag, float u, int index)
    {
        var x = -width * 0.02f + width * 1.04f * u;
        var y = y0 + (y1 - y0) * u + sag * 4f * u * (1f - u);
        if (index == 1)
            y = y1 + (y0 - y1) * u + sag * 4f * u * (1f - u); // second string hangs the other way round
        return new SKPoint(x, y);
    }

    private static void DrawVignette(SKCanvas canvas, int width, int height, float size)
    {
        using var paint = new SKPaint { IsAntialias = true, IsDither = true };
        paint.Shader = SKShader.CreateRadialGradient(
            new SKPoint(width * 0.5f, height * 0.5f), size * 0.75f,
            [SKColors.Transparent, new SKColor(0, 0, 0, 150)],
            [0.55f, 1f],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, width, height, paint);
    }
}
