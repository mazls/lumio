using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Media;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Log = Android.Util.Log;

namespace Lumio.App.Android;

/// <summary>
/// Stores event photos in the shared "Pictures/Lumio" folder instead of the app's private storage, so they show
/// up in Google Photos and the file manager (and can be copied or deleted there). New files are announced to the
/// media scanner; photos of older Lumio versions are moved over once.
/// </summary>
internal static class AndroidPhotoStorage
{
    private const string LogTag = "LumioStorage";

    public static void Configure(Context context)
    {
        string? pictures;
        try
        {
#pragma warning disable CS0618, CA1422 // the path is only used for plain file access, which the app may do for its own media
            pictures = global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryPictures)?.AbsolutePath;
#pragma warning restore CS0618, CA1422
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Shared pictures folder unavailable: {ex.Message}");
            return;
        }
        if (string.IsNullOrEmpty(pictures))
            return;

        BoothSettings.PlatformPicturesFolder = pictures;
        var appContext = context.ApplicationContext ?? context;
        EventStorage.FileSaved += path => Scan(appContext, [path]);
        EventStorage.FileDeleted += path => Scan(appContext, [path]); // a scan of a missing file removes it from the library
        MovePrivatePhotos(appContext);
    }

    /// <summary>Moves the photos of older versions (private app storage) into the shared folder.</summary>
    private static void MovePrivatePhotos(Context context)
    {
        var source = BoothSettings.PrivateOutputRoot;
        var target = BoothSettings.DefaultOutputRoot;
        if (!Directory.Exists(source) || string.Equals(source, target, StringComparison.Ordinal))
            return;

        var moved = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (!File.Exists(destination))
                        File.Copy(file, destination);
                    File.Delete(file);
                    moved.Add(destination);
                }
                catch (Exception ex)
                {
                    // e.g. storage permission not granted yet (Android 10 and older): tried again at the next start
                    Log.Warn(LogTag, $"Could not move {file}: {ex.Message}");
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (Directory.GetFileSystemEntries(directory).Length == 0)
                        Directory.Delete(directory);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Moving photos failed: {ex.Message}");
        }

        if (moved.Count > 0)
        {
            Log.Info(LogTag, $"Moved {moved.Count} files to {target}");
            Scan(context, moved.ToArray());
        }
    }

    private static void Scan(Context context, string[] paths)
    {
        try
        {
            MediaScannerConnection.ScanFile(context, paths, null, null);
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Media scan failed: {ex.Message}");
        }
    }
}
