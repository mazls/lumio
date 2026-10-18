namespace Lumio.Core.Settings;

/// <summary>Holds the active settings and notifies listeners when they change.</summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store;

    public SettingsService(SettingsStore store)
    {
        _store = store;
        Current = store.Load();
    }

    public BoothSettings Current { get; private set; }

    public event EventHandler<BoothSettings>? Changed;

    public void Update(BoothSettings settings) => Update(settings, notify: true);

    /// <param name="notify">
    /// False for bookkeeping values (e.g. the event's start date) that must not make the UI reload its
    /// settings, which would e.g. reconnect the camera in the middle of a capture.
    /// </param>
    public void Update(BoothSettings settings, bool notify)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        _store.Save(copy);
        Current = copy;
        if (notify)
            Changed?.Invoke(this, copy);
    }
}
