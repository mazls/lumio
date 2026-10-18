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
