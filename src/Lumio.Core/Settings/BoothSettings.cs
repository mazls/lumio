using Lumio.Core.Camera;

namespace Lumio.Core.Settings;

/// <summary>Which camera implementation the booth uses.</summary>
public enum CameraBackend
{
    /// <summary>Any USB/built-in webcam via Windows Media Foundation.</summary>
    Webcam,

    /// <summary>
    /// Legacy value (Lumio ≤ 1.1.8). Is migrated to <see cref="Dslr"/> when settings are loaded.
    /// </summary>
    Nikon,

    /// <summary>Generated test image – useful without any hardware.</summary>
    Simulated,

    /// <summary>
    /// DSLR / mirrorless camera of any vendor (Nikon, Canon, Sony, Fujifilm, Panasonic, Olympus, ...).
    /// Windows: via digiCamControl. Linux / Raspberry Pi: via gphoto2.
    /// </summary>
    Dslr,
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
    public const int MinVideoDuration = 5;
    public const int MaxVideoDuration = 120;

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

    /// <summary>Base URL of the digiCamControl web server (Windows DSLR bridge).</summary>
    public string DigiCamControlUrl { get; set; } = DefaultDigiCamControlUrl;

    public const string DefaultDigiCamControlUrl = "http://localhost:5513";

    /// <summary>Optional custom path to CameraControl.exe if not in standard installation folders.</summary>
    public string? DigiCamControlExePath { get; set; }

    /// <summary>Automatically launch digiCamControl in the background when Lumio starts.</summary>
    public bool AutoStartDigiCamControl { get; set; } = true;

    /// <summary>Shows Lumio's own touch keyboard for text fields in the admin menu.</summary>
    public bool OnScreenKeyboardEnabled { get; set; } = false;

    public string AdminPin { get; set; } = "1234";
    public string SelectedTemplateId { get; set; } = "single-10x15";
    public bool AutoPrint { get; set; } = false;
    public string? SelectedPrinter { get; set; }
    public int MaxPrintCopiesPerSession { get; set; } = 2;

    /// <summary>Maximum recording time for a video message in seconds.</summary>
    public int VideoMessageDurationSeconds { get; set; } = 20;

    /// <summary>Whether guests can choose the Video Message mode on the start screen.</summary>
    public bool EnableVideoMessages { get; set; } = true;

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
        VideoMessageDurationSeconds = Math.Clamp(VideoMessageDurationSeconds, MinVideoDuration, MaxVideoDuration);

        if (string.IsNullOrWhiteSpace(EventName))
            EventName = "Mein Event";

        if (string.IsNullOrWhiteSpace(OutputRoot))
            OutputRoot = new BoothSettings().OutputRoot;

        AdminPin = new string((AdminPin ?? string.Empty).Where(char.IsAsciiDigit).Take(8).ToArray());
        if (AdminPin.Length < 4)
            AdminPin = "1234";

        if (!Enum.IsDefined(CameraBackend))
            CameraBackend = CameraBackend.Webcam;

        // Lumio <= 1.1.8 only knew "Nikon" – it is now the generic DSLR backend.
        if (CameraBackend == CameraBackend.Nikon)
            CameraBackend = CameraBackend.Dslr;

        if (string.IsNullOrWhiteSpace(DigiCamControlUrl)
            || !Uri.TryCreate(DigiCamControlUrl.Trim(), UriKind.Absolute, out _))
            DigiCamControlUrl = DefaultDigiCamControlUrl;
        else
            DigiCamControlUrl = DigiCamControlUrl.Trim().TrimEnd('/');

        MaxPrintCopiesPerSession = Math.Clamp(MaxPrintCopiesPerSession, 1, 10);
        CameraControl.Normalize();
    }
}

/// <summary>
/// Exposure settings for DSLR / mirrorless cameras. Values are stored vendor-neutral
/// (e.g. ISO "200", shutter "1/125", aperture "5.6", white balance "Daylight", EV "+1");
/// the camera drivers translate them into the vocabulary of the respective camera.
/// </summary>
public sealed class DslrCameraSettings
{
    public string Iso { get; set; } = "Auto";
    public string ShutterSpeed { get; set; } = "1/125";
    public string Aperture { get; set; } = "5.6";
    public string WhiteBalance { get; set; } = "Auto";
    public string ExposureCompensation { get; set; } = "0";

    public DslrCameraSettings Clone() => (DslrCameraSettings)MemberwiseClone();

    /// <summary>Strips UI labels from older settings files (e.g. "f/5.6 (Empfohlen)" → "5.6").</summary>
    public void Normalize()
    {
        Iso = DslrOptions.NormalizeIso(Iso);
        ShutterSpeed = DslrOptions.NormalizeShutterSpeed(ShutterSpeed);
        Aperture = DslrOptions.NormalizeAperture(Aperture);
        WhiteBalance = DslrOptions.NormalizeWhiteBalance(WhiteBalance);
        ExposureCompensation = DslrOptions.NormalizeExposureCompensation(ExposureCompensation);
    }
}
