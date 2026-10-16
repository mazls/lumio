using Lumio.Core.Camera;
using Lumio.Core.Settings;

namespace Lumio.Camera;

/// <summary>
/// Owns the active camera. Recreates it when the camera settings change and
/// falls back to the simulated camera if the configured one is unavailable.
/// </summary>
public sealed class CameraManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _activeKey;

    public ICameraService? Current { get; private set; }

    /// <summary>Set when the configured camera failed and the test camera is used instead.</summary>
    public string? FallbackReason { get; private set; }

    public async Task<ICameraService> GetCameraAsync(BoothSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = BuildKey(settings);
            if (Current is { Status: CameraStatus.Connected } && _activeKey == key)
            {
                await Current.ApplySettingsAsync(settings.CameraControl, cancellationToken).ConfigureAwait(false);
                return Current;
            }

            await DisposeCurrentAsync().ConfigureAwait(false);
            FallbackReason = null;

            ICameraService camera = Create(settings);
            try
            {
                await camera.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _activeKey = key;
                await camera.ApplySettingsAsync(settings.CameraControl, cancellationToken).ConfigureAwait(false);
            }
            catch (CameraException ex) when (settings.CameraBackend != CameraBackend.Simulated)
            {
                await camera.DisposeAsync().ConfigureAwait(false);
                FallbackReason = ex.Message;
                camera = new SimulatedCamera();
                await camera.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _activeKey = null; // retry the real camera next time
            }

            Current = camera;
            return camera;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ICameraService Create(BoothSettings settings) => settings.CameraBackend switch
    {
        CameraBackend.Webcam => new WebcamCamera(settings.WebcamDeviceId),
        CameraBackend.Nikon => new NikonCamera(),
        _ => new SimulatedCamera(),
    };

    private static string BuildKey(BoothSettings settings) =>
        settings.CameraBackend == CameraBackend.Webcam
            ? $"webcam:{settings.WebcamDeviceId}"
            : settings.CameraBackend.ToString();

    private async Task DisposeCurrentAsync()
    {
        if (Current is null)
            return;

        await Current.DisposeAsync().ConfigureAwait(false);
        Current = null;
        _activeKey = null;
    }
}
