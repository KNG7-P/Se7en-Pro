using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public sealed class LocalProxyBridge : IAsyncDisposable
{

    private const long NotifyIntervalMs = 200;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _bytesSent;
    private long _bytesReceived;
    private long _lastNotifyTick;

    public int ListenPort { get; private set; }
    public int TargetPort { get; private set; }
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public event EventHandler? BytesTransferredChanged;

    public void Start(int listenPort, int targetPort, IPAddress? bindAddress = null)
    {
        Stop();

        ResetCounters();
        ListenPort = listenPort;
        TargetPort = targetPort;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(bindAddress ?? IPAddress.Loopback, listenPort);
        _listener.Start();

        _ = Task.Run(() => AcceptLoopAsync(_listener, targetPort, _cts.Token));
    }

    private async Task AcceptLoopAsync(TcpListener listener, int targetPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                _ = Task.Run(() => ForwardClientAsync(client, targetPort, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch
            {

                try { await Task.Delay(50, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ForwardClientAsync(TcpClient client, int targetPort, CancellationToken ct)
    {
        using (client)
        using (var target = new TcpClient { NoDelay = true })
        {
            try
            {
                await target.ConnectAsync(IPAddress.Loopback, targetPort, ct);
                var clientStream = client.GetStream();
                var targetStream = target.GetStream();

                var up = PumpStreamAsync(clientStream, targetStream, target.Client, isUpload: true, ct);
                var down = PumpStreamAsync(targetStream, clientStream, client.Client, isUpload: false, ct);
                await Task.WhenAll(up, down);
            }
            catch { }
        }
    }

    private async Task PumpStreamAsync(NetworkStream source, NetworkStream dest, Socket destSocket, bool isUpload, CancellationToken ct)
    {
        var buffer = new byte[32768];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                }
                catch { break; }

                if (n <= 0) break;

                try
                {
                    await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                catch { break; }

                if (isUpload)
                {
                    Interlocked.Add(ref _bytesSent, n);
                }
                else
                {
                    Interlocked.Add(ref _bytesReceived, n);
                }

                MaybeNotify();
            }
        }
        finally
        {

            try { destSocket.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    private void MaybeNotify()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastNotifyTick);
        if (now - last < NotifyIntervalMs) return;
        if (Interlocked.CompareExchange(ref _lastNotifyTick, now, last) != last) return;
        try { BytesTransferredChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _lastNotifyTick, 0);
        try { BytesTransferredChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
