namespace Lumio.Core.Camera;

/// <summary>A camera problem with a user-readable (German) message.</summary>
public sealed class CameraException : Exception
{
    public CameraException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
