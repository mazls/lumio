using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace Lumio.Camera;

/// <summary>
/// Controls the digiCamControl process and its embedded HTTP web server.
/// Enables starting, stopping, and checking the status of digiCamControl directly from Lumio.
/// </summary>
public static class DigiCamControlManager
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public static string? FindExecutable(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var candidates = new List<string>
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "external", "digiCamControl", "CameraControl.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "digiCamControl", "CameraControl.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "external", "digiCamControl", "CameraControl.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "digiCamControl", "CameraControl.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "external", "digiCamControl", "CameraControl.exe"),
            @"D:\Projekte\MarcelsBooth\external\digiCamControl\CameraControl.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "digiCamControl", "CameraControl.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "digiCamControl", "CameraControl.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "digiCamControl", "CameraControl.exe"),
            @"C:\Program Files (x86)\digiCamControl\CameraControl.exe",
            @"C:\Program Files\digiCamControl\CameraControl.exe",
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Ensures that digiCamControl has its embedded webserver enabled on port 5513
    /// in %ProgramData%\digiCamControl\settings.xml before launching.
    /// </summary>
    public static void EnsureWebserverEnabled()
    {
        try
        {
            var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "digiCamControl");
            Directory.CreateDirectory(programData);
            var settingsFile = Path.Combine(programData, "settings.xml");

            if (File.Exists(settingsFile))
            {
                var content = File.ReadAllText(settingsFile);
                if (content.Contains("<UseWebserver>false</UseWebserver>"))
                {
                    content = content.Replace("<UseWebserver>false</UseWebserver>", "<UseWebserver>true</UseWebserver>");
                    File.WriteAllText(settingsFile, content);
                }
            }
            else
            {
                var minimal = """
                    <?xml version="1.0" encoding="utf-8"?>
                    <Settings xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                      <UseWebserver>true</UseWebserver>
                      <WebserverPort>5513</WebserverPort>
                      <AllowWebserverActions>true</AllowWebserverActions>
                    </Settings>
                    """;
                File.WriteAllText(settingsFile, minimal);
            }
        }
        catch { }
    }

    public static async Task<DigiCamStatus> GetStatusAsync(string url)
    {
        var procs = Process.GetProcessesByName("CameraControl");
        bool hasProc = procs.Length > 0;

        try
        {
            var res = await HttpClient.GetStringAsync($"{url.TrimEnd('/')}/?slc=get&param1=camera");
            var cam = res?.Trim() ?? "";
            bool hasCam = !string.IsNullOrWhiteSpace(cam) && !cam.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) && cam != "null";
            if (hasCam)
            {
                return new DigiCamStatus(true, true, $"Aktiv · Kamera: {cam}", true, cam, hasProc);
            }
            return new DigiCamStatus(true, false, "Aktiv (Webserver bereit, aber keine Kamera erkannt)", true, null, hasProc);
        }
        catch
        {
            if (hasProc)
            {
                return new DigiCamStatus(true, false, "digiCamControl startet... (Webserver noch nicht bereit)", false, null, true);
            }
            return new DigiCamStatus(false, false, "digiCamControl ist derzeit nicht aktiv", false, null, false);
        }
    }

    public static async Task<bool> StartAsync(string? exePath, string url, bool minimized = true)
    {
        var resolved = FindExecutable(exePath);
        if (resolved is null || !File.Exists(resolved))
        {
            return false;
        }

        // Make sure webserver is configured in digiCamControl settings
        EnsureWebserverEnabled();

        // If already running and webserver responds, we are good
        var current = await GetStatusAsync(url);
        if (current.IsReachable)
            return true;

        var procs = Process.GetProcessesByName("CameraControl");
        if (procs.Length == 0)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = resolved,
                Arguments = minimized ? "/minimized" : "",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(resolved)!,
            });
        }

        // Wait up to 12 seconds for webserver to come up
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(500);
            var status = await GetStatusAsync(url);
            if (status.IsReachable)
                return true;
        }

        return false;
    }

    public static async Task StopAsync(string url)
    {
        try
        {
            await HttpClient.GetStringAsync($"{url.TrimEnd('/')}/?slc=shutdown");
            await Task.Delay(600);
        }
        catch { }

        foreach (var p in Process.GetProcessesByName("CameraControl"))
        {
            try
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(1000))
                {
                    p.Kill();
                }
            }
            catch { }
        }
    }
}

public readonly record struct DigiCamStatus(
    bool IsRunning,
    bool IsConnected,
    string Message,
    bool IsReachable = false,
    string? ConnectedCamera = null,
    bool IsProcessRunning = false
);
