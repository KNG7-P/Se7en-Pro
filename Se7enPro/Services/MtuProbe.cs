using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public static class MtuProbe
{
    private const string Target = "1.1.1.1";
    private const int IpIcmpHeader = 28;
    private const int ScanFloor = 1200;
    private const int ScanCeiling = 1472;
    private const int SafetyMargin = 32;
    private const int Snap = 16;

    public static async Task<int?> FindBestTunnelMtuAsync(
        int overheadBytes,
        int minMtu,
        int maxMtu,
        CancellationToken ct = default)
    {
        if (!await FitsAsync(ScanFloor, ct).ConfigureAwait(false)) return null;

        var lo = ScanFloor;
        var hi = ScanCeiling;
        while (lo < hi)
        {
            ct.ThrowIfCancellationRequested();
            var probe = (lo + hi + 1) / 2;
            if (await FitsAsync(probe, ct).ConfigureAwait(false)) lo = probe;
            else hi = probe - 1;
        }

        var tunnelMtu = ((lo + IpIcmpHeader - overheadBytes - SafetyMargin) / Snap) * Snap;
        return Math.Clamp(tunnelMtu, minMtu, maxMtu);
    }

    private static async Task<bool> FitsAsync(int payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var pinger = new Ping();
            using var reg = ct.CanBeCanceled ? ct.Register(() =>
            {
                try { pinger.SendAsyncCancel(); } catch { }
            }) : default;
            var options = new PingOptions(64, dontFragment: true);
            var reply = await pinger.SendPingAsync(
                IPAddress.Parse(Target), 1000, new byte[payload], options).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return reply.Status == IPStatus.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
