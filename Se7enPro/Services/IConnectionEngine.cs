using System;
using System.Collections.Generic;
using System.Threading;
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
    void CancelConnecting();

    /// <summary>
    /// Second chance, taken only after <see cref="StartAsync"/> has already failed.
    /// </summary>
    /// <remarks>
    /// Exists for engines that deliberately stand aside on the first attempt and let their
    /// core handle a prerequisite itself. When that turns out not to work here, the engine
    /// has to go and get the thing the slow way - typically by bringing up a different tunnel
    /// entirely - and then the caller starts it again.
    ///
    /// Returns true when it did something worth retrying for. Default: no, so engines with
    /// no such fast path need no change.
    /// </remarks>
    Task<bool> RecoverFromMissingPrerequisitesAsync(CancellationToken ct) => Task.FromResult(false);
}
