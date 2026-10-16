using Lumio.Core.Camera;
using Lumio.Core.Session;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lumio.Tests;

public class BoothSettingsTests
{
    [Fact]
    public void Normalize_ClampsInvalidValues()
    {
        var settings = new BoothSettings
        {
            CountdownSeconds = -5,
            ReviewSeconds = 9999,
            EventName = "   ",
            AdminPin = "abc",
        };

        settings.Normalize();

        Assert.Equal(BoothSettings.MinCountdown, settings.CountdownSeconds);
        Assert.Equal(BoothSettings.MaxReview, settings.ReviewSeconds);
        Assert.Equal("Mein Event", settings.EventName);
        Assert.Equal("1234", settings.AdminPin);
    }

    [Fact]
    public void Normalize_KeepsValidValues()
    {
        var settings = new BoothSettings
        {
            CountdownSeconds = 5,
            ReviewSeconds = 15,
            EventName = "Sommerfest 2026",
            AdminPin = "9876",
        };

        settings.Normalize();

        Assert.Equal(5, settings.CountdownSeconds);
        Assert.Equal(15, settings.ReviewSeconds);
        Assert.Equal("Sommerfest 2026", settings.EventName);
        Assert.Equal("9876", settings.AdminPin);
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _tempFile;

    public SettingsStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"marcelsbooth_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        if (File.Exists(_tempFile + ".corrupt")) File.Delete(_tempFile + ".corrupt");
    }

    [Fact]
    public void SaveAndLoad_PersistsValuesCorrectly()
    {
        var store = new SettingsStore(_tempFile);
        var original = new BoothSettings
        {
            EventName = "Geburtstag Marcel",
            CountdownSeconds = 4,
            ReviewSeconds = 10,
            AdminPin = "4321",
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal("Geburtstag Marcel", loaded.EventName);
        Assert.Equal(4, loaded.CountdownSeconds);
        Assert.Equal(10, loaded.ReviewSeconds);
        Assert.Equal("4321", loaded.AdminPin);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(_tempFile, "{ this is not valid json }");
        var store = new SettingsStore(_tempFile);

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("Mein Event", loaded.EventName);
        Assert.True(File.Exists(_tempFile + ".corrupt"));
    }
}

public class EventStorageTests
{
    [Fact]
    public void SanitizeFileName_RemovesInvalidCharacters()
    {
        var result = EventStorage.SanitizeFileName("Hochzeit: Marcel & Anna? <Party!>");
        Assert.DoesNotContain(":", result);
        Assert.DoesNotContain("?", result);
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
    }
}

public class PhotoCaptureWorkflowTests
{
    private sealed class FakeCamera : ICameraService
    {
        public string DisplayName => "Fake Camera";
        public CameraStatus Status => CameraStatus.Connected;
        public event EventHandler<CameraStatus>? StatusChanged;
        public event EventHandler<LiveViewFrame>? FrameReady;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartLiveViewAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopLiveViewAsync() => Task.CompletedTask;

        public Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
        {
            var fakeJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
            return Task.FromResult(new CapturedPhoto(fakeJpeg, DateTimeOffset.UtcNow));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_CompletesCountdownAndSavesPhoto()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"marcelsbooth_event_{Guid.NewGuid():N}");
        try
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 20, 0, 0, TimeSpan.Zero));
            var store = new SettingsStore(Path.Combine(tempDir, "settings.json"));
            var settingsService = new SettingsService(store);
            settingsService.Update(new BoothSettings { OutputRoot = tempDir, EventName = "TestEvent" });

            var storage = new EventStorage(settingsService, time);
            var workflow = new PhotoCaptureWorkflow(storage, time);
            var camera = new FakeCamera();

            var stages = new List<CaptureStage>();
            var progress = new Progress<CaptureProgress>(p => stages.Add(p.Stage));

            var workflowTask = workflow.RunAsync(camera, countdownSeconds: 2, progress);

            for (var i = 0; i < 10 && !workflowTask.IsCompleted; i++)
            {
                await Task.Delay(20);
                time.Advance(TimeSpan.FromSeconds(1));
            }

            var result = await workflowTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(result);
            Assert.True(File.Exists(result.FilePath));
            Assert.Contains(CaptureStage.Countdown, stages);
            Assert.Contains(CaptureStage.Capturing, stages);
            Assert.Contains(CaptureStage.Saving, stages);
            Assert.Contains(CaptureStage.Completed, stages);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
