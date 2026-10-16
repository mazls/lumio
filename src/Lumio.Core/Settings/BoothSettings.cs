namespace Lumio.Core.Settings;

/// <summary>Which camera implementation the booth uses.</summary>
public enum CameraBackend
{
    /// <summary>Any USB/built-in webcam via Windows Media Foundation.</summary>
    Webcam,

    /// <summary>Nikon DSLR / Z-Mirrorless via USB (D5600, Z 50 II, etc.).</summary>
    Nikon,

    /// <summary>Generated test image – useful without any hardware.</summary>
    Simulated,
}

/// <summary>All user-configurable booth settings. Persisted as JSON.</summary>
public sealed class BoothSettings
{
    public const int MinCountdown = 1;
    public const int MaxCountdown = 10;
    public const int MinReview = 3;
    public const int MaxReview = 60;
    public const int MinMultiShotPause = 1;
    public const int MaxMultiShotPause = 10;

    public string EventName { get; set; } = "Mein Event";

    public string OutputRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Lumio");

    public int CountdownSeconds { get; set; } = 3;

    /// <summary>Pause between series shots in seconds.</summary>
    public int MultiShotPauseSeconds { get; set; } = 3;

    /// <summary>How long the result stays on screen before returning to the start screen.</summary>
    public int ReviewSeconds { get; set; } = 12;

    public CameraBackend CameraBackend { get; set; } = CameraBackend.Webcam;

    /// <summary>Windows device id of the selected webcam; <c>null</c> = first available.</summary>
    public string? WebcamDeviceId { get; set; }

    /// <summary>Mirror the live view like a mirror (the saved photo is never mirrored).</summary>
    public bool MirrorLiveView { get; set; } = true;

    public string AdminPin { get; set; } = "1234";
    public string SelectedTemplateId { get; set; } = "single-10x15";
    public bool AutoPrint { get; set; } = false;
    public string? SelectedPrinter { get; set; }
    public int MaxPrintCopiesPerSession { get; set; } = 2;

    /// <summary>Whether to embed the Lumio logo in a corner of captured photos.</summary>
    public bool EmbedLogoOnPhotos { get; set; } = true;

    /// <summary>Position of the embedded logo: "BottomRight", "BottomLeft", "TopRight", "TopLeft", "None".</summary>
    public string LogoPosition { get; set; } = "BottomRight";

    /// <summary>Whether to print/embed the event text on photos.</summary>
    public bool EmbedEventTextOnPhotos { get; set; } = true;

    /// <summary>Style format for the event text: "EventAndYear" (z.B. Event – 2026), "EventAndDate" (z.B. Event • 17.10.2026), "EventOnly", "None".</summary>
    public string EventTextFormat { get; set; } = "EventAndYear";

    public DslrCameraSettings CameraControl { get; set; } = new();

    public BoothSettings Clone()
    {
        var clone = (BoothSettings)MemberwiseClone();
        clone.CameraControl = CameraControl.Clone();
        return clone;
    }

    /// <summary>Clamps values into valid ranges and repairs empty fields.</summary>
    public void Normalize()
    {
        CameraControl ??= new();
        CountdownSeconds = Math.Clamp(CountdownSeconds, MinCountdown, MaxCountdown);
        MultiShotPauseSeconds = Math.Clamp(MultiShotPauseSeconds, MinMultiShotPause, MaxMultiShotPause);
        ReviewSeconds = Math.Clamp(ReviewSeconds, MinReview, MaxReview);

        if (string.IsNullOrWhiteSpace(EventName))
            EventName = "Mein Event";

        if (string.IsNullOrWhiteSpace(OutputRoot))
            OutputRoot = new BoothSettings().OutputRoot;

        AdminPin = new string((AdminPin ?? string.Empty).Where(char.IsAsciiDigit).Take(8).ToArray());
        if (AdminPin.Length < 4)
            AdminPin = "1234";

        if (!Enum.IsDefined(CameraBackend))
            CameraBackend = CameraBackend.Webcam;
    }
}

public sealed class DslrCameraSettings
{
    public string Iso { get; set; } = "Auto";
    public string ShutterSpeed { get; set; } = "1/125";
    public string Aperture { get; set; } = "f/5.6";
    public string WhiteBalance { get; set; } = "Auto";
    public string ExposureCompensation { get; set; } = "0";

    public DslrCameraSettings Clone() => (DslrCameraSettings)MemberwiseClone();
}
