using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lumio.App.Services;

/// <summary>
/// High-performance native WPF animated GIF player using WPF's built-in BitmapDecoder.
/// Ensures zero-latency playback without third-party dependencies or visual tree race conditions.
/// </summary>
public sealed class GifPlayer : IDisposable
{
    private readonly Image _targetImage;
    private readonly DispatcherTimer _timer;
    private List<BitmapFrame> _frames = [];
    private int _currentIndex;

    public GifPlayer(Image targetImage)
    {
        _targetImage = targetImage;
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += OnTick;
    }

    public bool IsPlaying => _timer.IsEnabled;

    public void Play(string filePath)
    {
        Stop();

        if (!File.Exists(filePath))
            return;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            _frames = decoder.Frames.ToList();
            if (_frames.Count == 0)
                return;

            // Extract delay time from GIF metadata or default to 100ms
            var delayMs = 100;
            if (_frames[0].Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/grctlext/Delay"))
            {
                try
                {
                    var delayVal = Convert.ToInt32(metadata.GetQuery("/grctlext/Delay"));
                    if (delayVal > 0)
                        delayMs = delayVal * 10;
                }
                catch
                {
                    delayMs = 100;
                }
            }

            _currentIndex = 0;
            _targetImage.Source = _frames[0];

            if (_frames.Count > 1)
            {
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30, delayMs));
                _timer.Start();
            }
        }
        catch
        {
            // Fallback: try static image load
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
        _timer.Stop();
        _frames.Clear();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_frames.Count <= 1)
            return;

        _currentIndex = (_currentIndex + 1) % _frames.Count;
        _targetImage.Source = _frames[_currentIndex];
    }

    public void Dispose()
    {
        Stop();
    }
}
