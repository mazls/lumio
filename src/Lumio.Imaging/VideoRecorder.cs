using System.Diagnostics;
using System.Runtime.InteropServices;
using Lumio.Core.Camera;
using SkiaSharp;

namespace Lumio.Imaging;

/// <summary>
/// Result of a video message recording session.
/// </summary>
public sealed record VideoRecordingResult(string VideoPath, byte[]? ThumbnailJpeg, TimeSpan Duration);

/// <summary>
/// High-performance cross-platform video recorder for video messages.
/// Writes live view frames into a standard Motion-JPEG AVI stream in real-time,
/// captures microphone audio when available, and automatically transcodes to MP4 via ffmpeg if present.
/// </summary>
public sealed class VideoRecorder : IDisposable
{
    private readonly ICameraService _camera;
    private readonly string _tempDirectory;
    private readonly int _targetFps;
    private readonly object _lock = new();

    private FileStream? _aviStream;
    private BinaryWriter? _writer;
    private string? _tempAviPath;
    private string? _tempWavPath;
    private Process? _linuxArecordProc;
    private bool _isRecording;
    private DateTime _recordStartTime;
    private DateTime _lastFrameTime = DateTime.MinValue;
    private int _frameCount;
    private int _videoWidth = 1280;
    private int _videoHeight = 720;
    private byte[]? _firstFrameJpeg;
    private readonly List<(uint Offset, uint Length)> _frameIndex = new();

    private IDisposable? _customAudioCapture;

    /// <summary>
    /// Optional platform audio recorder (e.g. Android AudioRecord). Receives the WAV path to write;
    /// disposing the returned handle stops recording and finalizes the file.
    /// </summary>
    public static Func<string, IDisposable?>? CustomAudioCapture { get; set; }

    /// <summary>
    /// Optional platform MP4 encoder used instead of ffmpeg (e.g. Android MediaCodec).
    /// Arguments: Motion-JPEG AVI path, WAV path (may be missing), target MP4 path. Returns true on success.
    /// </summary>
    public static Func<string, string?, string, CancellationToken, Task<bool>>? CustomMp4Encoder { get; set; }

    public VideoRecorder(ICameraService camera, int targetFps = 25, string? tempDirectory = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _targetFps = Math.Clamp(targetFps, 10, 30);
        _tempDirectory = tempDirectory ?? Path.GetTempPath();
    }

    public bool IsRecording => _isRecording;

    /// <summary>
    /// Starts recording video frames from the camera and audio from the default microphone.
    /// </summary>
    public void StartRecording()
    {
        lock (_lock)
        {
            if (_isRecording)
                return;

            var id = Guid.NewGuid().ToString("N");
            _tempAviPath = Path.Combine(_tempDirectory, $"lumio_vid_{id}.avi");
            _tempWavPath = Path.Combine(_tempDirectory, $"lumio_aud_{id}.wav");

            _aviStream = new FileStream(_tempAviPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            _writer = new BinaryWriter(_aviStream);
            _frameCount = 0;
            _frameIndex.Clear();
            _firstFrameJpeg = null;
            _recordStartTime = DateTime.UtcNow;
            _lastFrameTime = DateTime.MinValue;

            // Reserve header space (will be filled in StopRecording)
            WriteAviHeaderPlaceholders(_writer);

            // Start audio recording
            StartAudioCapture(_tempWavPath);

            _camera.FrameReady += OnCameraFrameReady;
            _isRecording = true;
        }
    }

    private void OnCameraFrameReady(object? sender, LiveViewFrame frame)
    {
        if (!_isRecording || _writer is null || _aviStream is null)
            return;

        var now = DateTime.UtcNow;
        var minIntervalMs = 1000.0 / _targetFps;

        lock (_lock)
        {
            if (!_isRecording || _writer is null || _aviStream is null)
                return;

            if ((now - _lastFrameTime).TotalMilliseconds < minIntervalMs)
                return;

            _lastFrameTime = now;

            byte[] jpegBytes;
            if (frame.Format == FrameFormat.Jpeg)
            {
                jpegBytes = frame.Data;
            }
            else
            {
                using var bmp = GifEncoder.FrameToBitmap(frame);
                if (bmp is null) return;
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
                jpegBytes = data.ToArray();
            }

            if (_frameCount == 0)
            {
                _videoWidth = frame.Width > 0 ? frame.Width : 1280;
                _videoHeight = frame.Height > 0 ? frame.Height : 720;
                _firstFrameJpeg = jpegBytes;
            }

            // Write '00dc' video chunk
            var chunkOffset = (uint)(_aviStream.Position - _moviListPayloadStart);
            var length = (uint)jpegBytes.Length;

            _writer.Write(new char[] { '0', '0', 'd', 'c' });
            _writer.Write(length);
            _writer.Write(jpegBytes);

            // Pad to word boundary
            if (length % 2 != 0)
            {
                _writer.Write((byte)0);
            }

            _frameIndex.Add((chunkOffset, length));
            _frameCount++;
        }
    }

    /// <summary>
    /// Stops recording and finalizes the video file.
    /// If ffmpeg is present, transcodes to MP4 with audio.
    /// </summary>
    public async Task<VideoRecordingResult> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        string aviPath;
        string? wavPath;
        int frameCount;
        int width;
        int height;
        byte[]? thumbnail;
        var duration = TimeSpan.Zero;

        lock (_lock)
        {
            if (!_isRecording)
                throw new InvalidOperationException("Aufnahme ist nicht aktiv.");

            _camera.FrameReady -= OnCameraFrameReady;
            _isRecording = false;

            duration = DateTime.UtcNow - _recordStartTime;
            aviPath = _tempAviPath!;
            wavPath = _tempWavPath;
            frameCount = _frameCount;
            width = _videoWidth;
            height = _videoHeight;
            thumbnail = _firstFrameJpeg;

            // Stop audio
            StopAudioCapture(wavPath);

            // Finalize AVI file
            if (_writer is not null && _aviStream is not null)
            {
                FinalizeAviFile(_writer, _aviStream, frameCount, width, height, _targetFps);
                _writer.Flush();
                _writer.Dispose();
                _aviStream.Dispose();
                _writer = null;
                _aviStream = null;
            }
        }

        // If no frames were captured (e.g. simulated camera without live frames), create a fallback frame
        if (frameCount == 0 && thumbnail is not null)
        {
            // At least one thumbnail was present or capture photo fallback
        }

        // Check if ffmpeg is available to encode pristine MP4 (H.264 + AAC)
        var finalVideoPath = aviPath;
        var targetMp4Path = Path.ChangeExtension(aviPath, ".mp4");

        var ffmpegConverted = CustomMp4Encoder is not null
            ? await CustomMp4Encoder(aviPath, wavPath, targetMp4Path, cancellationToken).ConfigureAwait(false)
            : await TryTranscodeToMp4Async(aviPath, wavPath, targetMp4Path, cancellationToken).ConfigureAwait(false);
        if (ffmpegConverted && File.Exists(targetMp4Path))
        {
            finalVideoPath = targetMp4Path;
            try { File.Delete(aviPath); } catch { }
            if (wavPath is not null)
            {
                try { File.Delete(wavPath); } catch { }
            }
        }

        return new VideoRecordingResult(finalVideoPath, thumbnail, duration);
    }

    #region AVI File Format (RIFF / Motion-JPEG)

    private long _moviListPayloadStart;
    private long _riffSizePos;
    private long _avihFramesPos;
    private long _strhLengthPos;
    private long _moviSizePos;

    private void WriteAviHeaderPlaceholders(BinaryWriter writer)
    {
        // 1. RIFF 'AVI '
        writer.Write(new char[] { 'R', 'I', 'F', 'F' });
        _riffSizePos = writer.BaseStream.Position;
        writer.Write((uint)0); // placeholder for RIFF size
        writer.Write(new char[] { 'A', 'V', 'I', ' ' });

        // 2. LIST 'hdrl'
        writer.Write(new char[] { 'L', 'I', 'S', 'T' });
        var hdrlSizePos = writer.BaseStream.Position;
        writer.Write((uint)0); // placeholder
        writer.Write(new char[] { 'h', 'd', 'r', 'l' });

        // avih chunk
        writer.Write(new char[] { 'a', 'v', 'i', 'h' });
        writer.Write((uint)56); // avih size
        var microSecPerFrame = (uint)(1_000_000 / _targetFps);
        writer.Write(microSecPerFrame); // dwMicroSecPerFrame
        writer.Write((uint)0);          // dwMaxBytesPerSec
        writer.Write((uint)0);          // dwPaddingGranularity
        writer.Write((uint)0x810);      // dwFlags: AVIF_HASINDEX | AVIF_TRUSTCKTYPE
        _avihFramesPos = writer.BaseStream.Position;
        writer.Write((uint)0);          // dwTotalFrames (placeholder)
        writer.Write((uint)0);          // dwInitialFrames
        writer.Write((uint)1);          // dwStreams (video only in raw AVI)
        writer.Write((uint)(1024 * 1024)); // dwSuggestedBufferSize
        writer.Write((uint)_videoWidth);
        writer.Write((uint)_videoHeight);
        writer.Write(new byte[16]);     // reserved[4]

        // LIST 'strl'
        writer.Write(new char[] { 'L', 'I', 'S', 'T' });
        var strlSizePos = writer.BaseStream.Position;
        writer.Write((uint)0); // placeholder
        writer.Write(new char[] { 's', 't', 'r', 'l' });

        // strh chunk
        writer.Write(new char[] { 's', 't', 'r', 'h' });
        writer.Write((uint)56);
        writer.Write(new char[] { 'v', 'i', 'd', 's' });
        writer.Write(new char[] { 'M', 'J', 'P', 'G' });
        writer.Write((uint)0);          // dwFlags
        writer.Write((ushort)0);        // wPriority
        writer.Write((ushort)0);        // wLanguage
        writer.Write((uint)0);          // dwInitialFrames
        writer.Write((uint)1);          // dwScale
        writer.Write((uint)_targetFps); // dwRate
        writer.Write((uint)0);          // dwStart
        _strhLengthPos = writer.BaseStream.Position;
        writer.Write((uint)0);          // dwLength (placeholder)
        writer.Write((uint)(1024 * 1024)); // dwSuggestedBufferSize
        writer.Write((uint)10000);      // dwQuality
        writer.Write((uint)0);          // dwSampleSize
        writer.Write((short)0); writer.Write((short)0); // rcFrame left, top
        writer.Write((short)_videoWidth); writer.Write((short)_videoHeight); // rcFrame right, bottom

        // strf chunk (BITMAPINFOHEADER)
        writer.Write(new char[] { 's', 't', 'r', 'f' });
        writer.Write((uint)40); // biSize
        writer.Write((uint)40);
        writer.Write((int)_videoWidth);
        writer.Write((int)_videoHeight);
        writer.Write((short)1);  // biPlanes
        writer.Write((short)24); // biBitCount
        writer.Write(new char[] { 'M', 'J', 'P', 'G' });
        writer.Write((uint)(_videoWidth * _videoHeight * 3)); // biSizeImage
        writer.Write((int)0); writer.Write((int)0);
        writer.Write((uint)0); writer.Write((uint)0);

        // Update strl and hdrl sizes
        var afterStrlPos = writer.BaseStream.Position;
        writer.BaseStream.Seek(strlSizePos, SeekOrigin.Begin);
        writer.Write((uint)(afterStrlPos - strlSizePos - 4));

        writer.BaseStream.Seek(hdrlSizePos, SeekOrigin.Begin);
        writer.Write((uint)(afterStrlPos - hdrlSizePos - 4));

        writer.BaseStream.Seek(afterStrlPos, SeekOrigin.Begin);

        // 3. LIST 'movi'
        writer.Write(new char[] { 'L', 'I', 'S', 'T' });
        _moviSizePos = writer.BaseStream.Position;
        writer.Write((uint)0); // placeholder
        writer.Write(new char[] { 'm', 'o', 'v', 'i' });

        _moviListPayloadStart = writer.BaseStream.Position;
    }

    private void FinalizeAviFile(BinaryWriter writer, FileStream stream, int frameCount, int width, int height, int fps)
    {
        var moviEndPos = stream.Position;
        var moviSize = (uint)(moviEndPos - _moviSizePos - 4);

        // 4. idx1 chunk
        writer.Write(new char[] { 'i', 'd', 'x', '1' });
        writer.Write((uint)(_frameIndex.Count * 16));

        foreach (var (offset, length) in _frameIndex)
        {
            writer.Write(new char[] { '0', '0', 'd', 'c' });
            writer.Write((uint)0x10); // AVIIF_KEYFRAME
            writer.Write(offset);
            writer.Write(length);
        }

        var totalFileLength = stream.Position;

        // Patch RIFF size
        stream.Seek(_riffSizePos, SeekOrigin.Begin);
        writer.Write((uint)(totalFileLength - 8));

        // Patch avih
        stream.Seek(_avihFramesPos, SeekOrigin.Begin);
        writer.Write((uint)frameCount);

        // Patch width and height in avih
        stream.Seek(_avihFramesPos + 12, SeekOrigin.Begin);
        writer.Write((uint)width);
        writer.Write((uint)height);

        // Patch strh length
        stream.Seek(_strhLengthPos, SeekOrigin.Begin);
        writer.Write((uint)frameCount);

        // Patch rcFrame right and bottom in strh
        stream.Seek(_strhLengthPos + 16, SeekOrigin.Begin);
        writer.Write((short)width);
        writer.Write((short)height);

        // Patch strf width and height
        stream.Seek(_strhLengthPos + 28, SeekOrigin.Begin);
        writer.Write((int)width);
        writer.Write((int)height);

        // Patch movi size
        stream.Seek(_moviSizePos, SeekOrigin.Begin);
        writer.Write(moviSize);
    }

    #endregion

    #region Audio Capture (platform hook, Windows MCI & Linux ALSA)

    [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi)]
    private static extern int MciSendString(string command, System.Text.StringBuilder? returnBuffer, int bufferSize, IntPtr hwndCallback);

    private void StartAudioCapture(string wavPath)
    {
        try
        {
            if (CustomAudioCapture is not null)
            {
                _customAudioCapture = CustomAudioCapture(wavPath);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                MciSendString("close lumio_rec", null, 0, IntPtr.Zero);
                var err = MciSendString("open new type waveaudio alias lumio_rec", null, 0, IntPtr.Zero);
                if (err == 0)
                {
                    MciSendString("set lumio_rec time format ms bitspersample 16 channels 1 samplespersec 44100", null, 0, IntPtr.Zero);
                    MciSendString("record lumio_rec", null, 0, IntPtr.Zero);
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Linux / Raspberry Pi: spawn arecord
                var psi = new ProcessStartInfo
                {
                    FileName = "arecord",
                    Arguments = $"-q -f cd -t wav \"{wavPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                _linuxArecordProc = Process.Start(psi);
            }
        }
        catch
        {
            // Audio recording is optional; do not fail video recording if mic is absent
        }
    }

    private void StopAudioCapture(string? wavPath)
    {
        try
        {
            if (_customAudioCapture is not null)
            {
                _customAudioCapture.Dispose();
                _customAudioCapture = null;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && wavPath is not null)
            {
                MciSendString("stop lumio_rec", null, 0, IntPtr.Zero);
                MciSendString($"save lumio_rec \"{wavPath}\"", null, 0, IntPtr.Zero);
                MciSendString("close lumio_rec", null, 0, IntPtr.Zero);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (_linuxArecordProc is not null && !_linuxArecordProc.HasExited)
                {
                    _linuxArecordProc.Kill();
                    _linuxArecordProc.Dispose();
                    _linuxArecordProc = null;
                }
            }
        }
        catch
        {
            // Ignore audio stopping errors
        }
    }

    #endregion

    #region FFMPEG Transcoding

    private static async Task<bool> TryTranscodeToMp4Async(string aviPath, string? wavPath, string mp4Path, CancellationToken cancellationToken)
    {
        try
        {
            var ffmpegBin = FindFfmpegExecutable();
            if (string.IsNullOrEmpty(ffmpegBin))
                return false;

            var hasAudio = wavPath is not null && File.Exists(wavPath) && new FileInfo(wavPath).Length > 1000;

            string args;
            if (hasAudio)
            {
                args = $"-y -i \"{aviPath}\" -i \"{wavPath}\" -c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 128k -shortest -pix_fmt yuv420p \"{mp4Path}\"";
            }
            else
            {
                args = $"-y -i \"{aviPath}\" -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p \"{mp4Path}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegBin,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return proc.ExitCode == 0 && File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindFfmpegExecutable()
    {
        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "ffmpeg.exe", "ffmpeg" }
            : new[] { "ffmpeg", "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" };

        foreach (var name in names)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    proc.WaitForExit(1000);
                    if (proc.ExitCode == 0) return name;
                }
            }
            catch
            {
                // Not found or cannot execute
            }
        }

        return null;
    }

    #endregion

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isRecording)
            {
                _camera.FrameReady -= OnCameraFrameReady;
                _isRecording = false;
            }

            StopAudioCapture(_tempWavPath);

            _writer?.Dispose();
            _aviStream?.Dispose();
            _writer = null;
            _aviStream = null;
        }
    }
}
