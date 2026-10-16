namespace Lumio.Core.Camera;

public enum CameraStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

public enum FrameFormat
{
    /// <summary>Raw 32-bit BGRA/BGRX pixels (alpha is ignored).</summary>
    Bgra32,

    /// <summary>JPEG-compressed frame (typical for DSLR live view).</summary>
    Jpeg,
}

/// <summary>A single live-view frame.</summary>
public sealed record LiveViewFrame(byte[] Data, FrameFormat Format, int Width, int Height, int Stride);

/// <summary>A full-resolution photo taken by the camera.</summary>
public sealed record CapturedPhoto(byte[] JpegData, DateTimeOffset CapturedAt);

/// <summary>A camera that can be selected in the settings.</summary>
public sealed record CameraDeviceInfo(string Id, string Name);

/// <summary>
/// Abstraction over every camera type (webcam, simulated, Nikon, ...).
/// Events may be raised on any thread.
/// </summary>
public interface ICameraService : IAsyncDisposable
{
    string DisplayName { get; }

    CameraStatus Status { get; }

    event EventHandler<CameraStatus>? StatusChanged;

    event EventHandler<LiveViewFrame>? FrameReady;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task StartLiveViewAsync(CancellationToken cancellationToken = default);

    Task StopLiveViewAsync();

    Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default);

    Task ApplySettingsAsync(Lumio.Core.Settings.DslrCameraSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
