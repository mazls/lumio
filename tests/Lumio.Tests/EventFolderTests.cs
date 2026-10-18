using Lumio.Core.Camera;
using Lumio.Core.Settings;
using Lumio.Core.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lumio.Tests;

public class EventFolderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lumio_eventfolder_{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 23, 30, 0, TimeSpan.Zero));
    private readonly SettingsService _settings;
    private readonly EventStorage _storage;

    public EventFolderTests()
    {
        _settings = new SettingsService(new SettingsStore(Path.Combine(_tempDir, "settings.json")));
        _settings.Update(new BoothSettings { OutputRoot = _tempDir, EventName = "Hochzeit" });
        _storage = new EventStorage(_settings, _time);
    }

    private static CapturedPhoto Photo() => new([0xFF, 0xD8, 0xFF, 0xE0], DateTimeOffset.UtcNow);

    [Fact]
    public async Task PartyPastMidnight_StaysInTheFolderOfTheFirstDay()
    {
        var first = await _storage.SaveOriginalAsync(Photo());
        _time.Advance(TimeSpan.FromHours(26)); // next night
        var later = await _storage.SaveOriginalAsync(Photo());

        Assert.Contains("2026-10-17_Hochzeit", first);
        Assert.Contains("2026-10-17_Hochzeit", later);
        Assert.Equal("2026-10-17", _settings.Current.EventStartDate);
    }

    [Fact]
    public async Task NewEvent_GetsTheDateOfItsFirstPhoto()
    {
        await _storage.SaveOriginalAsync(Photo());
        _time.Advance(TimeSpan.FromDays(7));

        // Admin menu: new event started
        var copy = _settings.Current.Clone();
        copy.EventStartDate = null;
        _settings.Update(copy);

        var next = await _storage.SaveOriginalAsync(Photo());
        Assert.Contains("2026-10-24_Hochzeit", next);
    }

    [Fact]
    public void BeforeTheFirstPhoto_FolderUsesToday_WithoutFixingTheDate()
    {
        Assert.Equal("2026-10-17_Hochzeit", _storage.GetEventFolderName());
        Assert.Null(_settings.Current.EventStartDate);
    }

    [Fact]
    public async Task FixingTheDate_DoesNotNotifySettingsListeners()
    {
        var notified = false;
        _settings.Changed += (_, _) => notified = true;
        await _storage.SaveOriginalAsync(Photo());
        Assert.False(notified);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}

public class DeletePinTests
{
    [Fact]
    public void DeletePin_DefaultsToTheAdminPin()
    {
        var settings = new BoothSettings { AdminPin = "4711" };
        settings.Normalize();
        Assert.Null(settings.DeletePin);
        Assert.Equal("4711", settings.EffectiveDeletePin);
    }

    [Fact]
    public void DeletePin_CanBeSetSeparately_AndInvalidPinsAreIgnored()
    {
        var own = new BoothSettings { AdminPin = "4711", DeletePin = "2580" };
        own.Normalize();
        Assert.Equal("2580", own.EffectiveDeletePin);

        var tooShort = new BoothSettings { AdminPin = "4711", DeletePin = "12" };
        tooShort.Normalize();
        Assert.Equal("4711", tooShort.EffectiveDeletePin);
    }

    [Fact]
    public void DeleteMedia_RemovesAVideoTogetherWithItsPoster()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lumio_delete_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var video = Path.Combine(dir, "VIDEO_1.mp4");
            File.WriteAllBytes(video, [1, 2, 3]);
            File.WriteAllBytes(EventStorage.GetPosterPath(video), [4, 5, 6]);

            EventStorage.DeleteMedia(video);

            Assert.False(File.Exists(video));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
