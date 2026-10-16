using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumio.Core.Settings;

/// <summary>Loads and saves <see cref="BoothSettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lumio",
            "settings.json");
    }

    public string FilePath { get; }

    public BoothSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            var oldPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MarcelsBooth",
                "settings.json");
            if (File.Exists(oldPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.Copy(oldPath, FilePath, overwrite: false);
                }
                catch
                {
                    // Ignore migration copy error
                }
            }
            else
            {
                return new BoothSettings();
            }
        }

        try
        {
            var settings = JsonSerializer.Deserialize<BoothSettings>(File.ReadAllText(FilePath), JsonOptions)
                           ?? new BoothSettings();
            settings.Normalize();
            return settings;
        }
        catch (JsonException)
        {
            // Keep the broken file for inspection and continue with defaults.
            File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
            return new BoothSettings();
        }
    }

    public void Save(BoothSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Write to a temp file first so a crash never leaves a half-written settings file.
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tempPath, FilePath, overwrite: true);
    }
}
