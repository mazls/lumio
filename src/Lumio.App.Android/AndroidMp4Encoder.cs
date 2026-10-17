using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using Android.Media;
using Java.Nio;
using Log = Android.Util.Log;

namespace Lumio.App.Android;

/// <summary>
/// Turns the Motion-JPEG AVI (+ optional WAV) written by the VideoRecorder into an MP4 (H.264 + AAC) using the
/// Android hardware encoders, because ffmpeg is not available on Android. MP4 plays on every phone guests use.
/// </summary>
internal static class AndroidMp4Encoder
{
    private const string LogTag = "LumioMp4";
    private const int MaxWidth = 1280;
    private const int MaxHeight = 720;
    private const long TimeoutUs = 10_000;

    private sealed record Sample(byte[] Data, long PresentationTimeUs, MediaCodecBufferFlags Flags);

    public static Task<bool> EncodeAsync(string aviPath, string? wavPath, string mp4Path, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                Encode(aviPath, wavPath, mp4Path, cancellationToken);
                return File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0;
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"MP4 encoding failed: {ex}");
                try { File.Delete(mp4Path); } catch { }
                return false;
            }
        }, cancellationToken);

    private static void Encode(string aviPath, string? wavPath, string mp4Path, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (frameCount, aviFrameDurationUs) = ReadAviInfo(aviPath);
        if (frameCount == 0)
            throw new InvalidOperationException("Video contains no frames");

        var audio = wavPath is not null && File.Exists(wavPath) ? ReadWav(wavPath) : null;

        // Frames are captured at a variable rate: spread them over the real (audio) duration to stay in sync
        var frameDurationUs = audio is { DurationUs: > 0 } ? Math.Max(1, audio.DurationUs / frameCount) : aviFrameDurationUs;

        var (videoFormat, videoSamples) = EncodeVideo(aviPath, frameDurationUs, cancellationToken);
        MediaFormat? audioFormat = null;
        List<Sample>? audioSamples = null;
        if (audio is { Pcm.Length: > 0 })
            (audioFormat, audioSamples) = EncodeAudio(audio, cancellationToken);

        var muxer = new MediaMuxer(mp4Path, MuxerOutputType.Mpeg4);
        try
        {
            var videoTrack = muxer.AddTrack(videoFormat);
            var audioTrack = audioFormat is not null ? muxer.AddTrack(audioFormat) : -1;
            muxer.Start();

            // Interleave both tracks by timestamp
            var samples = videoSamples.Select(s => (Track: videoTrack, Sample: s));
            if (audioSamples is not null)
                samples = samples.Concat(audioSamples.Select(s => (Track: audioTrack, Sample: s)));

            var info = new MediaCodec.BufferInfo();
            foreach (var (track, sample) in samples.OrderBy(s => s.Sample.PresentationTimeUs))
            {
                info.Set(0, sample.Data.Length, sample.PresentationTimeUs, sample.Flags);
                muxer.WriteSampleData(track, ByteBuffer.Wrap(sample.Data), info);
            }

            muxer.Stop();
        }
        finally
        {
            muxer.Release();
        }

        Log.Info(LogTag, $"MP4 written: {frameCount} frames, audio={(audioSamples is not null)}, {stopwatch.ElapsedMilliseconds} ms");
    }

    #region AVI

    private static (int FrameCount, long FrameDurationUs) ReadAviInfo(string aviPath)
    {
        long frameDurationUs = 40_000;
        var count = 0;
        using var stream = File.OpenRead(aviPath);
        using var reader = new BinaryReader(stream);

        // avih: dwMicroSecPerFrame
        stream.Seek(12, SeekOrigin.Begin);
        var header = reader.ReadBytes(64);
        var avih = IndexOf(header, "avih"u8);
        if (avih >= 0 && avih + 12 <= header.Length)
            frameDurationUs = Math.Max(1, BitConverter.ToUInt32(header, avih + 8));

        foreach (var _ in EnumerateFrames(aviPath, readData: false))
            count++;
        return (count, frameDurationUs);
    }

    /// <summary>Streams the '00dc' JPEG chunks of the 'movi' list without loading the whole file.</summary>
    private static IEnumerable<byte[]> EnumerateFrames(string aviPath, bool readData = true)
    {
        using var stream = File.OpenRead(aviPath);
        using var reader = new BinaryReader(stream);

        // Find LIST 'movi'
        var start = FindMoviPayload(stream);
        if (start < 0) yield break;
        stream.Seek(start, SeekOrigin.Begin);

        while (stream.Position + 8 <= stream.Length)
        {
            var id = reader.ReadBytes(4);
            var size = reader.ReadUInt32();
            if (id.AsSpan().SequenceEqual("idx1"u8) || stream.Position + size > stream.Length)
                yield break;

            if (id.AsSpan().SequenceEqual("00dc"u8))
            {
                if (readData)
                    yield return reader.ReadBytes((int)size);
                else
                {
                    stream.Seek(size, SeekOrigin.Current);
                    yield return [];
                }
            }
            else
            {
                stream.Seek(size, SeekOrigin.Current);
            }

            if (size % 2 != 0)
                stream.Seek(1, SeekOrigin.Current);
        }
    }

    private static long FindMoviPayload(FileStream stream)
    {
        var buffer = new byte[64 * 1024];
        stream.Seek(0, SeekOrigin.Begin);
        var read = stream.Read(buffer, 0, buffer.Length);
        var index = IndexOf(buffer.AsSpan(0, read), "movi"u8);
        return index < 0 ? -1 : index + 4;
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value) => data.IndexOf(value);

    #endregion

    #region Video

    private static (MediaFormat Format, List<Sample> Samples) EncodeVideo(string aviPath, long frameDurationUs, CancellationToken cancellationToken)
    {
        int width = 0, height = 0;
        MediaCodec? codec = null;
        MediaFormat? outputFormat = null;
        var samples = new List<Sample>();
        var info = new MediaCodec.BufferInfo();
        long pts = 0;

        try
        {
            // Decoding + color conversion dominate: do them for several frames in parallel, encode in order
            foreach (var batch in EnumerateFrames(aviPath).Chunk(Environment.ProcessorCount))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (codec is null)
                {
                    using var first = BitmapFactory.DecodeByteArray(batch[0], 0, batch[0].Length)
                                      ?? throw new InvalidOperationException("First video frame is not decodable");
                    (width, height) = TargetSize(first.Width, first.Height);
                    codec = CreateVideoEncoder(width, height, frameDurationUs);
                }

                var frames = new I420Frame?[batch.Length];
                Parallel.For(0, batch.Length, i => frames[i] = DecodeToI420(batch[i], width, height));

                foreach (var frame in frames)
                {
                    if (frame is null) continue;

                    var inputIndex = DequeueInput(codec, info, samples, ref outputFormat);
                    using (var image = codec.GetInputImage(inputIndex) ?? throw new InvalidOperationException("Encoder has no input image"))
                    {
                        CopyToImage(image, frame, width, height);
                    }
                    codec.QueueInputBuffer(inputIndex, 0, width * height * 3 / 2, pts, 0);
                    pts += frameDurationUs;

                    Drain(codec, info, samples, ref outputFormat, endOfStream: false);
                }
            }

            if (codec is null)
                throw new InvalidOperationException("No decodable video frames");

            var eosIndex = DequeueInput(codec, info, samples, ref outputFormat);
            codec.QueueInputBuffer(eosIndex, 0, 0, pts, MediaCodecBufferFlags.EndOfStream);
            Drain(codec, info, samples, ref outputFormat, endOfStream: true);

            return (outputFormat ?? throw new InvalidOperationException("Encoder produced no format"), samples);
        }
        finally
        {
            try { codec?.Stop(); } catch { }
            codec?.Release();
        }
    }

    private sealed record I420Frame(byte[] Y, byte[] U, byte[] V);

    /// <summary>JPEG → scaled ARGB → planar YUV 4:2:0 (BT.601).</summary>
    private static I420Frame? DecodeToI420(byte[] jpeg, int width, int height)
    {
        using var decoded = BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length);
        if (decoded is null) return null;

        using var scaled = decoded.Width == width && decoded.Height == height
            ? null
            : Bitmap.CreateScaledBitmap(decoded, width, height, true);
        var argb = new int[width * height];
        (scaled ?? decoded).GetPixels(argb, 0, width, 0, 0, width, height);

        var y = new byte[width * height];
        var u = new byte[width * height / 4];
        var v = new byte[width * height / 4];
        var chromaWidth = width / 2;

        for (var row = 0; row < height; row++)
        {
            var src = row * width;
            var evenRow = (row & 1) == 0;
            var chromaRow = (row / 2) * chromaWidth;
            for (var x = 0; x < width; x++)
            {
                var c = argb[src + x];
                int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                y[src + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                if (evenRow && (x & 1) == 0)
                {
                    var ci = chromaRow + x / 2;
                    u[ci] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    v[ci] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        }

        return new I420Frame(y, u, v);
    }

    private static (int Width, int Height) TargetSize(int sourceWidth, int sourceHeight)
    {
        var scale = Math.Min(1.0, Math.Min((double)MaxWidth / sourceWidth, (double)MaxHeight / sourceHeight));
        // Multiples of 16 are accepted by every hardware encoder
        var width = Math.Max(16, (int)(sourceWidth * scale) / 16 * 16);
        var height = Math.Max(16, (int)(sourceHeight * scale) / 16 * 16);
        return (width, height);
    }

    private static MediaCodec CreateVideoEncoder(int width, int height, long frameDurationUs)
    {
        var fps = (int)Math.Clamp(1_000_000 / frameDurationUs, 5, 60);
        var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, width, height);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatyuv420flexible);
        format.SetInteger(MediaFormat.KeyBitRate, Math.Max(2_000_000, width * height * fps / 6));
        format.SetInteger(MediaFormat.KeyFrameRate, fps);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 1);

        var codec = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc);
        codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        codec.Start();
        return codec;
    }

    /// <summary>Copies a planar frame into the encoder's input image, honoring any plane layout/stride.</summary>
    private static void CopyToImage(Image image, I420Frame frame, int width, int height)
    {
        var planes = image.GetPlanes()!;
        for (var p = 0; p < 3; p++)
        {
            var plane = planes[p];
            var buffer = plane.Buffer!;
            var rowStride = plane.RowStride;
            var pixelStride = plane.PixelStride;
            var planeWidth = p == 0 ? width : width / 2;
            var planeHeight = p == 0 ? height : height / 2;
            var source = p switch { 0 => frame.Y, 1 => frame.U, _ => frame.V };

            buffer.Rewind();
            if (pixelStride == 1 && rowStride == planeWidth && buffer.Remaining() >= source.Length)
            {
                buffer.Put(source);
                continue;
            }

            var data = new byte[buffer.Remaining()];
            if (pixelStride > 1)
            {
                // Interleaved chroma shares memory with the other chroma plane: keep its bytes
                buffer.Get(data);
                buffer.Rewind();
            }

            for (var y = 0; y < planeHeight; y++)
            {
                var dst = y * rowStride;
                var src = y * planeWidth;
                if (pixelStride == 1)
                {
                    Array.Copy(source, src, data, dst, Math.Min(planeWidth, data.Length - dst));
                    continue;
                }
                for (var x = 0; x < planeWidth; x++)
                {
                    var index = dst + x * pixelStride;
                    if (index >= data.Length) break;
                    data[index] = source[src + x];
                }
            }

            buffer.Put(data);
        }
    }

    #endregion

    #region Audio

    private sealed record WavData(byte[] Pcm, int SampleRate, int Channels)
    {
        public long DurationUs => SampleRate > 0 && Channels > 0 ? Pcm.LongLength * 1_000_000 / (SampleRate * Channels * 2) : 0;
    }

    private static WavData? ReadWav(string wavPath)
    {
        var bytes = File.ReadAllBytes(wavPath);
        if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8))
            return null;

        int channels = 1, sampleRate = 48000;
        for (var pos = 12; pos + 8 <= bytes.Length;)
        {
            var size = (int)BitConverter.ToUInt32(bytes, pos + 4);
            if (bytes.AsSpan(pos, 4).SequenceEqual("fmt "u8))
            {
                channels = BitConverter.ToUInt16(bytes, pos + 10);
                sampleRate = (int)BitConverter.ToUInt32(bytes, pos + 12);
            }
            else if (bytes.AsSpan(pos, 4).SequenceEqual("data"u8))
            {
                var length = Math.Min(size, bytes.Length - pos - 8);
                return new WavData(bytes.AsSpan(pos + 8, length).ToArray(), sampleRate, channels);
            }
            pos += 8 + size + (size % 2);
        }
        return null;
    }

    private static (MediaFormat Format, List<Sample> Samples) EncodeAudio(WavData wav, CancellationToken cancellationToken)
    {
        var format = MediaFormat.CreateAudioFormat(MediaFormat.MimetypeAudioAac, wav.SampleRate, wav.Channels);
        format.SetInteger(MediaFormat.KeyAacProfile, (int)MediaCodecProfileType.Aacobjectlc);
        format.SetInteger(MediaFormat.KeyBitRate, 128_000);
        format.SetInteger(MediaFormat.KeyMaxInputSize, 16 * 1024);

        var codec = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeAudioAac);
        var samples = new List<Sample>();
        var info = new MediaCodec.BufferInfo();
        MediaFormat? outputFormat = null;
        var bytesPerSecond = wav.SampleRate * wav.Channels * 2;

        try
        {
            codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
            codec.Start();

            var offset = 0;
            while (offset < wav.Pcm.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var index = DequeueInput(codec, info, samples, ref outputFormat);
                var buffer = codec.GetInputBuffer(index)!;
                buffer.Clear();
                var chunk = Math.Min(Math.Min(buffer.Remaining(), 16 * 1024), wav.Pcm.Length - offset);
                chunk -= chunk % (wav.Channels * 2);
                buffer.Put(wav.Pcm, offset, chunk);

                var pts = (long)offset * 1_000_000 / bytesPerSecond;
                codec.QueueInputBuffer(index, 0, chunk, pts, 0);
                offset += chunk;

                Drain(codec, info, samples, ref outputFormat, endOfStream: false);
            }

            var eosIndex = DequeueInput(codec, info, samples, ref outputFormat);
            codec.QueueInputBuffer(eosIndex, 0, 0, (long)offset * 1_000_000 / bytesPerSecond, MediaCodecBufferFlags.EndOfStream);
            Drain(codec, info, samples, ref outputFormat, endOfStream: true);

            return (outputFormat ?? throw new InvalidOperationException("Audio encoder produced no format"), samples);
        }
        finally
        {
            try { codec.Stop(); } catch { }
            codec.Release();
        }
    }

    #endregion

    #region MediaCodec helpers

    private static int DequeueInput(MediaCodec codec, MediaCodec.BufferInfo info, List<Sample> samples, ref MediaFormat? outputFormat)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var index = codec.DequeueInputBuffer(TimeoutUs);
            if (index >= 0)
                return index;

            // Free input buffers by collecting finished output
            Drain(codec, info, samples, ref outputFormat, endOfStream: false);
        }
        throw new TimeoutException("Encoder accepts no input");
    }

    private static void Drain(MediaCodec codec, MediaCodec.BufferInfo info, List<Sample> samples, ref MediaFormat? outputFormat, bool endOfStream)
    {
        var idleRounds = 0;
        while (true)
        {
            var index = codec.DequeueOutputBuffer(info, endOfStream ? TimeoutUs : 0);
            if (index == (int)MediaCodecInfoState.TryAgainLater)
            {
                // At end of stream keep waiting for the EOS buffer, otherwise come back later
                if (!endOfStream || ++idleRounds > 500) return;
                continue;
            }
            if (index == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                outputFormat = codec.OutputFormat;
                continue;
            }
            if (index < 0)
                continue;

            if ((info.Flags & MediaCodecBufferFlags.CodecConfig) == 0 && info.Size > 0)
            {
                var buffer = codec.GetOutputBuffer(index)!;
                buffer.Position(info.Offset);
                buffer.Limit(info.Offset + info.Size);
                var data = new byte[info.Size];
                buffer.Get(data);
                samples.Add(new Sample(data, info.PresentationTimeUs, info.Flags & ~MediaCodecBufferFlags.EndOfStream));
            }

            codec.ReleaseOutputBuffer(index, false);

            if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                return;
        }
    }

    #endregion
}
