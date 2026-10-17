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
    public static void Draw(SKCanvas canvas, int width, int height, TimeSpan time)
    {
        var t = (float)time.TotalSeconds;
        var hue = t * 16f % 360f;

        // Smooth studio lighting backdrop: multi-stop radial gradient with dithering eliminates 8-bit banding completely
        using (var background = new SKPaint
        {
            IsAntialias = true,
            IsDither = true,
        })
        {
            var cInner = SKColor.FromHsl(hue, 68, 38);
            var cMid = SKColor.FromHsl((hue + 35) % 360, 70, 26);
            var cOuter = SKColor.FromHsl((hue + 70) % 360, 72, 16);

            background.Shader = SKShader.CreateRadialGradient(
                new SKPoint(width * 0.5f, height * 0.42f),
                MathF.Max(width, height) * 0.85f,
                [cInner, cMid, cOuter],
                [0.0f, 0.55f, 1.0f],
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, width, height, background);
        }

        // Soft, anti-aliased floating orbs / bokeh circles
        using (var bubble = new SKPaint { IsAntialias = true, IsDither = true, Color = new SKColor(255, 255, 255, 28) })
        {
            for (var i = 0; i < 7; i++)
            {
                var cx = width * (0.5f + 0.38f * MathF.Sin(t * 0.55f + i * 1.3f));
                var cy = height * (0.5f + 0.32f * MathF.Cos(t * 0.42f + i * 1.9f));
                canvas.DrawCircle(cx, cy, height * (0.05f + 0.025f * i), bubble);
            }
        }

        DrawSmiley(canvas, width / 2f, height * 0.42f, height * 0.16f, t);

        // Subpixel anti-aliased text with soft drop shadow for crisp readability
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            IsDither = true,
            Color = SKColors.White,
        };
        using var shadowPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 150),
        };

        using var titleFont = new SKFont(SKTypeface.Default, height * 0.06f) { Embolden = true, Subpixel = true };
        using var clockFont = new SKFont(SKTypeface.Default, height * 0.045f) { Subpixel = true };

        var titleY = height * 0.74f;
        var clockY = height * 0.82f;
        var shadowOff = height * 0.003f;

        canvas.DrawText("Lumio · Testkamera", width / 2f + shadowOff, titleY + shadowOff, SKTextAlign.Center, titleFont, shadowPaint);
        canvas.DrawText("Lumio · Testkamera", width / 2f, titleY, SKTextAlign.Center, titleFont, textPaint);

        var timeString = DateTime.Now.ToString("HH:mm:ss.f");
        canvas.DrawText(timeString, width / 2f + shadowOff, clockY + shadowOff, SKTextAlign.Center, clockFont, shadowPaint);
        canvas.DrawText(timeString, width / 2f, clockY, SKTextAlign.Center, clockFont, textPaint);
    }

    private static void DrawSmiley(SKCanvas canvas, float cx, float cy, float r, float t)
    {
        using var face = new SKPaint { IsAntialias = true, IsDither = true, Color = new SKColor(255, 214, 90) };
        using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 60) };
        using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(40, 30, 20) };
        using var mouth = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(40, 30, 20),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = r * 0.09f,
            StrokeCap = SKStrokeCap.Round,
        };

        var bob = MathF.Sin(t * 2f) * r * 0.05f;

        // Subtle drop shadow under face
        canvas.DrawCircle(cx, cy + bob + r * 0.04f, r, shadow);

        canvas.DrawCircle(cx, cy + bob, r, face);
        canvas.DrawCircle(cx - r * 0.35f, cy - r * 0.2f + bob, r * 0.11f, ink);
        canvas.DrawCircle(cx + r * 0.35f, cy - r * 0.2f + bob, r * 0.11f, ink);
        canvas.DrawArc(new SKRect(cx - r * 0.5f, cy - r * 0.25f + bob, cx + r * 0.5f, cy + r * 0.55f + bob), 20, 140, false, mouth);
    }
}
