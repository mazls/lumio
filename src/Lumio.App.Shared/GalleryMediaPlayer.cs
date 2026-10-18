using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Lumio.Imaging;
using SkiaSharp;

namespace Lumio.App.Avalonia;

/// <summary>
/// Shows gallery items in an <see cref="Image"/>: photos, animated GIFs / boomerangs and video messages.
/// Videos are played by the platform player when one is registered (Android: MediaPlayer, with sound);
/// otherwise their frames are decoded here without sound (Motion-JPEG AVI directly, MP4 through ffmpeg).
/// </summary>
public sealed class GalleryMediaPlayer : IDisposable
{
    private const int MaxVideoWidth = 960;

    /// <summary>Creates a native, looping video view for a file, or null if the platform cannot play it (set by the Android app).</summary>
    public static Func<string, Control?>? PlatformVideoViewFactory { get; set; }

    private readonly Image _image;
    private readonly ContentControl _videoHost;
    private CancellationTokenSource? _playback;
    private List<Bitmap>? _ownedFrames;

    public GalleryMediaPlayer(Image image, ContentControl videoHost)
    {
        _image = image;
        _videoHost = videoHost;
    }

    public static bool IsGif(string path) => path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    public static bool IsVideo(string path) =>
        path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Poster image saved next to a video message (empty path if it has none).</summary>
    public static string PosterPath(string videoPath) => Lumio.Core.Storage.EventStorage.FindPoster(videoPath) ?? string.Empty;

    public void Show(string path)
    {
        Stop();

        if (IsGif(path))
        {
            var playback = _playback = new CancellationTokenSource();
            _ = Task.Run(() =>
            {
                var frames = DecodeGif(path);
                Dispatcher.UIThread.Post(() =>
                {
                    if (playback.IsCancellationRequested)
                    {
                        frames.ForEach(f => f.Frame.Dispose());
                        return;
                    }
                    _ownedFrames = frames.ConvertAll(f => f.Frame);
                    PlayFrames(_ => frames, ownsFrames: false, playback.Token);
                });
            });
            return;
        }

        if (IsVideo(path))
        {
            _image.Source = LoadStill(PosterPath(path));
            if (PlatformVideoViewFactory?.Invoke(path) is { } videoView)
            {
                _videoHost.Content = videoView;
                _videoHost.IsVisible = true;
                return;
            }

            _playback = new CancellationTokenSource();
            PlayFrames(
                token => path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase) ? AviFrames(path, token) : FfmpegFrames(path, token),
                ownsFrames: true,
                _playback.Token);
            return;
        }

        _image.Source = LoadStill(path);
    }

    public void Stop()
    {
        _playback?.Cancel();
        _playback = null;

        if (_videoHost.Content is IDisposable disposable)
            disposable.Dispose();
        _videoHost.Content = null;
        _videoHost.IsVisible = false;

        var shown = _image.Source;
        _image.Source = null;
        if (_ownedFrames is not null)
        {
            _ownedFrames.ForEach(f => f.Dispose());
            _ownedFrames = null;
        }
        else
        {
            (shown as IDisposable)?.Dispose();
        }
    }

    public void Dispose() => Stop();

    private static Bitmap? LoadStill(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Shows the frames of a source in a loop until cancelled.</summary>
    private void PlayFrames(Func<CancellationToken, IEnumerable<(Bitmap Frame, int DelayMs)>> source, bool ownsFrames, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var shownAny = false;
                    foreach (var (frame, delay) in source(token))
                    {
                        if (token.IsCancellationRequested)
                        {
                            if (ownsFrames) frame.Dispose();
                            return;
                        }

                        shownAny = true;
                        var started = Stopwatch.GetTimestamp();
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (token.IsCancellationRequested)
                            {
                                if (ownsFrames) frame.Dispose();
                                return;
                            }
                            var previous = _image.Source;
                            _image.Source = frame;
                            if (ownsFrames && !ReferenceEquals(previous, frame))
                                (previous as IDisposable)?.Dispose();
                        }, DispatcherPriority.Background);

                        var remaining = delay - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        await Task.Delay(Math.Max(10, remaining), token).ConfigureAwait(false);
                    }

                    if (!shownAny)
                        return; // nothing playable (e.g. no ffmpeg): the poster stays
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Gallery playback failed: {ex.Message}");
            }
        });
    }

    /// <summary>Decodes all frames of a GIF (Lumio's GIFs are max. 640 px wide, so this stays small).</summary>
    private static List<(Bitmap Frame, int DelayMs)> DecodeGif(string path)
    {
        var result = new List<(Bitmap, int)>();
        var decoded = new List<SKBitmap>();
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null)
                return result;

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var frameInfos = codec.FrameInfo;
            var count = Math.Max(1, codec.FrameCount);

            for (var i = 0; i < count; i++)
            {
                var required = i < frameInfos.Length ? frameInfos[i].RequiredFrame : -1;
                var canvas = new SKBitmap(info);
                if (required >= 0 && required < decoded.Count)
                    decoded[required].CopyTo(canvas);
                else
                    canvas.Erase(SKColors.Transparent);

                codec.GetPixels(info, canvas.GetPixels(), canvas.RowBytes, new SKCodecOptions(i, required));
                decoded.Add(canvas);

                var frame = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, canvas.GetPixels(),
                    new PixelSize(info.Width, info.Height), new Vector(96, 96), canvas.RowBytes);
                var delay = i < frameInfos.Length && frameInfos[i].Duration > 0 ? frameInfos[i].Duration : 100;
                result.Add((frame, delay));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"GIF decoding failed: {ex.Message}");
        }
        finally
        {
            decoded.ForEach(b => b.Dispose());
        }
        return result;
    }

    /// <summary>Frames of a Motion-JPEG AVI (Lumio's own video format when no MP4 encoder is available).</summary>
    private static IEnumerable<(Bitmap Frame, int DelayMs)> AviFrames(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 12 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
            yield break;
        reader.ReadUInt32();
        reader.ReadBytes(4); // "AVI "

        var delay = 40;
        while (stream.Position + 8 <= stream.Length && !token.IsCancellationRequested)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            if (id == "LIST")
            {
                reader.ReadBytes(4); // list type (hdrl, strl, movi): descend into it
                continue;
            }

            var next = stream.Position + size + (size & 1);
            if (id == "avih" && size >= 4)
            {
                var microSecondsPerFrame = reader.ReadUInt32();
                if (microSecondsPerFrame > 0)
                    delay = (int)Math.Clamp(microSecondsPerFrame / 1000, 10, 1000);
            }
            else if (id.EndsWith("dc", StringComparison.Ordinal) && size > 0 && next <= stream.Length)
            {
                var frame = DecodeJpeg(reader.ReadBytes((int)size));
                if (frame is not null)
                    yield return (frame, delay);
            }
            stream.Position = next;
        }
    }

    /// <summary>Frames of an MP4 through ffmpeg (desktop Linux / Raspberry Pi), as a Motion-JPEG pipe.</summary>
    private static IEnumerable<(Bitmap Frame, int DelayMs)> FfmpegFrames(string path, CancellationToken token)
    {
        const int Fps = 20;
        var ffmpeg = VideoRecorder.FindFfmpegExecutable();
        if (ffmpeg is null)
            yield break;

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "-v", "quiet", "-i", path, "-vf", $"scale='min({MaxVideoWidth},iw)':-2,fps={Fps}", "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "5", "-" })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
            yield break;

        try
        {
            foreach (var jpeg in SplitJpegStream(process.StandardOutput.BaseStream, token))
            {
                var frame = DecodeJpeg(jpeg);
                if (frame is not null)
                    yield return (frame, 1000 / Fps);
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch { }
        }
    }

    private static IEnumerable<byte[]> SplitJpegStream(Stream stream, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var frame = new MemoryStream();
        var inFrame = false;
        var previous = -1;
        int read;
        while (!token.IsCancellationRequested && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                int b = buffer[i];
                if (!inFrame)
                {
                    if (previous == 0xFF && b == 0xD8)
                    {
                        inFrame = true;
                        frame.SetLength(0);
                        frame.WriteByte(0xFF);
                        frame.WriteByte(0xD8);
                        b = -1;
                    }
                }
                else
                {
                    frame.WriteByte((byte)b);
                    if (previous == 0xFF && b == 0xD9)
                    {
                        inFrame = false;
                        yield return frame.ToArray();
                        b = -1;
                    }
                }
                previous = b;
            }
        }
    }

    private static Bitmap? DecodeJpeg(byte[] jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg);
            return Bitmap.DecodeToWidth(stream, MaxVideoWidth);
        }
        catch
        {
            return null;
        }
    }
}
