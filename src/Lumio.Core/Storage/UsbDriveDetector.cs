namespace Lumio.Core.Storage;

/// <summary>
/// Information about a detected USB drive or removable media storage.
/// </summary>
public sealed record UsbDriveInfo(
    string Name,
    string RootPath,
    double FreeSpaceGb,
    double TotalSizeGb,
    string DisplayText);

/// <summary>
/// Detects USB drives and removable media across Windows and Linux (Raspberry Pi OS).
/// </summary>
public static class UsbDriveDetector
{
    public static IReadOnlyList<UsbDriveInfo> DetectDrives()
    {
        var list = new List<UsbDriveInfo>();

        try
        {
            // Standard DriveInfo for Windows and Linux
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                var isRemovable = drive.DriveType == DriveType.Removable;
                var root = drive.RootDirectory.FullName;
                var isLinuxMedia = root.StartsWith("/media", StringComparison.OrdinalIgnoreCase)
                                   || root.StartsWith("/run/media", StringComparison.OrdinalIgnoreCase);

                if (isRemovable || isLinuxMedia)
                {
                    var label = !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.VolumeLabel
                        : drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                    var totalGb = drive.TotalSize / (1024.0 * 1024 * 1024);

                    list.Add(new UsbDriveInfo(
                        label,
                        root,
                        freeGb,
                        totalGb,
                        $"💾 {label} ({freeGb:F1} GB frei von {totalGb:F1} GB)"));
                }
            }
        }
        catch { }

        // Extra Linux scanning for /media and /run/media mounts (Raspberry Pi OS default auto-mount)
        if (OperatingSystem.IsLinux())
        {
            ScanLinuxMounts(list);
        }

        return list;
    }

    private static void ScanLinuxMounts(List<UsbDriveInfo> list)
    {
        var baseDirs = new[] { "/media", "/run/media", "/mnt" };
        foreach (var baseDir in baseDirs)
        {
            if (!Directory.Exists(baseDir))
                continue;

            try
            {
                var candidateDirs = new List<string>();
                // /media/<user>/<label>
                foreach (var sub in Directory.EnumerateDirectories(baseDir))
                {
                    candidateDirs.Add(sub);
                    try
                    {
                        foreach (var inner in Directory.EnumerateDirectories(sub))
                        {
                            candidateDirs.Add(inner);
                        }
                    }
                    catch { }
                }

                foreach (var dir in candidateDirs)
                {
                    if (list.Any(d => d.RootPath.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        var drive = new DriveInfo(dir);
                        var freeGb = drive.IsReady ? drive.AvailableFreeSpace / (1024.0 * 1024 * 1024) : 0;
                        var totalGb = drive.IsReady ? drive.TotalSize / (1024.0 * 1024 * 1024) : 0;

                        list.Add(new UsbDriveInfo(
                            dirInfo.Name,
                            dir,
                            freeGb,
                            totalGb,
                            $"💾 USB: {dirInfo.Name} ({freeGb:F1} GB frei)"));
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
