using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using XamlAnimatedGif;

namespace Lumio.App.Services;

/// <summary>
/// Professional WPF animated GIF player powered by XamlAnimatedGif.
/// Provides smooth, hardware-accelerated playback with full GIF89a standard support.
/// </summary>
public sealed class GifPlayer : IDisposable
{
    private readonly Image _targetImage;

    private bool _isPlaying;

    public GifPlayer(Image targetImage)
    {
        _targetImage = targetImage;
    }

    public bool IsPlaying => _isPlaying;

    public void Play(string filePath)
    {
        Stop();

        if (!File.Exists(filePath))
            return;

        try
        {
            var uri = new Uri(filePath, UriKind.Absolute);
            AnimationBehavior.SetAutoStart(_targetImage, true);
            AnimationBehavior.SetRepeatBehavior(_targetImage, System.Windows.Media.Animation.RepeatBehavior.Forever);
            AnimationBehavior.SetSourceUri(_targetImage, uri);
            _isPlaying = true;
        }
        catch
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                _targetImage.Source = bmp;
            }
            catch
            {
                _targetImage.Source = null;
            }
        }
    }

    public void Stop()
    {
        _isPlaying = false;
        try
        {
            AnimationBehavior.SetSourceUri(_targetImage, null);
        }
        catch { }
        _targetImage.Source = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
