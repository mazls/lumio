using System;
using System.Threading;
using Avalonia.Threading;

namespace Lumio.App.Avalonia;

/// <summary>
/// Drop-in replacement for <see cref="DispatcherTimer"/> that raises <see cref="Tick"/> on the UI thread.
/// On some Android devices Avalonia's DispatcherTimer never fires (its wake-up is lost while the UI thread
/// renders), so the interval runs on a thread pool timer and each tick is posted to the dispatcher instead.
/// </summary>
public sealed class UiTimer
{
    private readonly object _lock = new();
    private Timer? _timer;
    private int _generation;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    public bool IsEnabled { get; private set; }

    public event EventHandler? Tick;

    public void Start()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            IsEnabled = true;
            var generation = ++_generation;
            _timer = new Timer(_ => Dispatcher.UIThread.Post(() => RaiseTick(generation)), null, Interval, Interval);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            IsEnabled = false;
            _generation++;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void RaiseTick(int generation)
    {
        // Ignore ticks that were already queued when the timer was stopped or restarted
        if (!IsEnabled || generation != _generation)
            return;
        Tick?.Invoke(this, EventArgs.Empty);
    }
}
