using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TunEngineTest;

public sealed class UdpFakeDnsServer : IDisposable
{
    public const string FakeServerIp = "127.0.0.2";
    public const string LocalMarker = "198.51.100.7";

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public int QueriesAnswered;

    public void Start()
    {
        _udp = new UdpClient();
        _udp.ExclusiveAddressUse = true;
        _udp.Client.Bind(new IPEndPoint(IPAddress.Parse(FakeServerIp), 53));
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult req;
            try { req = await _udp!.ReceiveAsync(ct); }
            catch { return; }

            try
            {
                var response = MockSocksServer.BuildDnsResponse(
                    req.Buffer, new byte[] { 198, 51, 100, 7 });
                await _udp.SendAsync(response, response.Length, req.RemoteEndPoint);
                Interlocked.Increment(ref QueriesAnswered);
            }
            catch { }
        }
    }
}
