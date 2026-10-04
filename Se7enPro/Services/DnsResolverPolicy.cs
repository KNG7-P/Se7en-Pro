using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

/// <summary>
/// Resolves the effective DNS resolver list for a tunnel session.
/// </summary>
public sealed class DnsResolverPolicy
{
    /// <summary>Plain-UDP resolvers used when the user has not configured any.</summary>
    internal static readonly IReadOnlyList<string> DefaultUdpResolvers =
        Array.AsReadOnly(new[] { "1.1.1.1", "8.8.8.8" });

    /// <summary>Cloudflare's DoH endpoint default.</summary>
    internal const string DefaultDohResolver = "https://1.1.1.1/dns-query";

    /// <summary>Cloudflare's v6 resolver, advertised only when a v6 address exists.</summary>
    internal const string DefaultUdpResolverV6 = "2606:4700:4700::1111";

    private readonly ILogger? _logger;

    public DnsResolverPolicy(ILogger? logger = null) => _logger = logger;

    /// <summary>The full resolver plan for one session.</summary>
    public sealed record Plan(
        IReadOnlyList<DnsServerEntry> Resolvers,
        IReadOnlyList<string> UdpLiterals,
        IReadOnlyList<string> DotLiterals,
        IReadOnlyList<string> DohLiterals,
        bool HasUserEntries,
        bool Strict,
        IReadOnlyList<string> Rejected);

    /// <summary>Builds the resolver plan from user settings.</summary>
    public Plan Build(UserSettings settings, bool hasV6Address)
    {
        var rejected = new List<string>();

        var udp = DnsSettings.ParseList(DnsTransport.Udp, settings.CustomDnsUdp, out var pUdp);
        rejected.AddRange(pUdp);
        var dot = DnsSettings.ParseList(DnsTransport.Dot, settings.CustomDnsDot, out var pDot);
        rejected.AddRange(pDot);
        var doh = DnsSettings.ParseList(DnsTransport.Doh, settings.CustomDnsDoh, out var pDoh);
        rejected.AddRange(pDoh);

        var userCount = udp.Count + dot.Count + doh.Count;
        var resolvers = new List<DnsServerEntry>(udp.Count + dot.Count + doh.Count + 2);
        resolvers.AddRange(udp);
        resolvers.AddRange(dot);
        resolvers.AddRange(doh);

        var strict = settings.CustomDnsStrict;

        var present = new HashSet<string>(
            resolvers.Where(r => r.Transport == DnsTransport.Udp && r.Address is not null)
                     .Select(r => r.Address!.ToString()),
            StringComparer.OrdinalIgnoreCase);

        if (!strict || userCount == 0)
        {
            if (userCount == 0)
            {
                resolvers.Add(new DnsServerEntry(
                    DnsTransport.Doh, DefaultDohResolver,
                    IPAddress.Parse("1.1.1.1"), "1.1.1.1", DnsSettings.DohPort));
            }

            foreach (var ip in DefaultUdpResolvers)
            {
                if (!present.Add(ip)) continue;
                resolvers.Add(new DnsServerEntry(DnsTransport.Udp, ip,
                    IPAddress.Parse(ip), ip, DnsSettings.UdpPort));
            }

            if (hasV6Address && IPAddress.TryParse(DefaultUdpResolverV6, out var v6))
            {
                if (present.Add(DefaultUdpResolverV6))
                {
                    resolvers.Add(new DnsServerEntry(DnsTransport.Udp, DefaultUdpResolverV6,
                        v6, DefaultUdpResolverV6, DnsSettings.UdpPort));
                }
            }
        }
        else if (userCount == 0)
        {
            _logger?.LogWarning(
                "DNS: strict mode is on but no usable resolver was configured; "
                + "falling back to the defaults so the session is not left without DNS.");

            resolvers.Add(new DnsServerEntry(
                DnsTransport.Doh, DefaultDohResolver,
                IPAddress.Parse("1.1.1.1"), "1.1.1.1", DnsSettings.DohPort));

            foreach (var ip in DefaultUdpResolvers)
            {
                if (!present.Add(ip)) continue;
                resolvers.Add(new DnsServerEntry(DnsTransport.Udp, ip,
                    IPAddress.Parse(ip), ip, DnsSettings.UdpPort));
            }
        }

        var udpLiterals = resolvers
            .Where(r => r.Transport == DnsTransport.Udp)
            .Select(r => r.Address?.ToString() ?? r.Host)
            .Where(s => s.Length > 0)
            .ToList();

        return new Plan(
            resolvers,
            udpLiterals,
            resolvers.Where(r => r.Transport == DnsTransport.Dot).Select(r => r.Raw).ToList(),
            resolvers.Where(r => r.Transport == DnsTransport.Doh).Select(r => r.Raw).ToList(),
            userCount > 0,
            strict,
            rejected);
    }

    /// <summary>Resolver list for psiphon tunnel-core config (plain IP literals).</summary>
    public static string ToTunnelCoreDnsServers(Plan plan)
    {
        var literals = plan.Resolvers
            .Where(r => r.Transport == DnsTransport.Udp && r.Address is not null)
            .Select(r => r.Address!.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(",", literals);
    }

    /// <summary>Resolvers formatted as a comma-separated list.</summary>
    public static string ToCommaSeparatedResolvers(Plan plan)
    {
        var tokens = plan.Resolvers
            .Select(r => r.Raw.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(",", tokens);
    }

    /// <summary>Resolvers usable as SOCKS relay targets.</summary>
    public static List<DnsServerEntry> RelayTargets(Plan plan) =>
        DnsSettings.ResolveForRelay(
            plan.Resolvers.Where(r => r.Transport == DnsTransport.Udp).ToList());

    /// <summary>The dns.servers array for Xray.</summary>
    public static List<object> ToXrayDnsServers(Plan plan)
    {
        var servers = new List<object>(plan.Resolvers.Count + 1);

        foreach (var r in plan.Resolvers)
        {
            var raw = r.Transport switch
            {
                DnsTransport.Dot => "tls://" + r.Host
                                    + (r.Port == DnsSettings.DotPort ? "" : ":" + r.Port),
                DnsTransport.Doh => r.Raw.StartsWith("doh:", StringComparison.OrdinalIgnoreCase)
                        && !r.Raw.StartsWith("doh://", StringComparison.OrdinalIgnoreCase)
                    ? "https://" + r.Raw[4..]
                    : r.Raw,
                _ => r.Address?.ToString() ?? r.Host,
            };
            servers.Add(raw);
        }

        servers.Add("localhost");
        return servers;
    }

    /// <summary>The dns.servers array for sing-box.</summary>
    public static List<object> ToSingBoxDnsServers(Plan plan)
    {
        var servers = new List<object>(plan.Resolvers.Count + 1);
        var n = 0;

        foreach (var r in plan.Resolvers)
        {
            var tag = "dns" + (n++).ToString(CultureInfo.InvariantCulture);
            var server = r.Address?.ToString() ?? r.Host;

            switch (r.Transport)
            {
                case DnsTransport.Dot:
                    servers.Add(new
                    {
                        type = "tls",
                        tag,
                        server = r.Host,
                        server_port = r.Port,
                    });
                    break;

                case DnsTransport.Doh:
                    servers.Add(new
                    {
                        type = "https",
                        tag,
                        server = r.Host,
                        server_port = r.Port,
                        path = DoHPath(r),
                    });
                    break;

                default:
                    servers.Add(new
                    {
                        type = "udp",
                        tag,
                        server = string.IsNullOrEmpty(server) ? "1.1.1.1" : server,
                        server_port = r.Port,
                    });
                    break;
            }
        }

        servers.Add(new { type = "local", tag = "dns" + (n++).ToString(CultureInfo.InvariantCulture) });
        return servers;
    }

    /// <summary>The path part of a DoH URL, defaulting to /dns-query.</summary>
    private static string DoHPath(DnsServerEntry entry)
    {
        var raw = entry.Raw.StartsWith("doh:", StringComparison.OrdinalIgnoreCase)
                  && !entry.Raw.StartsWith("doh://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + entry.Raw[4..]
            : entry.Raw;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath is { Length: > 1 } path)
        {
            return path;
        }
        return "/dns-query";
    }
}