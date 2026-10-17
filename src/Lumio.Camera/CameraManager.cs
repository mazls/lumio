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

    public static Func<BoothSettings, ICameraService?>? CustomCameraFactory { get; set; }

    /// <summary>Optional platform-specific camera discovery hook (e.g. for Android tablet &amp; USB-OTG cameras).</summary>
    public static Func<Task<IReadOnlyList<CameraDeviceInfo>>>? CustomCameraDiscovery { get; set; }

    /// <summary>
    /// Fired when camera hardware changes. The argument is true when devices were plugged in or unplugged,
    /// so a different (preferred) camera may now be available.
    /// </summary>
    public static event Action<bool>? OnHardwareChanged;

    public static void NotifyHardwareChanged(bool devicesChanged = false) => OnHardwareChanged?.Invoke(devicesChanged);

    /// <summary>Forces the next <see cref="GetCameraAsync"/> to recreate the camera even if the settings did not change.</summary>
    public void Invalidate() => _activeKey = null;

    private static ICameraService Create(BoothSettings settings)
    {
        if (CustomCameraFactory?.Invoke(settings) is { } custom)
            return custom;

        return settings.CameraBackend switch
        {
            CameraBackend.Webcam => new WebcamCamera(settings.WebcamDeviceId),
            CameraBackend.Dslr or CameraBackend.Nikon => new DslrCamera(settings),
            _ => new SimulatedCamera(),
        };
    }

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
