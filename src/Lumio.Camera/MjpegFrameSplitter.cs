namespace Lumio.Camera;

/// <summary>
/// Splits a continuous MJPEG byte stream (concatenated JPEGs, e.g. from
/// <c>gphoto2 --capture-movie --stdout</c>) into single JPEG frames.
/// </summary>
public sealed class MjpegFrameSplitter
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    private readonly List<byte> _buffer = new(512 * 1024);
    private bool _inFrame;
    private byte _previous;

    /// <summary>Feeds raw bytes and returns every JPEG frame that was completed by them.</summary>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data)
    {
        List<byte[]>? frames = null;

        foreach (var b in data)
        {
            if (!_inFrame)
            {
                // Start of image: FF D8
                if (_previous == 0xFF && b == 0xD8)
                {
                    _buffer.Clear();
                    _buffer.Add(0xFF);
                    _buffer.Add(0xD8);
                    _inFrame = true;
                }
            }
            else
            {
                _buffer.Add(b);

                // End of image: FF D9
                if (_previous == 0xFF && b == 0xD9)
                {
                    (frames ??= []).Add(_buffer.ToArray());
                    _buffer.Clear();
                    _inFrame = false;
                }
                else if (_buffer.Count > MaxFrameBytes)
                {
                    // Corrupt stream – resynchronise on the next SOI marker.
                    _buffer.Clear();
                    _inFrame = false;
                }
            }

            _previous = b;
        }

        return frames ?? (IReadOnlyList<byte[]>)[];
    }
}
