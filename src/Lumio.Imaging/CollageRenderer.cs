using SkiaSharp;

namespace Lumio.Imaging;

public sealed class CollageRenderer
{
    public byte[] Render(
        BoothTemplate template,
        IReadOnlyList<byte[]> photoJpegs,
        Dictionary<string, string>? textVariables = null,
        int jpegQuality = 95)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(photoJpegs);

        var info = new SKImageInfo(template.Width, template.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        // 1. Draw Background
        if (!string.IsNullOrWhiteSpace(template.BackgroundImagePath) && File.Exists(template.BackgroundImagePath))
        {
            using var bgBitmap = SKBitmap.Decode(template.BackgroundImagePath);
            if (bgBitmap != null)
            {
                var destRect = new SKRect(0, 0, template.Width, template.Height);
                canvas.DrawBitmap(bgBitmap, destRect, new SKSamplingOptions(SKFilterMode.Linear), null);
            }
        }
        else
        {
            var bgColor = SKColor.TryParse(template.BackgroundColorHex, out var parsedBg) ? parsedBg : SKColors.White;
            canvas.Clear(bgColor);
        }

        // 2. Draw Photos in Slots
        var slotCount = template.Slots.Count;
        for (var i = 0; i < slotCount; i++)
        {
            var slot = template.Slots[i];
            var photoData = i < photoJpegs.Count ? photoJpegs[i] : (photoJpegs.Count > 0 ? photoJpegs[0] : null);
            if (photoData is null) continue;

            using var photoBitmap = SKBitmap.Decode(photoData);
            if (photoBitmap is null) continue;

            DrawPhotoInSlot(canvas, photoBitmap, slot);
        }

        // 3. Draw Overlay Image (PNG with transparency)
        if (!string.IsNullOrWhiteSpace(template.OverlayImagePath) && File.Exists(template.OverlayImagePath))
        {
            using var overlayBitmap = SKBitmap.Decode(template.OverlayImagePath);
            if (overlayBitmap != null)
            {
                var destRect = new SKRect(0, 0, template.Width, template.Height);
                canvas.DrawBitmap(overlayBitmap, destRect, new SKSamplingOptions(SKFilterMode.Linear), null);
            }
        }

        // 4. Draw Dynamic Text Elements
        foreach (var textItem in template.Texts)
        {
            var rawText = textItem.Text;
            if (textVariables != null)
            {
                foreach (var (k, v) in textVariables)
                {
                    rawText = rawText.Replace($"{{{k}}}", v, StringComparison.OrdinalIgnoreCase);
                }
            }

            var textColor = SKColor.TryParse(textItem.ColorHex, out var parsedColor) ? parsedColor : SKColors.White;
            using var textPaint = new SKPaint
            {
                IsAntialias = true,
                Color = textColor
            };

            var textAlign = textItem.Alignment.ToLowerInvariant() switch
            {
                "left" => SKTextAlign.Left,
                "right" => SKTextAlign.Right,
                _ => SKTextAlign.Center
            };

            using var typeface = SKTypeface.FromFamilyName(textItem.FontName, textItem.Bold ? SKFontStyle.Bold : SKFontStyle.Normal);
            using var font = new SKFont(typeface, textItem.FontSize);

            canvas.DrawText(rawText, textItem.X, textItem.Y, textAlign, font, textPaint);
        }

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
        return encoded.ToArray();
    }

    private static void DrawPhotoInSlot(SKCanvas canvas, SKBitmap photo, TemplateSlot slot)
    {
        canvas.Save();

        var slotRect = new SKRect(slot.X, slot.Y, slot.X + slot.Width, slot.Y + slot.Height);

        if (slot.RotationDegrees != 0)
        {
            canvas.RotateDegrees(slot.RotationDegrees, slotRect.MidX, slotRect.MidY);
        }

        // Rounded corners clip
        if (slot.CornerRadius > 0)
        {
            using var clipPath = new SKPath();
            clipPath.AddRoundRect(slotRect, slot.CornerRadius, slot.CornerRadius);
            canvas.ClipPath(clipPath, antialias: true);
        }
        else
        {
            canvas.ClipRect(slotRect, antialias: true);
        }

        // Aspect Fill calculation: crop source to match slot aspect ratio
        var slotAspect = slot.Width / slot.Height;
        var photoAspect = (float)photo.Width / photo.Height;

        SKRect sourceRect;
        if (photoAspect > slotAspect)
        {
            // Photo is wider than slot: crop left & right
            var targetWidth = photo.Height * slotAspect;
            var left = (photo.Width - targetWidth) / 2f;
            sourceRect = new SKRect(left, 0, left + targetWidth, photo.Height);
        }
        else
        {
            // Photo is taller than slot: crop top & bottom
            var targetHeight = photo.Width / slotAspect;
            var top = (photo.Height - targetHeight) / 2f;
            sourceRect = new SKRect(0, top, photo.Width, top + targetHeight);
        }

        using var photoImage = SKImage.FromBitmap(photo);
        canvas.DrawImage(photoImage, sourceRect, slotRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), null);

        // Optional Border
        if (slot.BorderWidth > 0)
        {
            var borderColor = SKColor.TryParse(slot.BorderColorHex, out var parsedBorder) ? parsedBorder : SKColors.White;
            using var borderPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = slot.BorderWidth,
                Color = borderColor
            };

            if (slot.CornerRadius > 0)
                canvas.DrawRoundRect(slotRect, slot.CornerRadius, slot.CornerRadius, borderPaint);
            else
                canvas.DrawRect(slotRect, borderPaint);
        }

        canvas.Restore();
    }
}
