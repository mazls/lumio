using Lumio.Core.Camera;
using Lumio.Core.Settings;

namespace Lumio.Core.Storage;

/// <summary>
/// Decides where event files are stored:
/// <c>{OutputRoot}/{yyyy-MM-dd}_{EventName}/Originale/IMG_....jpg</c>
/// The date is the day the event began (its first file), so a party past midnight or over a weekend stays
/// in one folder until a new event is started.
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

    public string GetEventFolder() => Path.Combine(_settings.Current.OutputRoot, GetEventFolderName());

    /// <summary>Folder name of the current event; before its first file it carries today's date.</summary>
    public string GetEventFolderName()
    {
        var settings = _settings.Current;
        var date = IsValidDate(settings.EventStartDate) ? settings.EventStartDate! : Today;
        return $"{date}_{SanitizeFileName(settings.EventName)}";
    }

    private string Today => _time.GetLocalNow().ToString("yyyy-MM-dd");

    private static bool IsValidDate(string? date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);

    /// <summary>The first saved file fixes the event's date (and with it the folder) until a new event is started.</summary>
    private void EnsureEventStarted()
    {
        if (IsValidDate(_settings.Current.EventStartDate))
            return;

        try
        {
            var copy = _settings.Current.Clone();
            copy.EventStartDate = Today;
            _settings.Update(copy, notify: false);
        }
        catch
        {
            // Settings not writable: the folder simply follows today's date
        }
    }

    public static string GetDefaultInternalRoot() => BoothSettings.DefaultOutputRoot;

    /// <summary>
    /// Raised after a photo / GIF / video was written (also when a guest's look replaced a collage), so the
    /// platform can announce it (Android: media scanner, then it shows up in Google Photos and the file manager).
    /// </summary>
    public static event Action<string>? FileSaved;

    public static void NotifyFileSaved(string path) => FileSaved?.Invoke(path);

    /// <summary>Raised after a gallery item was deleted (Android: removes it from the media library as well).</summary>
    public static event Action<string>? FileDeleted;

    /// <summary>Deletes a photo / GIF / video of the gallery (a video together with its poster image).</summary>
    public static void DeleteMedia(string path)
    {
        var poster = FindPoster(path);
        File.Delete(path);
        FileDeleted?.Invoke(path);
        if (poster is not null && !string.Equals(poster, path, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(poster); } catch { }
        }
    }

    /// <summary>
    /// Still image of a video message for the gallery. Hidden (leading dot), so phone gallery apps do not list it
    /// as an extra photo.
    /// </summary>
    public static string GetPosterPath(string videoPath) =>
        Path.Combine(Path.GetDirectoryName(videoPath) ?? string.Empty, "." + Path.GetFileNameWithoutExtension(videoPath) + ".jpg");

    /// <summary>The poster of a video message if there is one (also the visible name used by version 1.3.4).</summary>
    public static string? FindPoster(string videoPath)
    {
        var hidden = GetPosterPath(videoPath);
        if (File.Exists(hidden))
            return hidden;
        var legacy = Path.ChangeExtension(videoPath, ".jpg");
        return File.Exists(legacy) ? legacy : null;
    }

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
            var fallbackEvent = Path.Combine(GetDefaultInternalRoot(), GetEventFolderName());
            var fallbackFolder = Path.Combine(fallbackEvent, subFolderName);
            Directory.CreateDirectory(fallbackFolder);
            return fallbackFolder;
        }
    }

    public async Task<string> SaveVideoAsync(byte[] videoData, DateTimeOffset timestamp, string extension = "mp4", CancellationToken cancellationToken = default)
    {
        EnsureEventStarted();
        ArgumentNullException.ThrowIfNull(videoData);

        var folder = GetVideosFolder();
        Directory.CreateDirectory(folder);

        var ext = extension.TrimStart('.');
        var baseName = $"VIDEO_{timestamp.ToLocalTime():yyyyMMdd_HHmmss}";
        var path = Path.Combine(folder, $"{baseName}.{ext}");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName}_{i}.{ext}");

        await File.WriteAllBytesAsync(path, videoData, cancellationToken).ConfigureAwait(false);
        NotifyFileSaved(path);
        return path;
    }

    /// <param name="posterJpeg">Optional still saved next to the video (same name, .jpg) for the gallery.</param>
    public async Task<string> MoveVideoAsync(string sourcePath, DateTimeOffset timestamp, string extension = "mp4", byte[]? posterJpeg = null, CancellationToken cancellationToken = default)
    {
        EnsureEventStarted();
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
            if (posterJpeg is not null)
                File.WriteAllBytes(GetPosterPath(targetPath), posterJpeg);
        }, cancellationToken).ConfigureAwait(false);

        NotifyFileSaved(targetPath);

        return targetPath;
    }

    public async Task<string> SaveGifAsync(byte[] gifData, DateTimeOffset timestamp, string prefix = "GIF", CancellationToken cancellationToken = default)
    {
        EnsureEventStarted();
        ArgumentNullException.ThrowIfNull(gifData);

        var folder = GetGifsFolder();
        Directory.CreateDirectory(folder);

        var baseName = $"{prefix}_{timestamp.ToLocalTime():yyyyMMdd_HHmmss_fff}";
        var path = Path.Combine(folder, baseName + ".gif");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName}_{i}.gif");

        await File.WriteAllBytesAsync(path, gifData, cancellationToken).ConfigureAwait(false);
        NotifyFileSaved(path);
        return path;
    }

    public async Task<string> SaveCollageAsync(byte[] jpegData, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        EnsureEventStarted();
        ArgumentNullException.ThrowIfNull(jpegData);

        var folder = GetCollagesFolder();
        Directory.CreateDirectory(folder);

        var baseName = $"PRINT_{timestamp.ToLocalTime():yyyyMMdd_HHmmss_fff}";
        var path = Path.Combine(folder, baseName + ".jpg");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName}_{i}.jpg");

        await File.WriteAllBytesAsync(path, jpegData, cancellationToken).ConfigureAwait(false);
        NotifyFileSaved(path);
        return path;
    }

    public async Task<string> SaveOriginalAsync(CapturedPhoto photo, CancellationToken cancellationToken = default)
    {
        EnsureEventStarted();
        ArgumentNullException.ThrowIfNull(photo);

        var folder = GetOriginalsFolder();
        Directory.CreateDirectory(folder);

        var baseName = $"IMG_{photo.CapturedAt.ToLocalTime():yyyyMMdd_HHmmss_fff}";
        var path = Path.Combine(folder, baseName + ".jpg");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName}_{i}.jpg");

        await File.WriteAllBytesAsync(path, photo.JpegData, cancellationToken).ConfigureAwait(false);
        NotifyFileSaved(path);
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
