using Lumio.Imaging;
using SkiaSharp;
using Xunit;

namespace Lumio.Tests;

public class PhotoLookTests
{
    private static byte[] CreatePhoto(Func<int, int, SKColor> pixel, int width = 120, int height = 80)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, pixel(x, y));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        return data.ToArray();
    }

    private static SKColor Center(byte[] jpeg)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
    }

    [Fact]
    public void Original_ReturnsThePhotoUnchanged()
    {
        var photo = CreatePhoto((_, _) => SKColors.OrangeRed);
        Assert.Same(photo, PhotoLooks.Apply(photo, PhotoLook.Original));
    }

    [Fact]
    public void Glam_IsBlackAndWhite()
    {
        var result = Center(PhotoLooks.Apply(CreatePhoto((_, _) => new SKColor(200, 60, 40)), PhotoLook.Glam));
        Assert.InRange(Math.Abs(result.Red - result.Blue), 0, 6);
        Assert.InRange(Math.Abs(result.Red - result.Green), 0, 6);
    }

    [Fact]
    public void Sonnig_IsWarmerThanTheOriginal()
    {
        var gray = new SKColor(128, 128, 128);
        var result = Center(PhotoLooks.Apply(CreatePhoto((_, _) => gray), PhotoLook.Sonnig));
        Assert.True(result.Red > result.Blue + 10, $"{result}");
    }

    [Theory]
    [InlineData(PhotoLook.Sonnig)]
    [InlineData(PhotoLook.Strahlend)]
    [InlineData(PhotoLook.Glam)]
    [InlineData(PhotoLook.Vintage)]
    [InlineData(PhotoLook.Kino)]
    public void EveryLook_KeepsTheSize_AndMakesAPreview(PhotoLook look)
    {
        var photo = CreatePhoto((x, y) => new SKColor((byte)(x * 2), (byte)(y * 3), 90));
        using var result = SKBitmap.Decode(PhotoLooks.Apply(photo, look));
        Assert.Equal(120, result.Width);
        Assert.Equal(80, result.Height);

        using var preview = SKBitmap.Decode(PhotoLooks.Preview(photo, look, 60));
        Assert.Equal(60, preview.Width);
    }

    [Fact]
    public void BeautyFilter_SmoothsSkin_ButLeavesOtherColors()
    {
        // Noisy skin tone on the left, noisy blue on the right
        var random = new Random(7);
        var photo = CreatePhoto((x, _) =>
        {
            var noise = random.Next(-25, 26);
            return x < 60
                ? new SKColor((byte)(215 + noise / 2), (byte)(165 + noise), (byte)(135 + noise))
                : new SKColor((byte)(40 + noise), (byte)(80 + noise), (byte)(200 + noise));
        }, 120, 80);

        var smoothed = BeautyFilter.Apply(photo, strength: 3, jpegQuality: 100);

        Assert.True(Noise(smoothed, 5, 50) < Noise(photo, 5, 50) * 0.8, "skin should get smoother");
        Assert.True(Noise(smoothed, 70, 115) > Noise(photo, 70, 115) * 0.9, "blue should stay as it is");
        Assert.Same(photo, BeautyFilter.Apply(photo, strength: 0));
    }

    /// <summary>Mean absolute difference between horizontal neighbors in a column range.</summary>
    private static double Noise(byte[] jpeg, int fromX, int toX)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        double sum = 0;
        var count = 0;
        for (var y = 10; y < bitmap.Height - 10; y++)
            for (var x = fromX; x < toX; x++)
            {
                sum += Math.Abs(bitmap.GetPixel(x, y).Green - bitmap.GetPixel(x + 1, y).Green);
                count++;
            }
        return sum / count;
    }
}
