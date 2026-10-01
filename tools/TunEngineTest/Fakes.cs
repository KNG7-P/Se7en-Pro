using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Se7enPro.Models;
using Se7enPro.Services;

namespace TunEngineTest;

internal sealed class FakeSettings : ISettingsService
{
    public UserSettings Settings { get; } = new();

    public event EventHandler? SettingsChanged;

    public void Load() { }
    public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeTunnel : ITunnelCoreManager
{
    public ConnectionState State { get; private set; } = ConnectionState.Connected;
    public int SocksProxyPort { get; private set; }
    public int HttpProxyPort => SocksProxyPort; 
    public string ClientRegion => "";
    public string ConnectedServerRegion => "";
    public string CurrentRouteIp => "";
    public string CurrentRouteSni => "";
    public IReadOnlyList<string> AvailableEgressRegions => Array.Empty<string>();
    public IReadOnlyList<string> RecentLog => Array.Empty<string>();
    public long BytesSent => 0;
    public long BytesReceived => 0;
    public int ConnectProgressPercent => 100;
    public string ConnectProgressText => "Connected";

#pragma warning disable CS0067
    public event EventHandler? RouteChanged;
    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<bool>? ConnectionIntentChanged;
    public event EventHandler<Notice>? NoticeReceived;
    public event EventHandler<string>? LogLineAppended;
    public event EventHandler? BytesTransferredChanged;
    public event EventHandler? LogCleared;
    public event EventHandler? ConnectProgressChanged;
#pragma warning restore CS0067

    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task RestartAsync() => Task.CompletedTask;
    public void CancelInFlightConnection() { }

        public void Configure(int socksPort, ConnectionState state)
    {
        SocksProxyPort = socksPort;
        State = state;
        StateChanged?.Invoke(this, state);
    }
}

internal sealed class NullChildGuard : IChildProcessGuard
{
    public void Adopt(Process process) { }
}

internal sealed class FakeSystemProxy : ISystemProxyService
{
    public int SetCalls;
    public int ClearCalls;
    public int? LastSetPort;

    public bool IsApplied => SetCalls > ClearCalls;

    public void Set(int httpProxyPort)
    {
        SetCalls++;
        LastSetPort = httpProxyPort;
    }

    public void Clear() => ClearCalls++;

    public void RestoreIfCrashed() { }
}
