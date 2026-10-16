using Lumio.Imaging;
using SkiaSharp;
using Xunit;

namespace Lumio.Tests;

public sealed class GifTests
{
    [Fact]
    public void CreateBoomerangSequence_BouncesBackAndForthCorrectly()
    {
        var input = new List<int> { 0, 1, 2, 3 };
        var boomerang = GifEncoder.CreateBoomerangSequence(input);

        // Expected: [0, 1, 2, 3, 2, 1]
        Assert.Equal(6, boomerang.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 2, 1 }, boomerang);
    }

    [Fact]
    public void Encode_CreatesValidGif89aFile()
    {
        // Generate 3 simple colored test frames
        var frames = new List<SKBitmap>();
        try
        {
            var colors = new[] { SKColors.Crimson, SKColors.DodgerBlue, SKColors.ForestGreen };
            foreach (var color in colors)
            {
                var bmp = new SKBitmap(100, 80, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(color);
                }
                frames.Add(bmp);
            }

            using var ms = new MemoryStream();
            GifEncoder.Encode(frames, frameDelayMs: 150, ms, loop: true);
            var gifBytes = ms.ToArray();

            // Check GIF89a header
            Assert.True(gifBytes.Length > 100);
            Assert.Equal((byte)'G', gifBytes[0]);
            Assert.Equal((byte)'I', gifBytes[1]);
            Assert.Equal((byte)'F', gifBytes[2]);
            Assert.Equal((byte)'8', gifBytes[3]);
            Assert.Equal((byte)'9', gifBytes[4]);
            Assert.Equal((byte)'a', gifBytes[5]);

            // Check GIF trailer
            Assert.Equal(0x3B, gifBytes[^1]);

            // Verify with SkiaSharp's SKCodec
            using var readStream = new SKMemoryStream(gifBytes);
            using var codec = SKCodec.Create(readStream);
            Assert.NotNull(codec);
            Assert.Equal(100, codec.Info.Width);
            Assert.Equal(80, codec.Info.Height);
            Assert.Equal(3, codec.FrameCount);
        }
        finally
        {
            foreach (var b in frames)
                b.Dispose();
        }
    }

    [Fact]
    public void EncodeFromJpegs_ProducesDecodableAnimatedGif()
    {
        var jpegList = new List<byte[]>();
        for (var i = 0; i < 4; i++)
        {
            using var bmp = new SKBitmap(120, 120);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(i % 2 == 0 ? SKColors.Gold : SKColors.DarkViolet);
            }
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
            jpegList.Add(data.ToArray());
        }

        var gifBytes = GifEncoder.EncodeFromJpegs(jpegList, frameDelayMs: 200, maxWidth: 100, maxHeight: 100, overlayText: "Lumio 2026");

        Assert.NotNull(gifBytes);
        Assert.True(gifBytes.Length > 200);

        using var readStream = new SKMemoryStream(gifBytes);
        using var codec = SKCodec.Create(readStream);
        Assert.NotNull(codec);
        Assert.Equal(4, codec.FrameCount);
    }

    [Fact]
    public void EncodeBoomerang_ProducesPingPongAnimatedGif()
    {
        // 5 dummy live view frames
        var liveFrames = new List<Lumio.Core.Camera.LiveViewFrame>();
        for (var i = 0; i < 5; i++)
        {
            var data = new byte[80 * 60 * 4];
            Array.Fill(data, (byte)(i * 40));
            liveFrames.Add(new Lumio.Core.Camera.LiveViewFrame(
                data,
                Lumio.Core.Camera.FrameFormat.Bgra32,
                80,
                60,
                80 * 4));
        }

        var boomerangBytes = GifEncoder.EncodeBoomerang(liveFrames, frameDelayMs: 90, maxWidth: 80, maxHeight: 60, overlayText: "Boomerang Test");
        Assert.NotNull(boomerangBytes);

        using var readStream = new SKMemoryStream(boomerangBytes);
        using var codec = SKCodec.Create(readStream);
        Assert.NotNull(codec);
        // 5 forward frames bounced ping-pong = [0,1,2,3,4,3,2,1] = 8 frames!
        Assert.Equal(8, codec.FrameCount);
    }
}
