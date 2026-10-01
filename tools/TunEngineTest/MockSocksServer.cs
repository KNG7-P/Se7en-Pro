using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TunEngineTest;

public sealed partial class MockSocksServer
{
    private TcpListener? _listener;
    private int _connects;

    public int Port { get; private set; }
    public int ConnectsServed => _connects;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public void Stop() => _listener?.Stop();

    private async Task AcceptLoopAsync()
    {
        while (_listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            await using var stream = client.GetStream();

            
            
            var gh = new byte[2];
            await ReadExactlyAsync(stream, gh);
            if (gh[0] != 5) return;
            var methods = new byte[gh[1]];
            if (methods.Length > 0) await ReadExactlyAsync(stream, methods);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 });

            var head = new byte[4];
            await ReadExactlyAsync(stream, head);
            if (head[1] != 1) return;

            string host;
            int port;
            switch (head[3])
            {
                case 1:
                    var v4 = new byte[4]; await ReadExactlyAsync(stream, v4);
                    var p4 = new byte[2]; await ReadExactlyAsync(stream, p4);
                    host = new IPAddress(v4).ToString(); port = (p4[0] << 8) | p4[1];
                    break;
                case 3:
                    var len = new byte[1]; await ReadExactlyAsync(stream, len);
                    var name = new byte[len[0]]; await ReadExactlyAsync(stream, name);
                    var p3 = new byte[2]; await ReadExactlyAsync(stream, p3);
                    host = Encoding.ASCII.GetString(name); port = (p3[0] << 8) | p3[1];
                    break;
                case 4:
                    var v6 = new byte[16]; await ReadExactlyAsync(stream, v6);
                    var p6 = new byte[2]; await ReadExactlyAsync(stream, p6);
                    host = new IPAddress(v6).ToString(); port = (p6[0] << 8) | p6[1];
                    break;
                default: return;
            }

            Interlocked.Increment(ref _connects);

            if (host == "198.18.100.7" && port == 9999)
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"));
                return;
            }

            if (port == 53 && (host == "1.1.1.1" || host == "8.8.8.8"))
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                await HandleDnsAsync(stream);
                return;
            }

            using var upstream = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await upstream.ConnectAsync(host, port, cts.Token);
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
            await RelayAsync(stream, upstream.GetStream());
        }
        catch { }
        finally
        {
            try { client.Close(); } catch { }
        }
    }
}
