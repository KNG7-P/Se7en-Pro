using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Se7enPro.Models;

namespace Se7enPro.Services.Foxy;

public static class FoxyNetwork
{
    private const int DialStaggerMs = 300;
    private static readonly TimeSpan FailedAddressCooldown = TimeSpan.FromMinutes(10);
    private static readonly Uri DefaultMozillaProbeUri = new("https://api.accounts.firefox.com/v1/account/status");
    private static readonly ConcurrentDictionary<IPAddress, DateTime> FailedAddresses = new();
    private static readonly Dictionary<string, IPAddress[]> KnownMozillaIps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api.accounts.firefox.com"] = new[]
        {
            IPAddress.Parse("151.101.1.91"), IPAddress.Parse("151.101.65.91"),
            IPAddress.Parse("151.101.129.91"), IPAddress.Parse("151.101.193.91"),
        },
        ["accounts.firefox.com"] = new[]
        {
            IPAddress.Parse("151.101.1.91"), IPAddress.Parse("151.101.65.91"),
            IPAddress.Parse("151.101.129.91"), IPAddress.Parse("151.101.193.91"),
        },
        ["vpn.mozilla.org"] = new[]
        {
            IPAddress.Parse("151.101.1.91"), IPAddress.Parse("151.101.65.91"),
            IPAddress.Parse("151.101.129.91"), IPAddress.Parse("151.101.193.91"),
        },
        ["firefox.settings.services.mozilla.com"] = new[]
        {
            IPAddress.Parse("34.107.243.141"), IPAddress.Parse("35.244.209.117"),
        },
    };

    private static readonly Dictionary<string, IPAddress[]> KnownFastlyMasqueIps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["us.m1.fastly-masque.net"] = new[] { IPAddress.Parse("23.235.42.1") },
        ["ca.m1.fastly-masque.net"] = new[] { IPAddress.Parse("23.235.42.2") },
        ["p.m1.fastly-masque.net"] = new[] { IPAddress.Parse("23.235.42.11") },
        ["fra1.m1.fastly-masque.net"] = new[] { IPAddress.Parse("23.235.42.8") },
        ["muc139.m1.fastly-masque.net"] = new[] { IPAddress.Parse("23.235.42.8") },
        ["eglc860.m1.fastly-masque.net"] = new[] { IPAddress.Parse("23.235.42.10") },
        ["ehrd229.m1.fastly-masque.net"] = new[] { IPAddress.Parse("23.235.42.17") },
        ["hel141.m1.fastly-masque.net"] = new[] { IPAddress.Parse("23.235.42.15") },
        ["essb127.m1.fastly-masque.net"] = new[] { IPAddress.Parse("23.235.42.16") },
        ["lfpb115.m1.fastly-masque.net"] = new[] { IPAddress.Parse("23.235.42.9") },
    };

    private static readonly IPAddress[] DefaultFastlyMasqueIps =
    {
        IPAddress.Parse("23.235.42.1"), IPAddress.Parse("23.235.42.11"),
    };

    public static bool IsBlockPageOrBogon(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6None) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast)
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            if (!address.TryWriteBytes(b, out _))
                return true;

            return b[0] == 0 ||
                   b[0] == 10 ||
                   b[0] == 127 ||
                   (b[0] == 169 && b[1] == 254) ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   b[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> b = stackalloc byte[16];
            if (!address.TryWriteBytes(b, out _))
                return true;

            if ((b[0] & 0xfe) == 0xfc)
                return true;

            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x41 && b[3] == 0x88 &&
                b[4] == 0x00 && b[5] == 0x02 && b[6] == 0x06 && b[7] == 0x00 &&
                b[8] == 0x00 && b[9] == 0x10 && b[10] == 0x00 && b[11] == 0x10 &&
                b[12] == 0x00 && b[13] == 0x34)
            {
                return true;
            }
        }

        return false;
    }

    public static void MarkAddressFailed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        FailedAddresses[address] = DateTime.UtcNow + FailedAddressCooldown;
    }

    public static void ClearAddressFailure(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        FailedAddresses.TryRemove(address, out _);
    }

    internal static void ClearAllAddressFailuresForTests() => FailedAddresses.Clear();

    public static IPAddress[] OrderAddressesByHealth(IEnumerable<IPAddress> addresses)
    {
        var now = DateTime.UtcNow;
        var healthy = new List<IPAddress>();
        var demoted = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();

        foreach (var raw in addresses)
        {
            var addr = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
            if (!seen.Add(addr))
                continue;

            if (FailedAddresses.TryGetValue(addr, out var until))
            {
                if (now <= until)
                {
                    demoted.Add(addr);
                    continue;
                }
                FailedAddresses.TryRemove(addr, out _);
            }

            healthy.Add(addr);
        }

        if (demoted.Count > 0)
            healthy.AddRange(demoted);
        return healthy.ToArray();
    }

    public static async Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return Array.Empty<IPAddress>();
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };

        if (TryGetPinnedAddresses(host, out var pinned))
            return OrderAddressesByHealth(pinned);

        var dohTask = QueryDohAsync(host, ct);
        var sysTask = QuerySystemDnsFilteredAsync(host, ct);

        try
        {
            var doh = await dohTask.ConfigureAwait(false);
            if (doh.Length > 0)
                return OrderAddressesByHealth(doh);
        }
        catch { }

        try
        {
            var sys = await sysTask.ConfigureAwait(false);
            if (sys.Length > 0)
                return OrderAddressesByHealth(sys);
        }
        catch { }

        return Array.Empty<IPAddress>();
    }

    private static async Task<IPAddress[]> QuerySystemDnsFilteredAsync(string host, CancellationToken ct)
    {
        try
        {
            var system = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return system.Where(a => !IsBlockPageOrBogon(a)).ToArray();
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static bool TryGetPinnedAddresses(string host, out IPAddress[] addresses)
    {
        if (KnownMozillaIps.TryGetValue(host, out var mozilla))
        {
            addresses = mozilla;
            return true;
        }

        if (host.EndsWith(".fastly-masque.net", StringComparison.OrdinalIgnoreCase))
        {
            addresses = KnownFastlyMasqueIps.TryGetValue(host, out var edge)
                ? edge
                : DefaultFastlyMasqueIps;
            return true;
        }

        addresses = Array.Empty<IPAddress>();
        return false;
    }

    public static bool IsDirectFoxyOnly(UserSettings? settings)
    {
        if (settings is null) return true;
        var method = ConnectionMethodExtensions.ParseConnectionMethod(settings.ConnectionMethod);
        return method is not (ConnectionMethod.FoxyOverWarp or ConnectionMethod.FoxyOverV2Ray);
    }

    public static IWebProxy? ResolveProxy(UserSettings? settings, ITunnelCoreManager? tunnel)
    {
        if (settings is not null && settings.FirefoxCustomProxyEnabled &&
            IsDirectFoxyOnly(settings) &&
            !string.IsNullOrWhiteSpace(settings.FirefoxCustomProxyHost))
        {
            var scheme = string.Equals(settings.FirefoxCustomProxyType, "http", StringComparison.OrdinalIgnoreCase)
                ? "http" : "socks5";
            var port = settings.FirefoxCustomProxyPort > 0
                ? settings.FirefoxCustomProxyPort
                : (scheme == "http" ? 8080 : 1080);
            var proxy = new WebProxy($"{scheme}://{settings.FirefoxCustomProxyHost.Trim()}:{port}");
            if (!string.IsNullOrEmpty(settings.FirefoxCustomProxyUsername))
            {
                proxy.Credentials = new NetworkCredential(
                    settings.FirefoxCustomProxyUsername, settings.FirefoxCustomProxyPassword ?? "");
            }
            return proxy;
        }

        if (tunnel is not null && tunnel.State == ConnectionState.Connected)
        {
            if (tunnel.SocksProxyPort > 0) return new WebProxy($"socks5://127.0.0.1:{tunnel.SocksProxyPort}");
            if (tunnel.HttpProxyPort > 0) return new WebProxy($"http://127.0.0.1:{tunnel.HttpProxyPort}");
        }

        if (settings is not null &&
            settings.LocalSocksProxyPort > 0 &&
            IsTcpPortListening("127.0.0.1", settings.LocalSocksProxyPort, 150))
        {
            return new WebProxy($"socks5://127.0.0.1:{settings.LocalSocksProxyPort}");
        }

        return null;
    }

    public static IWebProxy? ResolveEffectiveProxy(
        UserSettings? settings,
        ITunnelCoreManager? tunnel,
        Uri? probeUri = null,
        Func<Uri, bool>? isSelfProxy = null)
    {
        var explicitProxy = ResolveProxy(settings, tunnel);
        if (explicitProxy is not null)
            return explicitProxy;

        return TryGetSystemWebProxy(probeUri ?? DefaultMozillaProbeUri, isSelfProxy);
    }

    public static IWebProxy? TryGetSystemWebProxy(Uri targetUri, Func<Uri, bool>? isSelfProxy = null)
    {
        try
        {
            var sys = HttpClient.DefaultProxy;
            if (sys.IsBypassed(targetUri))
                return null;

            var pUri = sys.GetProxy(targetUri);
            if (pUri is null || pUri.Equals(targetUri))
                return null;

            if (isSelfProxy?.Invoke(pUri) == true)
                return null;

            if (IsLoopbackHost(pUri.Host) && pUri.Port > 0 && !IsTcpPortListening(pUri.Host, pUri.Port, 150))
                return null;

            var webProxy = new WebProxy(pUri);
            if (sys.Credentials is not null)
                webProxy.Credentials = sys.Credentials;
            return webProxy;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static bool IsTcpPortListening(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                return false;
            client.EndConnect(ar);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static async ValueTask<System.IO.Stream> ConnectDirectWithDohFallbackAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        if (IsLoopbackHost(host))
        {
            var directLoop = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await directLoop.ConnectAsync(host, port, ct).ConfigureAwait(false);
                return new NetworkStream(directLoop, ownsSocket: true);
            }
            catch
            {
                directLoop.Dispose();
                throw;
            }
        }

        var addresses = await ResolveHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        var socket = await ConnectRaceAsync(addresses, port, ct).ConfigureAwait(false);
        return new HelloSplitStream(new NetworkStream(socket, ownsSocket: true), HelloSplitMode.Sni);
    }

    public static async Task<Socket> ConnectRaceAsync(
        IPAddress[] addresses,
        int port,
        CancellationToken ct = default,
        int staggerMs = DialStaggerMs)
    {
        if (addresses is null || addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        var ordered = OrderAddressesByHealth(addresses);
        if (ordered.Length == 1)
        {
            var single = new Socket(ordered[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await single.ConnectAsync(new IPEndPoint(ordered[0], port), ct).ConfigureAwait(false);
                ClearAddressFailure(ordered[0]);
                return single;
            }
            catch
            {
                single.Dispose();
                if (!ct.IsCancellationRequested)
                    MarkAddressFailed(ordered[0]);
                throw;
            }
        }

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inFlight = new List<Task<(Socket? Socket, IPAddress Addr, Exception? Error)>>(ordered.Length);
        Exception? lastError = null;
        var nextIndex = 0;

        Task<(Socket? Socket, IPAddress Addr, Exception? Error)> LaunchNext()
        {
            var addr = ordered[nextIndex++];
            return DialSingleAsync(addr, port, raceCts.Token);
        }

        static async Task<(Socket? Socket, IPAddress Addr, Exception? Error)> DialSingleAsync(
            IPAddress addr, int port, CancellationToken token)
        {
            var sock = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await sock.ConnectAsync(new IPEndPoint(addr, port), token).ConfigureAwait(false);
                return (sock, addr, null);
            }
            catch (Exception ex)
            {
                sock.Dispose();
                return (null, addr, ex);
            }
        }

        static void DrainLosers(IEnumerable<Task<(Socket? Socket, IPAddress Addr, Exception? Error)>> tasks)
        {
            foreach (var t in tasks)
            {
                _ = t.ContinueWith(completed =>
                {
                    if (completed.IsCompletedSuccessfully && completed.Result.Socket is { } s)
                    {
                        try { s.Dispose(); } catch { }
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        inFlight.Add(LaunchNext());

        while (inFlight.Count > 0)
        {
            var anyTask = Task.WhenAny(inFlight);
            Task completed;
            if (nextIndex < ordered.Length)
            {
                var delayTask = Task.Delay(staggerMs, raceCts.Token);
                completed = await Task.WhenAny(anyTask, delayTask).ConfigureAwait(false);
                if (completed == delayTask)
                {
                    ct.ThrowIfCancellationRequested();
                    inFlight.Add(LaunchNext());
                    continue;
                }
            }
            else
            {
                await anyTask.ConfigureAwait(false);
            }

            var finished = await anyTask.ConfigureAwait(false);
            inFlight.Remove(finished);
            var outcome = await finished.ConfigureAwait(false);

            if (outcome.Socket is not null)
            {
                ClearAddressFailure(outcome.Addr);
                raceCts.Cancel();
                DrainLosers(inFlight);
                return outcome.Socket;
            }

            lastError = outcome.Error;
            if (!ct.IsCancellationRequested)
                MarkAddressFailed(outcome.Addr);

            if (inFlight.Count == 0 && nextIndex < ordered.Length)
                inFlight.Add(LaunchNext());
        }

        ct.ThrowIfCancellationRequested();
        throw lastError ?? new SocketException((int)SocketError.TimedOut);
    }

    private static async Task<IPAddress[]> QueryDohAsync(string host, CancellationToken ct)
    {
        var escaped = Uri.EscapeDataString(host);
        var endpoints = new[]
        {
            $"https://1.1.1.1/dns-query?name={escaped}&type=A",
            $"https://1.0.0.1/dns-query?name={escaped}&type=A",
            $"https://8.8.8.8/resolve?name={escaped}&type=A",
            $"https://8.8.4.4/resolve?name={escaped}&type=A",
            $"https://9.9.9.9:5053/dns-query?name={escaped}&type=A",
        };

        using var dohCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dohCts.CancelAfter(TimeSpan.FromSeconds(3));

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(2.5),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

        var tasks = endpoints.Select(ep => QuerySingleDohEndpointAsync(client, ep, dohCts.Token)).ToList();
        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);
            var addrs = await finished.ConfigureAwait(false);
            if (addrs.Length > 0)
            {
                dohCts.Cancel();
                return addrs;
            }
        }

        return Array.Empty<IPAddress>();
    }

    private static async Task<IPAddress[]> QuerySingleDohEndpointAsync(
        HttpClient client, string endpoint, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.Accept.ParseAdd("application/dns-json");
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<IPAddress>();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("Answer", out var answer) || answer.ValueKind != JsonValueKind.Array)
                return Array.Empty<IPAddress>();

            var results = new List<IPAddress>();
            foreach (var entry in answer.EnumerateArray())
            {
                if (entry.TryGetProperty("data", out var data) &&
                    IPAddress.TryParse(data.GetString(), out var address) &&
                    !IsBlockPageOrBogon(address))
                {
                    results.Add(address);
                }
            }
            return results.ToArray();
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }
}

public sealed class DynamicWebProxy : IWebProxy
{
    private readonly Func<IWebProxy?> _provider;

    public DynamicWebProxy(Func<IWebProxy?> provider) => _provider = provider;

    public ICredentials? Credentials
    {
        get => _provider()?.Credentials;
        set { }
    }

    public Uri? GetProxy(Uri destination) => _provider()?.GetProxy(destination);

    public bool IsBypassed(Uri host) => _provider()?.IsBypassed(host) ?? true;
}
