using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services;

public sealed partial class WintunTunManager : ITunManager
{
    private const string TunInterfaceName = "se7en_tun";
    private const string CachedTunExeName = "Se7enPro.Tun.exe";
    private const int TunMtu = 1420;

    private static readonly IPAddress TunAddressV4 = IPAddress.Parse("198.18.0.1");
    private const byte TunPrefixV4 = 30;
    private static readonly IPAddress TunAddressV6 = IPAddress.Parse("fdfe:dcba:9876::1");
    private const byte TunPrefixV6 = 126;

    private static readonly IPAddress TunDnsAddressV4 = IPAddress.Parse("198.18.0.2");
    private const byte TunDnsPrefixV4 = 32;

    private static readonly IPAddress TunDnsAddressV6 = IPAddress.Parse("fdfe:dcba:9876::2");
    private const byte TunDnsPrefixV6 = 128;

    private const string UdpSessionTimeout = "30s";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AdapterWaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AdapterDownWait = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan SupervisorRestartWindow = TimeSpan.FromMinutes(5);
    private const int SupervisorMaxRestartsInWindow = 5;
    private static readonly TimeSpan SupervisorMaxBackoff = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CatchAllSuspendGrace = TimeSpan.FromSeconds(90);

    private readonly ILogger<WintunTunManager> _logger;
    private readonly ITunnelCoreManager _tunnel;
    private readonly ISettingsService _settings;
    private readonly IChildProcessGuard _childGuard;
    private readonly ISystemProxyService _systemProxy;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisorTask;
    private Process? _process;
    private string? _workDir;
    private string? _logPath;
    private StreamWriter? _logWriter;
    private long _logBytesWritten;
    private int _logCapNoticeWritten;
    private const long LogByteCap = 32L * 1024 * 1024;

    private readonly ConcurrentQueue<string> _recentOutput = new();
    private const int RecentOutputMax = 24;

    private readonly object _routeLock = new();
    private readonly List<WintunRouteApi.RouteEntry> _appliedRoutes = new();

    private readonly List<WintunRouteApi.RouteEntry> _catchAllRoutes = new();
    private bool _catchAllSuspended;
    private CancellationTokenSource? _catchAllGraceCts;
    private volatile bool _catchAllGraceExpired;

    private SocksDnsForwarder? _dnsForwarder;
    private bool _adapterDnsSet;
    private bool _v6Enabled;

    private bool _quicBlockInstalled;

    private readonly Dictionary<string, (WintunRouteApi.RouteEntry Entry, string Domain)> _dynamicRoutes
        = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> _underlyingDnsServers = Array.Empty<string>();
    private int _realIfIndex;
    private IPAddress _realGateway = IPAddress.Any;
    private bool _realRouteKnown;
    private int _tunIfIndex;

    private int _realIfIndexV6;
    private IPAddress _realGatewayV6 = IPAddress.IPv6Any;
    private bool _realRouteV6Known;

    internal static Func<IReadOnlyList<string>>? UnderlyingDnsServersOverride = null;

    private CancellationTokenSource? _refresherCts;
    private Task? _refresherTask;

    private CancellationTokenSource? _processSplitCts;
    private Task? _processSplitTask;

    private bool _proxySuppressedByTun;

    private int _activeSocksPort;
    private string _activeSplitHash = "";

    public TunState State { get; private set; } = TunState.Off;
    public string? LastError { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler<string>? LogLineAppended;

    public WintunTunManager(
        ILogger<WintunTunManager> logger,
        ITunnelCoreManager tunnel,
        ISettingsService settings,
        IChildProcessGuard childGuard,
        ISystemProxyService systemProxy)
    {
        _logger = logger;
        _tunnel = tunnel;
        _settings = settings;
        _childGuard = childGuard;
        _systemProxy = systemProxy;

        _tunnel.StateChanged += OnTunnelStateChanged;
        _tunnel.RouteChanged += OnTunnelRouteChanged;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    private async void OnTunnelStateChanged(object? sender, ConnectionState s)
    {
        try { await ReconcileAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "OnTunnelStateChanged failed"); }
    }

    private void OnTunnelRouteChanged(object? sender, EventArgs e)
    {
        if (State != TunState.Running || !_realRouteKnown) return;
        var cleanIp = CleanIpOrHost(_tunnel.CurrentRouteIp);
        if (IPAddress.TryParse(cleanIp, out var routeIp) &&
            routeIp.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(routeIp))
        {
            var entry = WintunRouteApi.AddRoute(_realIfIndex, routeIp, 32, _realGateway);
            if (entry is null) return;
            var ipKey = routeIp.ToString();
            lock (_routeLock)
            {
                if (!_dynamicRoutes.ContainsKey(ipKey))
                {
                    _appliedRoutes.Add(entry);
                    _dynamicRoutes[ipKey] = (entry, "tunnel-server");
                    WriteDiag($"pinned active tunnel route: {routeIp}/32 via real gateway {_realGateway}");
                }
            }
        }
    }

    internal static string CleanIpOrHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Replace("(outer)", "").Replace("(inner)", "").Trim();
        if (s.StartsWith("[") && s.Contains("]:"))
            return s.Substring(1, s.IndexOf("]:") - 1).Trim();
        if (s.StartsWith("[") && s.EndsWith("]"))
            return s.Substring(1, s.Length - 2).Trim();
        var colonIdx = s.LastIndexOf(':');
        if (colonIdx > 0 && !s.Contains(']'))
            return s.Substring(0, colonIdx).Trim();
        return s;
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        try { await ReconcileAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "OnSettingsChanged failed"); }
    }
}
