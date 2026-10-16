using Lumio.Imaging;
using SkiaSharp;
using Xunit;

namespace Lumio.Tests;

public class CollageTests
{
    private static byte[] CreateSamplePhoto(int width, int height, SKColor color)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    [Fact]
    public void Render_SinglePhotoPostcard_ProducesExpectedDimensions()
    {
        var template = BuiltInTemplates.CreateSinglePhotoPostcard();
        var renderer = new CollageRenderer();

        var photo = CreateSamplePhoto(3000, 2000, SKColors.Crimson);
        var rendered = renderer.Render(template, [photo], new Dictionary<string, string>
        {
            ["EventName"] = "Marcel & Julia",
            ["Date"] = "16.10.2026"
        });

        Assert.NotNull(rendered);
        Assert.True(rendered.Length > 1000);

        using var bitmap = SKBitmap.Decode(rendered);
        Assert.Equal(template.Width, bitmap.Width);
        Assert.Equal(template.Height, bitmap.Height);
    }

    [Fact]
    public void Render_QuadCollage_Renders4Photos()
    {
        var template = BuiltInTemplates.CreateQuadCollage();
        var renderer = new CollageRenderer();

        var photos = new[]
        {
            CreateSamplePhoto(1920, 1080, SKColors.DodgerBlue),
            CreateSamplePhoto(1920, 1080, SKColors.DarkOrange),
            CreateSamplePhoto(1920, 1080, SKColors.MediumSeaGreen),
            CreateSamplePhoto(1920, 1080, SKColors.MediumPurple)
        };

        var rendered = renderer.Render(template, photos, new Dictionary<string, string>
        {
            ["EventName"] = "Sommerfest"
        });

        Assert.NotNull(rendered);
        using var bitmap = SKBitmap.Decode(rendered);
        Assert.Equal(1800, bitmap.Width);
        Assert.Equal(1200, bitmap.Height);
    }

    [Fact]
    public void Render_DoubleStripForSelphy_RendersCorrectly()
    {
        var template = BuiltInTemplates.CreateDoubleStripForSelphy();
        var renderer = new CollageRenderer();

        var photos = new[]
        {
            CreateSamplePhoto(1000, 1000, SKColors.Teal),
            CreateSamplePhoto(1000, 1000, SKColors.Gold),
            CreateSamplePhoto(1000, 1000, SKColors.Coral)
        };

        var rendered = renderer.Render(template, photos, new Dictionary<string, string>
        {
            ["EventName"] = "Hochzeit 2026",
            ["Date"] = "Oktober 2026"
        });

        Assert.NotNull(rendered);
        using var bitmap = SKBitmap.Decode(rendered);
        Assert.Equal(1800, bitmap.Width);
        Assert.Equal(1200, bitmap.Height);
    }
}
