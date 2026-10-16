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
    private const int PreviewWidth = 1200;
    private const int PreviewHeight = 800;
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
            return JpegEncoder.Encode(image);
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
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        using var bitmap = new SKBitmap(new SKImageInfo(PreviewWidth, PreviewHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                TestPattern.Draw(canvas, PreviewWidth, PreviewHeight, _clock.Elapsed);
                canvas.Flush();
                FrameReady?.Invoke(this, new LiveViewFrame(
                    bitmap.Bytes, FrameFormat.Bgra32, PreviewWidth, PreviewHeight, bitmap.RowBytes));
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
        var hue = t * 18f % 360f;

        using (var background = new SKPaint())
        {
            background.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, height),
                [SKColor.FromHsl(hue, 70, 22), SKColor.FromHsl((hue + 70) % 360, 75, 38)],
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, width, height, background);
        }

        using (var bubble = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 34) })
        {
            for (var i = 0; i < 7; i++)
            {
                var cx = width * (0.5f + 0.38f * MathF.Sin(t * 0.55f + i * 1.3f));
                var cy = height * (0.5f + 0.32f * MathF.Cos(t * 0.42f + i * 1.9f));
                canvas.DrawCircle(cx, cy, height * (0.05f + 0.025f * i), bubble);
            }
        }

        DrawSmiley(canvas, width / 2f, height * 0.42f, height * 0.16f, t);

        using var textPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var titleFont = new SKFont(SKTypeface.Default, height * 0.06f) { Embolden = true };
        using var clockFont = new SKFont(SKTypeface.Default, height * 0.045f);
        canvas.DrawText("Lumio · Testkamera", width / 2f, height * 0.74f, SKTextAlign.Center, titleFont, textPaint);
        canvas.DrawText(DateTime.Now.ToString("HH:mm:ss.f"), width / 2f, height * 0.82f, SKTextAlign.Center, clockFont, textPaint);
    }

    private static void DrawSmiley(SKCanvas canvas, float cx, float cy, float r, float t)
    {
        using var face = new SKPaint { IsAntialias = true, Color = new SKColor(255, 214, 90) };
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
        canvas.DrawCircle(cx, cy + bob, r, face);
        canvas.DrawCircle(cx - r * 0.35f, cy - r * 0.2f + bob, r * 0.11f, ink);
        canvas.DrawCircle(cx + r * 0.35f, cy - r * 0.2f + bob, r * 0.11f, ink);
        canvas.DrawArc(new SKRect(cx - r * 0.5f, cy - r * 0.25f + bob, cx + r * 0.5f, cy + r * 0.55f + bob), 20, 140, false, mouth);
    }
}
