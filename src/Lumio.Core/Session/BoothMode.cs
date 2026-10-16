namespace Lumio.Core.Session;

/// <summary>
/// Active capture mode chosen by the guest on the start screen.
/// </summary>
public enum BoothMode
{
    /// <summary>Single high-resolution photo.</summary>
    SinglePhoto,

    /// <summary>4-photo collage arranged in a 2x2 grid.</summary>
    CollageQuad,

    /// <summary>2x 3-photo double strip for the Canon Selphy.</summary>
    CollageStrip,

    /// <summary>4 sequential poses compiled into an animated GIF loop.</summary>
    Gif,

    /// <summary>Rapid burst sequence played forward and backward (ping-pong bounce).</summary>
    Boomerang,
}
