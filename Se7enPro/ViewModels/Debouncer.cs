using System;
using System.Windows.Threading;

namespace Se7enPro.ViewModels;

internal sealed class Debouncer : IDisposable
{
    private readonly DispatcherTimer _timer;
    private Action? _pending;
    private bool _disposed;

    public Debouncer(TimeSpan delay, DispatcherPriority priority = DispatcherPriority.Background)
    {
        _timer = new DispatcherTimer(priority) { Interval = delay };
        _timer.Tick += OnTick;
    }

        public void Schedule(Action action)
    {
        if (_disposed) return;
        _pending = action;
        _timer.Stop();
        _timer.Start();
    }

        public void Flush()
    {
        if (_disposed) return;
        _timer.Stop();
        var action = _pending;
        _pending = null;
        action?.Invoke();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        var action = _pending;
        _pending = null;
        action?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _pending = null;
    }
}
