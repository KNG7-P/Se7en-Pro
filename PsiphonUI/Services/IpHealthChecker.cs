using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace PsiphonUI.Services;

public interface IIpHealthChecker
{
    Task<IpHealthResult> CheckAsync(
    string ip,
    IpHealthCheckMethod method,
    TimeSpan timeout,
    string sniHost,
    CancellationToken ct);
}

public enum IpHealthCheckMethod
{
    Ping,
    Tcp443,
    TlsSni,
}

public sealed record IpHealthResult(
    string Ip,
    bool Ok,
    int LatencyMs,
    int? Ttl,
    string Message);

public sealed class IpHealthChecker : IIpHealthChecker
{
    public async Task<IpHealthResult> CheckAsync(
        string ip,
        IpHealthCheckMethod method,
        TimeSpan timeout,
        string sniHost,
        CancellationToken ct)
    {
        return method switch
        {
            IpHealthCheckMethod.Ping => await PingAsync(ip, timeout, ct).ConfigureAwait(false),
            IpHealthCheckMethod.Tcp443 => await Tcp443Async(ip, timeout, ct).ConfigureAwait(false),
            IpHealthCheckMethod.TlsSni => await TlsSniAsync(ip, sniHost, timeout, ct).ConfigureAwait(false),
            _ => new IpHealthResult(ip, false, 0, null, "unknown method"),
        };
    }

    private static async Task<IpHealthResult> PingAsync(string ip, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var ping = new Ping();
            var to = (int)Math.Clamp(timeout.TotalMilliseconds, 250, 30_000);
            using var reg = ct.Register(() => { try { ping.SendAsyncCancel(); } catch { } });

            var reply = await ping.SendPingAsync(ip, to).ConfigureAwait(false);
            sw.Stop();
            if (reply.Status == IPStatus.Success)
            {
                return new IpHealthResult(
                    ip, true,
                    (int)reply.RoundtripTime,
                    reply.Options?.Ttl,
                    "ping ok");
            }
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, reply.Status.ToString());
        }
        catch (OperationCanceledException)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, "cancelled");
        }
        catch (PingException ex)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, Trim(ex.Message));
        }
        catch (Exception ex)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, Trim(ex.Message));
        }
    }

    private static async Task<IpHealthResult> Tcp443Async(string ip, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeout);

            await client.ConnectAsync(ip, 443, linkedCts.Token).ConfigureAwait(false);
            sw.Stop();
            return new IpHealthResult(ip, true, (int)sw.ElapsedMilliseconds, null, "tcp 443 ok");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, "timeout");
        }
        catch (OperationCanceledException)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, "cancelled");
        }
        catch (SocketException ex)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, $"sock:{ex.SocketErrorCode}");
        }
        catch (Exception ex)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, Trim(ex.Message));
        }
    }

    private static async Task<IpHealthResult> TlsSniAsync(
        string ip, string sniHost, TimeSpan timeout, CancellationToken ct)
    {
        var host = string.IsNullOrWhiteSpace(sniHost) ? ip : sniHost.Trim();
        var sw = Stopwatch.StartNew();

        TcpClient? client = null;
        SslStream? ssl = null;
        try
        {
            client = new TcpClient { NoDelay = true };
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeout);

            await client.ConnectAsync(ip, 443, linkedCts.Token).ConfigureAwait(false);

            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            var opts = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };
            await ssl.AuthenticateAsClientAsync(opts, linkedCts.Token).ConfigureAwait(false);
            sw.Stop();
            return new IpHealthResult(ip, true, (int)sw.ElapsedMilliseconds, null, $"tls ok ({host})");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, "timeout");
        }
        catch (OperationCanceledException)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, "cancelled");
        }
        catch (AuthenticationException ex)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, "tls:" + Trim(ex.Message));
        }
        catch (SocketException ex)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, $"sock:{ex.SocketErrorCode}");
        }
        catch (Exception ex)
        {
            return new IpHealthResult(ip, false, (int)sw.ElapsedMilliseconds, null, Trim(ex.Message));
        }
        finally
        {
            try { ssl?.Dispose(); } catch { }
            try { client?.Dispose(); } catch { }
        }
    }

    private static string Trim(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var first = s.Split('\n', 2)[0].Trim();
        return first.Length <= 80 ? first : first[..80];
    }
}
