using Lumio.Core.Camera;
using Lumio.Core.Settings;

namespace Lumio.Camera;

/// <summary>
/// Nikon camera driver (backwards-compatible wrapper around <see cref="DslrCamera"/>).
/// Supports Nikon DSLRs (e.g. D5600) and Z-Mirrorless (e.g. Z 50 II) as well as other
/// vendors through the universal DSLR driver.
/// </summary>
public sealed class NikonCamera : DslrCamera
{
    public NikonCamera(BoothSettings? settings = null) : base(settings)
    {
    }

    /// <summary>Backwards-compatible discovery method.</summary>
    public static async Task<IReadOnlyList<CameraDeviceInfo>> FindConnectedNikonDevicesAsync()
    {
        return await FindConnectedDevicesAsync();
    }
}
