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

    public string GetOriginalsFolder() => Path.Combine(GetEventFolder(), OriginalsFolderName);
    public string GetCollagesFolder() => Path.Combine(GetEventFolder(), CollagesFolderName);
    public string GetGifsFolder() => Path.Combine(GetEventFolder(), GifsFolderName);

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

        if (Directory.Exists(collagesDir))
            list.AddRange(Directory.EnumerateFiles(collagesDir, "*.jpg"));

        if (Directory.Exists(gifsDir))
            list.AddRange(Directory.EnumerateFiles(gifsDir, "*.gif"));

        if (Directory.Exists(originalsDir))
            list.AddRange(Directory.EnumerateFiles(originalsDir, "*.jpg"));

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
