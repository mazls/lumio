using System;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Views;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;
using Log = Android.Util.Log;

namespace Lumio.App.Android;

/// <summary>
/// Plays a video message (MP4) in the gallery with Android's MediaPlayer, including sound, looped.
/// Hosted as a native view inside the Avalonia layout; a TextureView (unlike VideoView's SurfaceView) blends
/// correctly with the Avalonia surface and is scaled so the video keeps its aspect ratio.
/// </summary>
internal sealed class AndroidVideoView : NativeControlHost, IDisposable
{
    private const string LogTag = "LumioVideo";

    private readonly Context _context;
    private readonly string _path;
    private TextureView? _textureView;
    private MediaPlayer? _player;
    private Surface? _surface;

    private AndroidVideoView(Context context, string path)
    {
        _context = context;
        _path = path;
    }

    /// <summary>Android's MediaPlayer plays the MP4 files Lumio writes; Motion-JPEG AVI is left to the frame player.</summary>
    public static Control? Create(Context context, string path) =>
        path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? new AndroidVideoView(context, path) : null;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _textureView = new TextureView(_context) { SurfaceTextureListener = new SurfaceListener(this) };
        return new AndroidViewControlHandle(_textureView);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        ReleasePlayer();
        base.DestroyNativeControlCore(control);
    }

    private void StartPlayback(SurfaceTexture texture)
    {
        try
        {
            _surface = new Surface(texture);
            _player = new MediaPlayer();
            _player.SetSurface(_surface);
            _player.SetDataSource(_path);
            _player.Looping = true;
            _player.VideoSizeChanged += (_, e) => FitVideo(e.Width, e.Height);
            _player.Prepared += (_, _) => _player?.Start();
            _player.PrepareAsync();
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Video playback failed: {ex.Message}");
            ReleasePlayer();
        }
    }

    /// <summary>The TextureView stretches the video to its size: scale it back to fit (letterbox) instead.</summary>
    private void FitVideo(int videoWidth, int videoHeight)
    {
        var view = _textureView;
        if (view is null || videoWidth <= 0 || videoHeight <= 0 || view.Width <= 0 || view.Height <= 0)
            return;

        var viewAspect = (float)view.Width / view.Height;
        var videoAspect = (float)videoWidth / videoHeight;
        var scaleX = videoAspect < viewAspect ? videoAspect / viewAspect : 1f;
        var scaleY = videoAspect > viewAspect ? viewAspect / videoAspect : 1f;

        var matrix = new Matrix();
        matrix.SetScale(scaleX, scaleY, view.Width / 2f, view.Height / 2f);
        view.SetTransform(matrix);
    }

    private void ReleasePlayer()
    {
        try
        {
            _player?.Stop();
        }
        catch { }
        _player?.Release();
        _player = null;
        _surface?.Release();
        _surface = null;
    }

    public void Dispose() => ReleasePlayer();

    private sealed class SurfaceListener(AndroidVideoView owner) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) => owner.StartPlayback(surface);

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            owner.ReleasePlayer();
            return true;
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
        {
            if (owner._player is { } player)
                owner.FitVideo(player.VideoWidth, player.VideoHeight);
        }

        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }
}
