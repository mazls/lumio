using System.IO;
using SkiaSharp;
using Xunit;

namespace Lumio.Tests;

public class IconGeneratorTests
{
    [Fact]
    public void GenerateMultiResolutionIcoFromLogo()
    {
        // Find repository root
        var current = Directory.GetCurrentDirectory();
        while (current != null && !File.Exists(Path.Combine(current, "Lumio.slnx")) && !File.Exists(Path.Combine(current, "README.md")))
        {
            var parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        Assert.NotNull(current);
        var logoPath = Path.Combine(current, "assets", "logo.png");
        Assert.True(File.Exists(logoPath), $"Logo not found at {logoPath}");

        using var srcStream = File.OpenRead(logoPath);
        using var srcBitmap = SKBitmap.Decode(srcStream);
        Assert.NotNull(srcBitmap);

        int[] sizes = [256, 128, 64, 48, 32, 16];
        var encodedImages = new List<(int size, byte[] data)>();

        foreach (var size in sizes)
        {
            using var resized = srcBitmap.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul), SKSamplingOptions.Default);
            Assert.NotNull(resized);
            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            encodedImages.Add((size, data.ToArray()));
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // ICO Header
        bw.Write((ushort)0); // Reserved
        bw.Write((ushort)1); // Type 1 = ICO
        bw.Write((ushort)encodedImages.Count); // Image count

        int currentOffset = 6 + (16 * encodedImages.Count);

        // Directory Entries
        foreach (var (size, bytes) in encodedImages)
        {
            byte w = (byte)(size >= 256 ? 0 : size);
            byte h = (byte)(size >= 256 ? 0 : size);
            bw.Write(w);
            bw.Write(h);
            bw.Write((byte)0); // Colors
            bw.Write((byte)0); // Reserved
            bw.Write((ushort)1); // Color planes
            bw.Write((ushort)32); // Bits per pixel
            bw.Write((uint)bytes.Length); // Size of image data
            bw.Write((uint)currentOffset); // Offset of image data
            currentOffset += bytes.Length;
        }

        // Image Data
        foreach (var (_, bytes) in encodedImages)
        {
            bw.Write(bytes);
        }

        bw.Flush();
        var icoBytes = ms.ToArray();
        Assert.True(icoBytes.Length > 1000);

        // Save to targets
        var targetPaths = new[]
        {
            Path.Combine(current, "assets", "app.ico"),
            Path.Combine(current, "src", "Lumio.App", "Assets", "app.ico"),
            Path.Combine(current, "src", "Lumio.App.Avalonia", "Assets", "app.ico")
        };

        foreach (var path in targetPaths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, icoBytes);
            Assert.True(File.Exists(path));
        }
    }
}
