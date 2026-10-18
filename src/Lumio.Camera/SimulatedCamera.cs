using System.Diagnostics;
using Lumio.Core.Camera;
using SkiaSharp;

namespace Lumio.Camera;

/// <summary>
/// Generates an animated test image. Lets the whole booth run without any camera attached.
/// Uses a 3:2 aspect ratio like a DSLR.
/// </summary>
public sealed class SimulatedCamera : ICameraService
{
    private const int PreviewWidth = 960;
    private const int PreviewHeight = 640;
    private const int PhotoWidth = 3600;
    private const int PhotoHeight = 2400;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private CameraStatus _status;

    public string DisplayName => "Simulierte Kamera";

    public CameraStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            StatusChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<CameraStatus>? StatusChanged;

    public event EventHandler<LiveViewFrame>? FrameReady;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Status = CameraStatus.Connected;
        return Task.CompletedTask;
    }

    public Task StartLiveViewAsync(CancellationToken cancellationToken = default)
    {
        if (_loopTask is not null)
            return Task.CompletedTask;

        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loopTask = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopLiveViewAsync()
    {
        if (_loopTask is null)
            return;

        _loopCts!.Cancel();
        await _loopTask.ConfigureAwait(false);
        _loopCts.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    public async Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
    {
        // Mimic a small shutter delay.
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        var elapsed = _clock.Elapsed;

        var jpeg = await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(PhotoWidth, PhotoHeight));
            TestPattern.Draw(surface.Canvas, PhotoWidth, PhotoHeight, elapsed);
            using var image = surface.Snapshot();
            return JpegEncoder.Encode(image, 98);
        }, cancellationToken).ConfigureAwait(false);

        return new CapturedPhoto(jpeg, DateTimeOffset.Now);
    }

    public async ValueTask DisposeAsync()
    {
        await StopLiveViewAsync().ConfigureAwait(false);
        Status = CameraStatus.Disconnected;
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        using var surface = SKSurface.Create(new SKImageInfo(PreviewWidth, PreviewHeight));

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                TestPattern.Draw(surface.Canvas, PreviewWidth, PreviewHeight, _clock.Elapsed);
                surface.Canvas.Flush();
                using var image = surface.Snapshot();
                var jpeg = JpegEncoder.Encode(image, 70);
                FrameReady?.Invoke(this, new LiveViewFrame(jpeg, FrameFormat.Jpeg, PreviewWidth, PreviewHeight, 0));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}

internal static class TestPattern
{
    private static readonly SKColor Accent = new(255, 77, 141);
    private static readonly SKColor AccentWarm = new(255, 112, 90);

    public static void Draw(SKCanvas canvas, int width, int height, TimeSpan time)
    {
        var t = (float)time.TotalSeconds;
        PartyScene.DrawBackdrop(canvas, width, height, t, animated: true);

        DrawViewfinder(canvas, width, height, t);
        DrawApertureLogo(canvas, width / 2f, height * 0.44f, height * 0.13f, t);

        using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 140),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, height * 0.006f),
        };

        // "LUMIO" wordmark
        using var wordmark = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), height * 0.075f) { Subpixel = true, ScaleX = 1.08f };
        var wordY = height * 0.68f;
        canvas.DrawText("LUMIO", width / 2f, wordY + height * 0.004f, SKTextAlign.Center, wordmark, shadow);
        canvas.DrawText("LUMIO", width / 2f, wordY, SKTextAlign.Center, wordmark, text);

        // "TESTKAMERA" pill
        using var labelFont = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), height * 0.032f) { Subpixel = true };
        const string Label = "TESTKAMERA";
        var labelWidth = labelFont.MeasureText(Label);
        var pillHeight = height * 0.056f;
        var pill = SKRect.Create(width / 2f - labelWidth / 2f - pillHeight * 0.6f, height * 0.725f, labelWidth + pillHeight * 1.2f, pillHeight);
        using (var pillPaint = new SKPaint { IsAntialias = true })
        {
            pillPaint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(pill.Left, 0), new SKPoint(pill.Right, 0), [AccentWarm, Accent], null, SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(pill, pillHeight / 2f, pillHeight / 2f, pillPaint);
        }
        canvas.DrawText(Label, width / 2f, pill.MidY + labelFont.Size * 0.36f, SKTextAlign.Center, labelFont, text);

        // Live clock - shows that frames are really coming in
        using var clockFont = new SKFont(SKTypeface.Default, height * 0.036f) { Subpixel = true };
        using var clockPaint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 200) };
        canvas.DrawText(DateTime.Now.ToString("HH:mm:ss.f"), width / 2f, height * 0.86f, SKTextAlign.Center, clockFont, clockPaint);
    }

    /// <summary>Camera-like corner brackets plus a blinking record dot.</summary>
    private static void DrawViewfinder(SKCanvas canvas, int width, int height, float t)
    {
        var margin = height * 0.07f;
        var arm = height * 0.08f;
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(2f, height * 0.006f),
            StrokeCap = SKStrokeCap.Round,
            Color = new SKColor(255, 255, 255, 190),
        };

        float l = margin, r = width - margin, top = margin, b = height - margin;
        canvas.DrawLine(l, top, l + arm, top, stroke); canvas.DrawLine(l, top, l, top + arm, stroke);
        canvas.DrawLine(r, top, r - arm, top, stroke); canvas.DrawLine(r, top, r, top + arm, stroke);
        canvas.DrawLine(l, b, l + arm, b, stroke); canvas.DrawLine(l, b, l, b - arm, stroke);
        canvas.DrawLine(r, b, r - arm, b, stroke); canvas.DrawLine(r, b, r, b - arm, stroke);

        // Center focus marks
        var c = new SKPoint(width / 2f, height / 2f);
        var tick = height * 0.02f;
        stroke.Color = new SKColor(255, 255, 255, 90);
        stroke.StrokeWidth = MathF.Max(1f, height * 0.003f);
        canvas.DrawLine(c.X - tick, c.Y, c.X + tick, c.Y, stroke);
        canvas.DrawLine(c.X, c.Y - tick, c.X, c.Y + tick, stroke);

        // Blinking dot top-left
        if ((int)(t * 2) % 2 == 0)
        {
            using var dot = new SKPaint { IsAntialias = true, Color = Accent };
            canvas.DrawCircle(l + arm * 0.45f, top + arm * 0.45f, height * 0.012f, dot);
        }
    }

    /// <summary>Lumio's aperture mark: six blades in the brand gradient with a soft glow, slowly turning.</summary>
    private static void DrawApertureLogo(SKCanvas canvas, float cx, float cy, float r, float t)
    {
        using (var glow = new SKPaint
        {
            IsAntialias = true,
            Color = Accent.WithAlpha(110),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, r * 0.35f),
        })
        {
            canvas.DrawCircle(cx, cy, r * 1.05f, glow);
        }

        using var ringPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = r * 0.1f };
        ringPaint.Shader = SKShader.CreateSweepGradient(new SKPoint(cx, cy), [AccentWarm, Accent, new SKColor(150, 110, 255), AccentWarm], null);
        canvas.DrawCircle(cx, cy, r, ringPaint);

        using var dark = new SKPaint { IsAntialias = true, Color = new SKColor(18, 10, 28, 235) };
        canvas.DrawCircle(cx, cy, r * 0.94f, dark);

        // Six blades
        using var blade = new SKPaint { IsAntialias = true, IsDither = true };
        blade.Shader = SKShader.CreateLinearGradient(
            new SKPoint(cx - r, cy - r), new SKPoint(cx + r, cy + r), [AccentWarm, Accent], null, SKShaderTileMode.Clamp);
        var rotation = t * 12f;
        var inner = r * 0.28f;
        var outer = r * 0.78f;
        for (var i = 0; i < 6; i++)
        {
            var a0 = (rotation + i * 60f) * MathF.PI / 180f;
            var a1 = a0 + 60f * MathF.PI / 180f;
            using var builder = new SKPathBuilder();
            builder.MoveTo(cx + inner * MathF.Cos(a0), cy + inner * MathF.Sin(a0));
            builder.LineTo(cx + outer * MathF.Cos(a0 + 0.35f), cy + outer * MathF.Sin(a0 + 0.35f));
            builder.LineTo(cx + outer * MathF.Cos(a1 + 0.05f), cy + outer * MathF.Sin(a1 + 0.05f));
            builder.LineTo(cx + inner * MathF.Cos(a1), cy + inner * MathF.Sin(a1));
            builder.Close();
            using var path = builder.Detach();
            canvas.DrawPath(path, blade);
        }

        // Highlight in the middle
        using var core = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 40) };
        canvas.DrawCircle(cx, cy, inner * 0.9f, core);
    }
}
