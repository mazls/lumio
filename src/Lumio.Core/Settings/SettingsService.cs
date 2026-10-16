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

    public void Update(BoothSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        _store.Save(copy);
        Current = copy;
        Changed?.Invoke(this, copy);
    }
}
