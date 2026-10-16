using Lumio.Core.Camera;
using SkiaSharp;

namespace Lumio.Camera;

internal static class JpegEncoder
{
    public static byte[] EncodeBgra(LiveViewFrame frame, int quality = 92)
    {
        if (frame.Format != FrameFormat.Bgra32)
            throw new ArgumentException("Frame must be BGRA32.", nameof(frame));

        // Opaque: many webcams deliver alpha = 0, which must be ignored.
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, frame.Data, frame.Stride);
        return Encode(image, quality);
    }

    public static byte[] Encode(SKImage image, int quality = 92)
    {
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    public static LiveViewFrame DecodeJpegToBgra(byte[] jpegBytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(jpegBytes))
                          ?? throw new InvalidOperationException("Could not decode JPEG live view frame.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        return new LiveViewFrame(bitmap.Bytes, FrameFormat.Bgra32, info.Width, info.Height, bitmap.RowBytes);
    }
}
