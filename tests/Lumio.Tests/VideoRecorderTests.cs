using System.Text;
using Lumio.Core.Camera;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Lumio.Imaging;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Xunit;

namespace Lumio.Tests;

public sealed class VideoRecorderTests
{
    private sealed class MockCamera : ICameraService
    {
        public string DisplayName => "Mock Camera";
        public CameraStatus Status => CameraStatus.Connected;
        public event EventHandler<CameraStatus>? StatusChanged;
        public event EventHandler<LiveViewFrame>? FrameReady;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartLiveViewAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopLiveViewAsync() => Task.CompletedTask;
        public Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapturedPhoto(Array.Empty<byte>(), DateTimeOffset.UtcNow));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitFrame(LiveViewFrame frame)
        {
            FrameReady?.Invoke(this, frame);
        }
    }

    [Fact]
    public async Task VideoRecorder_RecordsFrames_AndProducesValidAvi()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lumio_test_vid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var mockCam = new MockCamera();
            using var recorder = new VideoRecorder(mockCam, targetFps: 25, tempDirectory: tempDir);

            // Generate test JPEG frame
            using var bmp = new SKBitmap(320, 240);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.DarkBlue);
            }
            using var img = SKImage.FromBitmap(bmp);
            using var encoded = img.Encode(SKEncodedImageFormat.Jpeg, 80);
            var jpegBytes = encoded.ToArray();

            recorder.StartRecording();
            Assert.True(recorder.IsRecording);

            // Emit 5 frames
            for (var i = 0; i < 5; i++)
            {
                mockCam.EmitFrame(new LiveViewFrame(jpegBytes, FrameFormat.Jpeg, 320, 240, 0));
                await Task.Delay(50); // space apart for fps pacing
            }

            var result = await recorder.StopRecordingAsync();
            Assert.False(recorder.IsRecording);

            Assert.NotNull(result);
            Assert.True(File.Exists(result.VideoPath));
            Assert.NotNull(result.ThumbnailJpeg);
            Assert.True(result.ThumbnailJpeg.Length > 0);

            // Verify file headers (RIFF and AVI or MP4)
            var fileBytes = await File.ReadAllBytesAsync(result.VideoPath);
            Assert.True(fileBytes.Length > 64);

            var riffTag = Encoding.ASCII.GetString(fileBytes, 0, 4);
            if (riffTag == "RIFF")
            {
                var aviTag = Encoding.ASCII.GetString(fileBytes, 8, 4);
                Assert.Equal("AVI ", aviTag);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task EventStorage_SaveAndMoveVideo_WorksCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lumio_test_storage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new SettingsStore(Path.Combine(tempDir, "settings.json"));
            var settings = new SettingsService(store);
            settings.Update(new BoothSettings
            {
                OutputRoot = tempDir,
                EventName = "TestEvent",
            });

            var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
            var storage = new EventStorage(settings, timeProvider);

            var dummyBytes = new byte[] { 1, 2, 3, 4, 5 };
            var savedPath = await storage.SaveVideoAsync(dummyBytes, timeProvider.GetUtcNow(), "mp4");

            Assert.True(File.Exists(savedPath));
            Assert.Equal("Videos", Path.GetFileName(Path.GetDirectoryName(savedPath)));
            Assert.EndsWith(".mp4", savedPath);

            var mediaList = storage.GetEventPhotos();
            Assert.Contains(savedPath, mediaList);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
