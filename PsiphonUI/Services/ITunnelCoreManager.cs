using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PsiphonUI.Models;

namespace PsiphonUI.Services;

public interface ITunnelCoreManager
{
    ConnectionState State { get; }
    int SocksProxyPort { get; }
    int HttpProxyPort { get; }

    string ClientRegion { get; }

    string ConnectedServerRegion { get; }

    IReadOnlyList<string> AvailableEgressRegions { get; }
    IReadOnlyList<string> RecentLog { get; }

    long BytesSent { get; }

    long BytesReceived { get; }

    event EventHandler<ConnectionState>? StateChanged;
    event EventHandler<Notice>? NoticeReceived;
    event EventHandler<string>? LogLineAppended;

    event EventHandler? BytesTransferredChanged;

    event EventHandler? LogCleared;

    Task StartAsync();

    Task StopAsync();

    Task RestartAsync();
}
