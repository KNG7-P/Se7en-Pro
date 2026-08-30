using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Se7enPro.Models;

namespace Se7enPro.Services;

public interface IConnectionEngine
{

    ConnectionMethod Method { get; }

    ConnectionState State { get; }

    int SocksProxyPort { get; }

    int HttpProxyPort { get; }

    string ClientRegion { get; }
    string ConnectedServerRegion { get; }
    string CurrentRouteIp { get; }
    string CurrentRouteSni { get; }

    IReadOnlyList<string> AvailableEgressRegions { get; }

    long BytesSent { get; }
    long BytesReceived { get; }

    int ConnectProgressPercent { get; }
    string ConnectProgressText { get; }

    IReadOnlyList<string> CoreProcessNames { get; }

    event EventHandler<ConnectionState>? StateChanged;
    event EventHandler<Notice>? NoticeReceived;
    event EventHandler<string>? LogLineAppended;
    event EventHandler? BytesTransferredChanged;
    event EventHandler? RouteChanged;
    event EventHandler? ConnectProgressChanged;

    Task StartAsync();
    Task StopAsync();
}
