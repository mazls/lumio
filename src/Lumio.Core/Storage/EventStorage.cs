using Lumio.Core.Camera;
using Lumio.Core.Settings;

namespace Lumio.Core.Storage;

/// <summary>
/// Decides where event files are stored:
/// <c>{OutputRoot}/{yyyy-MM-dd}_{EventName}/Originale/IMG_....jpg</c>
/// </summary>
public sealed class EventStorage
{
    public const string OriginalsFolderName = "Originale";
    public const string CollagesFolderName = "Collagen";
    public const string GifsFolderName = "GIFs";
    public const string VideosFolderName = "Videos";

    private readonly SettingsService _settings;
    private readonly TimeProvider _time;

    public EventStorage(SettingsService settings, TimeProvider time)
    {
        _settings = settings;
        _time = time;
    }

    public string GetEventFolder()
    {
        var settings = _settings.Current;
        var date = _time.GetLocalNow().ToString("yyyy-MM-dd");
        return Path.Combine(settings.OutputRoot, $"{date}_{SanitizeFileName(settings.EventName)}");
    }

    public static string GetDefaultInternalRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Lumio");

    public string GetOriginalsFolder() => GetSafeFolder(OriginalsFolderName);
    public string GetCollagesFolder() => GetSafeFolder(CollagesFolderName);
    public string GetGifsFolder() => GetSafeFolder(GifsFolderName);
    public string GetVideosFolder() => GetSafeFolder(VideosFolderName);

    private string GetSafeFolder(string subFolderName)
    {
        var primaryFolder = Path.Combine(GetEventFolder(), subFolderName);
        try
        {
            Directory.CreateDirectory(primaryFolder);
            return primaryFolder;
        }
        catch
        {
            // Fail-safe: if USB drive is disconnected, unwritable or removed, write to internal drive
            var date = _time.GetLocalNow().ToString("yyyy-MM-dd");
            var fallbackEvent = Path.Combine(GetDefaultInternalRoot(), $"{date}_{SanitizeFileName(_settings.Current.EventName)}");
            var fallbackFolder = Path.Combine(fallbackEvent, subFolderName);
            Directory.CreateDirectory(fallbackFolder);
            return fallbackFolder;
        }
    }

    public async Task<string> SaveVideoAsync(byte[] videoData, DateTimeOffset timestamp, string extension = "mp4", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoData);

        var folder = GetVideosFolder();
        Directory.CreateDirectory(folder);

        var ext = extension.TrimStart('.');
        var baseName = $"VIDEO_{timestamp.ToLocalTime():yyyyMMdd_HHmmss}";
        var path = Path.Combine(folder, $"{baseName}.{ext}");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName}_{i}.{ext}");

        await File.WriteAllBytesAsync(path, videoData, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> MoveVideoAsync(string sourcePath, DateTimeOffset timestamp, string extension = "mp4", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var folder = GetVideosFolder();
        Directory.CreateDirectory(folder);

        var ext = extension.TrimStart('.');
        var baseName = $"VIDEO_{timestamp.ToLocalTime():yyyyMMdd_HHmmss}";
        var targetPath = Path.Combine(folder, $"{baseName}.{ext}");
        for (var i = 1; File.Exists(targetPath); i++)
            targetPath = Path.Combine(folder, $"{baseName}_{i}.{ext}");

        await Task.Run(() =>
        {
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(sourcePath, targetPath);
        }, cancellationToken).ConfigureAwait(false);

        return targetPath;
    }

    public async Task<string> SaveGifAsync(byte[] gifData, DateTimeOffset timestamp, string prefix = "GIF", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gifData);

        var folder = GetGifsFolder();
        Directory.CreateDirectory(folder);

        var baseName = $"{prefix}_{timestamp.ToLocalTime():yyyyMMdd_HHmmss_fff}";
        var path = Path.Combine(folder, baseName + ".gif");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName}_{i}.gif");

        await File.WriteAllBytesAsync(path, gifData, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> SaveCollageAsync(byte[] jpegData, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpegData);

        var folder = GetCollagesFolder();
        Directory.CreateDirectory(folder);

        var baseName = $"PRINT_{timestamp.ToLocalTime():yyyyMMdd_HHmmss_fff}";
        var path = Path.Combine(folder, baseName + ".jpg");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName}_{i}.jpg");

        await File.WriteAllBytesAsync(path, jpegData, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> SaveOriginalAsync(CapturedPhoto photo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var folder = GetOriginalsFolder();
        Directory.CreateDirectory(folder);

        var baseName = $"IMG_{photo.CapturedAt.ToLocalTime():yyyyMMdd_HHmmss_fff}";
        var path = Path.Combine(folder, baseName + ".jpg");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName}_{i}.jpg");

        await File.WriteAllBytesAsync(path, photo.JpegData, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public IReadOnlyList<string> GetEventPhotos()
    {
        var list = new List<string>();
        var collagesDir = GetCollagesFolder();
        var originalsDir = GetOriginalsFolder();
        var gifsDir = GetGifsFolder();
        var videosDir = GetVideosFolder();

        if (Directory.Exists(collagesDir))
            list.AddRange(Directory.EnumerateFiles(collagesDir, "*.jpg"));

        if (Directory.Exists(gifsDir))
            list.AddRange(Directory.EnumerateFiles(gifsDir, "*.gif"));

        if (Directory.Exists(originalsDir))
            list.AddRange(Directory.EnumerateFiles(originalsDir, "*.jpg"));

        if (Directory.Exists(videosDir))
        {
            list.AddRange(Directory.EnumerateFiles(videosDir, "*.mp4"));
            list.AddRange(Directory.EnumerateFiles(videosDir, "*.avi"));
        }

        return list
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    public string? GetLatestPhoto()
    {
        var photos = GetEventPhotos();
        return photos.Count > 0 ? photos[0] : null;
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Event" : cleaned;
    }
}
