using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Se7enPro.Models;

namespace Se7enPro.Services;

public interface ITunnelCoreManager
{
    ConnectionState State { get; }
    int SocksProxyPort { get; }
    int HttpProxyPort { get; }

    string ClientRegion { get; }

    string ConnectedServerRegion { get; }

    string CurrentRouteIp { get; }

    string CurrentRouteSni { get; }

    event EventHandler? RouteChanged;

    IReadOnlyList<string> AvailableEgressRegions { get; }
    IReadOnlyList<string> RecentLog { get; }

    long BytesSent { get; }

    long BytesReceived { get; }

    int ConnectProgressPercent { get; }

    string ConnectProgressText { get; }

    event EventHandler<ConnectionState>? StateChanged;
    event EventHandler<Notice>? NoticeReceived;
    event EventHandler<string>? LogLineAppended;

    event EventHandler? BytesTransferredChanged;

    event EventHandler? LogCleared;
    event EventHandler? ConnectProgressChanged;

    Task StartAsync();

    Task StopAsync();

    Task RestartAsync();
}
