using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public interface IUiAdapter
{
    

        bool CheckAccess();

        void Post(Action action);

        void Invoke(Action action);

    

    void SetClipboard(string text);

        Task<string?> GetClipboardTextAsync();

    

        Task<bool> ConfirmAsync(string title, string message);

        Task<(string Kind, string Value)?> AskRuleAsync();

        Task<IReadOnlyList<string>> PickApplicationsAsync();

        Task<bool> ConfirmElevationAsync();

    

    void MinimizeMainWindow();

    void ToggleMaximizeMainWindow();

        void RequestCloseMainWindow();

        void ExitWithoutPrompt();

        void RequestRelaunchOnExit();

        bool IsSystemDarkTheme();

        object FrozenBrush(string hex);

        object CreateGroupedOptionsView(System.Collections.IList items, string groupName);
}

public static class Ui
{
    public static IUiAdapter? Adapter { get; set; }

    public static bool CheckAccess() => Adapter?.CheckAccess() ?? true;

    public static void Post(Action action)
    {
        if (Adapter is null || Adapter.CheckAccess())
        {
            action();
            return;
        }

        Adapter.Post(action);
    }

    public static void Invoke(Action action)
    {
        var adapter = Adapter;
        if (adapter is null || adapter.CheckAccess())
        {
            action();
            return;
        }

        adapter.Invoke(action);
    }

    public static void SetClipboard(string text)
    {
        try
        {
            Adapter?.SetClipboard(text);
        }
        catch
        {
            
        }
    }

    public static async Task<string> GetClipboardTextAsync()
    {
        try
        {
            return await (Adapter?.GetClipboardTextAsync() ?? Task.FromResult<string?>(null)) ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static Task<bool> ConfirmAsync(string title, string message) =>
        Adapter?.ConfirmAsync(title, message) ?? Task.FromResult(false);

    public static Task<(string Kind, string Value)?> AskRuleAsync() =>
        Adapter?.AskRuleAsync() ?? Task.FromResult<(string Kind, string Value)?>(null);

    public static Task<IReadOnlyList<string>> PickApplicationsAsync() =>
        Adapter?.PickApplicationsAsync() ?? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public static Task<bool> ConfirmElevationAsync() =>
        Adapter?.ConfirmElevationAsync() ?? Task.FromResult(false);

    public static void MinimizeMainWindow() => Adapter?.MinimizeMainWindow();

    public static void ToggleMaximizeMainWindow() => Adapter?.ToggleMaximizeMainWindow();

    public static void RequestCloseMainWindow() => Adapter?.RequestCloseMainWindow();

    public static void ExitWithoutPrompt() => Adapter?.ExitWithoutPrompt();

    public static void RequestRelaunchOnExit() => Adapter?.RequestRelaunchOnExit();

    public static bool IsSystemDarkTheme() => Adapter?.IsSystemDarkTheme() ?? true;

    public static object FrozenBrush(string hex) =>
        Adapter?.FrozenBrush(hex) ?? throw new InvalidOperationException("No UI adapter is registered.");

        public static T Brush<T>(string hex) => (T)FrozenBrush(hex);

    public static object CreateGroupedOptionsView(System.Collections.IList items, string groupName) =>
        Adapter?.CreateGroupedOptionsView(items, groupName) ?? items;
}

public sealed class UiTimer : IDisposable
{
    private readonly Action _tick;
    private readonly int _intervalMs;
    private Timer? _timer;

        private int _pending;

    public UiTimer(TimeSpan interval, Action tick)
    {
        _intervalMs = Math.Max(1, (int)interval.TotalMilliseconds);
        _tick = tick;
    }

    public bool IsRunning => _timer is not null;

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(Fire, null, _intervalMs, _intervalMs);
    }

    public void Stop()
    {
        var timer = _timer;
        if (timer is null) return;
        _timer = null;
        timer.Dispose();
    }

        public void Restart()
    {
        Stop();
        Start();
    }

    private void Fire(object? state)
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        Ui.Post(() =>
        {
            try
            {
                _tick();
            }
            finally
            {
                Volatile.Write(ref _pending, 0);
            }
        });
    }

    public void Dispose() => Stop();
}
