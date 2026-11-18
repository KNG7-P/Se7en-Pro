using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services.Foxy;

public sealed class FoxyEngine : IConnectionEngine, IDisposable
{
    private const int DefaultSocksPort = 1840;
    private const int DefaultHttpPort = 1841;
    private const int MaxRememberedFailedEdges = 8;
    private const int MaxEdgeAttempts = 4;
    private static readonly TimeSpan FailedEdgeCooldown = TimeSpan.FromMinutes(10);

    private readonly ILogger<FoxyEngine> _logger;
    private readonly ISettingsService _settings;
    private readonly FxaClient _fxa;

    private GuardianClient? _guardian;
    private string _guardianEgress = "";
    private readonly object _guardianGate = new();

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim[] _sessionSwapLocks = { new(1, 1), new(1, 1) };
    private readonly FoxyH2Session?[] _sessions = new FoxyH2Session?[2];
    private readonly List<FoxyH2Session> _retiredSessions = new();

    private CancellationTokenSource? _connectCts;
    private int _attemptGeneration;

    private TcpListener? _socksListener;
    private TcpListener? _httpListener;

    private volatile string _currentProxyPassToken = "";
    private long _currentProxyPassExpiryEpochSeconds;
    private CancellationTokenSource? _renewalCts;

    private volatile bool _disposed;
    private long _bytesSent;
    private long _bytesReceived;
    private long _lastBytesNotifyTick;

    private readonly List<(string Host, DateTime FailedUtc)> _failedEdges = new();

    public static string? UpstreamProxyOverride { get; set; }

    public static bool PreferDualSessionPool { get; set; }

    private FoxyH2Session? PrimarySession => Volatile.Read(ref _sessions[0]);

    public FoxyEngine(ILogger<FoxyEngine> logger, ISettingsService settings)
    {
        _logger = logger;
        _settings = settings;
        _fxa = new FxaClient(logger, CustomProxyProvider);
    }

    public ConnectionMethod Method => ConnectionMethod.Foxy;

    public ConnectionState State { get; private set; }

    public int SocksProxyPort { get; private set; }
    public int HttpProxyPort { get; private set; }

    public string ClientRegion => "";
    public string ConnectedServerRegion { get; private set; } = "";
    public string CurrentRouteIp { get; private set; } = "";
    public string CurrentRouteSni { get; private set; } = "";

    public string ServerEndpointAddress
    {
        get
        {
            var primary = PrimarySession ?? Volatile.Read(ref _sessions[1]);
            var addr = primary?.RemoteAddress;
            if (addr is not null)
                return FormatAddress(addr);
            return primary?.EdgeHost ?? "";
        }
    }

    public IReadOnlyList<string> ServerEndpointAddresses
    {
        get
        {
            var result = new List<string>(4);
            for (var i = 0; i < _sessions.Length; i++)
            {
                var s = Volatile.Read(ref _sessions[i]);
                if (s is null) continue;
                var val = s.RemoteAddress is { } ip ? FormatAddress(ip) : s.EdgeHost;
                if (!string.IsNullOrEmpty(val) && !result.Contains(val, StringComparer.OrdinalIgnoreCase))
                    result.Add(val);
            }
            return result;
        }
    }

    private static string FormatAddress(IPAddress addr) =>
        (addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr).ToString();

    public IReadOnlyList<string> AvailableEgressRegions => Guardian.CachedCountries
        .Where(c => !string.IsNullOrEmpty(c.Code) && !c.Code.Equals("REC", StringComparison.OrdinalIgnoreCase))
        .Select(c => c.Code.ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public int ConnectProgressPercent { get; private set; }
    public string ConnectProgressText { get; private set; } = "";

    public IReadOnlyList<string> CoreProcessNames => Array.Empty<string>();

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Notice>? NoticeReceived;
    public event EventHandler<string>? LogLineAppended;
    public event EventHandler? BytesTransferredChanged;
    public event EventHandler? RouteChanged;
    public event EventHandler? ConnectProgressChanged;

    private GuardianClient Guardian
    {
        get
        {
            var upstream = ResolveUpstreamProxy();
            var egress = upstream is { Enabled: true } ? $"{upstream.Host}:{upstream.Port}" : "";
            lock (_guardianGate)
            {
                if (_guardian is not null && _guardianEgress == egress) return _guardian;

                var previous = _guardian;
                var seed = previous?.CachedCountries;
                _guardian = new GuardianClient(_logger, egress.Length > 0 ? egress : null, seed, CustomProxyProvider);
                _guardianEgress = egress;
                return _guardian;
            }
        }
    }

    private IWebProxy? CustomProxyProvider()
    {
        var upstream = ResolveUpstreamProxy();
        if (upstream is not { Enabled: true }) return null;

        var scheme = string.Equals(upstream.Type, "http", StringComparison.OrdinalIgnoreCase) ? "http" : "socks5";
        var proxy = new WebProxy($"{scheme}://{upstream.Host}:{upstream.Port}");
        if (!string.IsNullOrEmpty(upstream.Username))
            proxy.Credentials = new NetworkCredential(upstream.Username, upstream.Password ?? "");
        return proxy;
    }

    private UpstreamProxySettings? ResolveUpstreamProxy()
    {
        if (!string.IsNullOrWhiteSpace(UpstreamProxyOverride))
        {
            var parts = UpstreamProxyOverride.Split(':');
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 1080;
            return new UpstreamProxySettings(true, "socks5", parts[0], port);
        }

        var s = _settings.Settings;
        if (FoxyNetwork.IsDirectFoxyOnly(s) && s.FirefoxCustomProxyEnabled && !string.IsNullOrWhiteSpace(s.FirefoxCustomProxyHost))
        {
            return new UpstreamProxySettings(true, s.FirefoxCustomProxyType, s.FirefoxCustomProxyHost,
                s.FirefoxCustomProxyPort, s.FirefoxCustomProxyUsername, s.FirefoxCustomProxyPassword);
        }
        return null;
    }

    public async Task RefreshServerListAsync(CancellationToken ct = default)
    {
        try
        {
            await Guardian.FetchCountriesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Firefox server list refresh failed");
        }
    }

    public Task StartAsync()
    {
        if (State == ConnectionState.Connected) return Task.CompletedTask;

        var attempt = Interlocked.Increment(ref _attemptGeneration);
        try { _connectCts?.Cancel(); } catch { }
        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;

        SetState(ConnectionState.Connecting);
        _ = Task.Run(() => RunGatedAsync(() => StartCoreAsync(ct, attempt)));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try { _connectCts?.Cancel(); } catch { }
        await RunGatedAsync(StopCoreAsync).ConfigureAwait(false);
    }

    public void CancelConnecting()
    {
        try { _connectCts?.Cancel(); } catch { }
    }

    private bool IsSuperseded(int attempt) => Volatile.Read(ref _attemptGeneration) != attempt;

    private async Task StartCoreAsync(CancellationToken ct, int attempt)
    {
        SetProgress(10, "Initializing Firefox VPN...");
        try
        {
            var account = ResolveActiveAccount();
            if (account is null)
            {
                Log("No usable Firefox account. Add or re-add one in Settings -> Foxy.");
                RaiseNotice(new Notice { NoticeType = "error" });
                SetState(ConnectionState.Error);
                return;
            }

            SetProgress(30, "Signing in to Firefox Accounts (" + account.Email + ")...");
            var pass = await EnsureProxyPassWithFailoverAsync(account, ct).ConfigureAwait(false);
            if (pass is null)
            {
                Log("Could not obtain a proxy pass from Mozilla Guardian.");
                SetState(ConnectionState.Error);
                return;
            }

            SetProgress(50, "Resolving edge server...");
            var countries = await Guardian.FetchCountriesAsync(ct).ConfigureAwait(false);
            var candidates = GuardianClient.GetCandidates(countries,
                _settings.Settings.FirefoxServerCountry, _settings.Settings.FirefoxServerCity);
            if (candidates.Count == 0)
            {
                Log("No Firefox VPN edge matched the selected location.");
                SetState(ConnectionState.Error);
                return;
            }

            var ordered = OrderCandidates(candidates);
            SetProgress(60, $"Ranking {ordered.Count} Firefox VPN edge(s)...");

            var (session, chosen, lastError) = await DialEdgeSessionAsync(ordered, updateProgress: true, ct).ConfigureAwait(false);

            if (session is null || chosen is null)
            {
                var attempts = Math.Min(ordered.Count, MaxEdgeAttempts);
                Log(lastError is null
                    ? $"No Firefox VPN edge could be reached across {attempts} attempt(s)."
                    : $"All {attempts} Firefox VPN edge(s) failed. Last error: {lastError.Message}");
                await CleanupSessionAsync().ConfigureAwait(false);
                SetState(ConnectionState.Error);
                return;
            }

            _currentProxyPassToken = pass.Token;
            _currentProxyPassExpiryEpochSeconds = pass.ExpiresAtEpochSeconds;
            AttachActiveSession(session, chosen);
            RememberSuccessfulEdge(chosen);

            Log($"Connected to Firefox VPN edge {chosen.Authority} ({chosen.CountryName}).");
            SetProgress(85, "Starting local SOCKS5 & HTTP relays...");
            StartLocalSocksListener(ct);
            StartLocalHttpListener(ct);
            StartProxyPassRenewal(attempt, ct);

            SetProgress(95, "Verifying tunnel connectivity...");
            if (PreferDualSessionPool)
                _ = Task.Run(() => EnsureSecondarySessionAsync(ordered, attempt, ct), ct);
            _ = Task.Run(() => ProbeExitIpAsync(ct));

            SetProgress(100, "Connected to Firefox VPN");
            SetState(ConnectionState.Connected);
            Log("Firefox VPN HTTP/2 tunnel is up.");
        }
        catch (OperationCanceledException)
        {
            if (IsSuperseded(attempt))
            {
                Log("Firefox VPN connect attempt superseded by a newer attempt.");
                return;
            }
            Log("Firefox VPN connection cancelled.");
            await CleanupSessionAsync().ConfigureAwait(false);
            ResetRouteInfo();
            SetState(ConnectionState.Disconnected);
        }
        catch (Exception ex)
        {
            if (IsSuperseded(attempt))
            {
                Log("Firefox VPN connect attempt superseded by a newer attempt (" + ex.Message + ").");
                return;
            }
            Log("Firefox VPN connection failed: " + ex.Message);
            await CleanupSessionAsync().ConfigureAwait(false);
            ResetRouteInfo();
            SetState(ConnectionState.Error);
        }
    }

    private void AttachActiveSession(FoxyH2Session session, ProxyCandidate chosen, int slot = 0)
    {
        session.UpdateProxyPassToken(_currentProxyPassToken);
        session.SessionDraining += OnSessionDraining;
        session.SessionFailed += OnSessionFailed;
        Volatile.Write(ref _sessions[slot], session);

        if (slot == 0)
        {
            ConnectedServerRegion = chosen.CountryCode;
            CurrentRouteSni = chosen.Host;
            var remote = session.RemoteAddress?.ToString() ?? "";
            if (!string.IsNullOrEmpty(remote) && !remote.StartsWith("127.")) CurrentRouteIp = remote;
        }
        RaiseRouteChanged();
    }

    private async Task EnsureSecondarySessionAsync(List<ProxyCandidate> ordered, int attempt, CancellationToken ct)
    {
        await _sessionSwapLocks[1].WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsSuperseded(attempt) || _disposed || Volatile.Read(ref _sessions[1]) is { IsConnected: true })
                return;

            var primary = PrimarySession;
            var avoidAddr = primary?.RemoteAddress;
            var secondaryCandidates = ordered.Count > 1 && primary is not null
                ? ordered.Where(c => !string.Equals(c.Host, primary.EdgeHost, StringComparison.OrdinalIgnoreCase))
                    .Concat(ordered.Where(c => string.Equals(c.Host, primary.EdgeHost, StringComparison.OrdinalIgnoreCase)))
                    .ToList()
                : ordered;

            var (secondary, chosen, _) = await DialEdgeSessionAsync(
                secondaryCandidates, updateProgress: false, ct, avoidAddr).ConfigureAwait(false);

            if (secondary is null || chosen is null) return;
            if (IsSuperseded(attempt) || _disposed || State is ConnectionState.Disconnected or ConnectionState.Error)
            {
                await secondary.DisposeAsync().ConfigureAwait(false);
                return;
            }

            AttachActiveSession(secondary, chosen, slot: 1);
            Log($"Opened secondary Firefox VPN HTTP/2 pool session to {chosen.Authority} ({secondary.RemoteAddress}).");
            _ = Task.Run(() => RunDualPoolStaggerLoopAsync(ordered, attempt, ct), ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Secondary Firefox VPN pool session dial skipped: {Message}", ex.Message);
        }
        finally
        {
            _sessionSwapLocks[1].Release();
        }
    }

    private async Task RunDualPoolStaggerLoopAsync(List<ProxyCandidate> ordered, int attempt, CancellationToken ct)
    {
        var slot = 1;
        while (!ct.IsCancellationRequested && !_disposed && !IsSuperseded(attempt))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(4), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_disposed || IsSuperseded(attempt) || State != ConnectionState.Connected || !PreferDualSessionPool)
                break;

            var slotLock = _sessionSwapLocks[slot];
            await slotLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed || IsSuperseded(attempt) || State != ConnectionState.Connected || !PreferDualSessionPool)
                    break;

                var current = Volatile.Read(ref _sessions[slot]);
                var avoidAddr = Volatile.Read(ref _sessions[1 - slot])?.RemoteAddress;
                var candidates = RefreshCandidatesOrFallback(ordered);
                var (fresh, chosen, _) = await DialEdgeSessionAsync(
                    candidates, updateProgress: false, ct, avoidAddr).ConfigureAwait(false);

                if (fresh is null || chosen is null) continue;
                if (_disposed || IsSuperseded(attempt) || State != ConnectionState.Connected)
                {
                    await fresh.DisposeAsync().ConfigureAwait(false);
                    break;
                }

                AttachActiveSession(fresh, chosen, slot);
                RetireSession(current);
                Log($"Staggered background refresh swapped HTTP/2 pool slot {slot} ({chosen.Authority}).");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _disposed)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Staggered dual-pool refresh failed for slot {Slot}: {Message}", slot, ex.Message);
            }
            finally
            {
                slotLock.Release();
            }

            slot = 1 - slot;
        }
    }

    private List<ProxyCandidate> RefreshCandidatesOrFallback(List<ProxyCandidate> fallback)
    {
        try
        {
            var countries = Guardian.CachedCountries;
            if (countries is { Count: > 0 })
            {
                var candidates = GuardianClient.GetCandidates(countries,
                    _settings.Settings.FirefoxServerCountry, _settings.Settings.FirefoxServerCity);
                if (candidates.Count > 0) return OrderCandidates(candidates);
            }
        }
        catch
        {
        }
        return fallback;
    }

    private void RetireSession(FoxyH2Session? oldSession)
    {
        if (oldSession is null) return;
        oldSession.SessionDraining -= OnSessionDraining;
        oldSession.SessionFailed -= OnSessionFailed;

        void CleanupRetired(object? sender, string reason)
        {
            oldSession.SessionFailed -= CleanupRetired;
            lock (_retiredSessions) _retiredSessions.Remove(oldSession);
            _ = Task.Run(async () =>
            {
                try { await oldSession.DisposeAsync().ConfigureAwait(false); } catch {  }
            });
        }

        if (!oldSession.IsConnected)
        {
            CleanupRetired(null, "already disconnected");
            return;
        }

        oldSession.SessionFailed += CleanupRetired;
        lock (_retiredSessions) _retiredSessions.Add(oldSession);
        if (!oldSession.IsConnected) CleanupRetired(null, "disconnected during retire");
    }

    private async Task<(FoxyH2Session? Session, ProxyCandidate? Chosen, Exception? LastError)> DialEdgeSessionAsync(
        IReadOnlyList<ProxyCandidate> ordered, bool updateProgress, CancellationToken ct, IPAddress? avoidAddress = null)
    {
        FoxyH2Session? session = null;
        ProxyCandidate? chosen = null;
        Exception? lastError = null;
        var attempts = Math.Min(ordered.Count, MaxEdgeAttempts);

        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = ordered[i];
            try
            {
                if (updateProgress)
                    SetProgress(60 + i * 30 / Math.Max(1, attempts),
                        "Connecting to Fastly edge (" + candidate.Host + ")...");
                Log($"Trying Firefox VPN edge {i + 1}/{attempts}: {candidate.Authority} ({candidate.CountryName})");

                var edge = new FoxyH2Session(candidate.Host, candidate.Port, ResolveUpstreamProxy(), _logger, avoidAddress);
                await edge.ConnectAsync(ct).ConfigureAwait(false);
                session = edge;
                chosen = candidate;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "Firefox VPN edge {Host}:{Port} failed", candidate.Host, candidate.Port);
                Log("Edge " + candidate.Authority + " failed: " + ex.Message);
            }
        }

        if (session is null && !ordered.Any(c =>
                string.Equals(c.Host, "p.m1.fastly-masque.net", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Log("Location edges failed; trying the global anycast edge p.m1.fastly-masque.net:2499...");
                var edge = new FoxyH2Session("p.m1.fastly-masque.net", 2499, ResolveUpstreamProxy(), _logger, avoidAddress);
                await edge.ConnectAsync(ct).ConfigureAwait(false);
                session = edge;
                chosen = new ProxyCandidate("p.m1.fastly-masque.net", 2499, "REC", "Global Anycast", "REC");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "Fallback anycast edge failed: {Message}", ex.Message);
                Log("Fallback anycast edge failed: " + ex.Message);
            }
        }

        return (session, chosen, lastError);
    }

    private int FindSessionSlot(FoxyH2Session session)
    {
        for (var i = 0; i < _sessions.Length; i++)
        {
            if (ReferenceEquals(session, Volatile.Read(ref _sessions[i])))
                return i;
        }
        return -1;
    }

    private bool HasOtherHealthySession(int slot, bool requireActiveStreams = false)
    {
        for (var i = 0; i < _sessions.Length; i++)
        {
            if (i == slot) continue;
            var peer = Volatile.Read(ref _sessions[i]);
            if (peer is { IsConnected: true } && (!requireActiveStreams || peer.ActiveStreamCount > 0))
                return true;
        }
        return false;
    }

    private void OnSessionDraining(object? sender, string reason)
    {
        if (sender is not FoxyH2Session session || FindSessionSlot(session) < 0
            || State != ConnectionState.Connected || _disposed) return;

        Log("Firefox VPN edge initiated graceful drain (" + reason + "); opening replacement HTTP/2 session in place...");
        var attempt = Volatile.Read(ref _attemptGeneration);
        var ct = _connectCts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => ReplaceSessionInPlaceAsync(session, reason, isFailure: false, attempt, ct));
    }

    private void OnSessionFailed(object? sender, string reason)
    {
        if (sender is not FoxyH2Session session || FindSessionSlot(session) < 0
            || State != ConnectionState.Connected || _disposed) return;

        Log("Firefox VPN HTTP/2 session dropped (" + reason + "); attempting seamless in-place replacement...");
        var attempt = Volatile.Read(ref _attemptGeneration);
        var ct = _connectCts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => ReplaceSessionInPlaceAsync(session, reason, isFailure: true, attempt, ct));
    }

    private async Task ReplaceSessionInPlaceAsync(
        FoxyH2Session targetSession, string reason, bool isFailure, int attempt, CancellationToken ct)
    {
        var slot = FindSessionSlot(targetSession);
        if (slot < 0) return;

        var slotLock = _sessionSwapLocks[slot];
        await slotLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            slot = FindSessionSlot(targetSession);
            if (slot < 0 || IsSuperseded(attempt) || _disposed || State != ConnectionState.Connected) return;

            if (isFailure)
            {
                NoteEdgeFailed(targetSession.EdgeHost);
                if (string.Equals(_settings.Settings.FirefoxLastEdgeHost, targetSession.EdgeHost, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.Settings.FirefoxLastEdgeHost = "";
                    _settings.Settings.FirefoxLastEdgePort = 0;
                    _settings.Save();
                }
            }

            var account = ResolveActiveAccount();
            if (account is not null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= _currentProxyPassExpiryEpochSeconds - 30)
            {
                var freshPass = await EnsureProxyPassWithFailoverAsync(account, ct).ConfigureAwait(false);
                if (freshPass is not null)
                {
                    _currentProxyPassToken = freshPass.Token;
                    _currentProxyPassExpiryEpochSeconds = freshPass.ExpiresAtEpochSeconds;
                    UpdateAllSessionTokens(freshPass.Token);
                }
            }

            var countries = await Guardian.FetchCountriesAsync(ct).ConfigureAwait(false);
            var candidates = GuardianClient.GetCandidates(countries,
                _settings.Settings.FirefoxServerCountry, _settings.Settings.FirefoxServerCity);
            if (candidates.Count == 0)
            {
                candidates = new List<ProxyCandidate>
                {
                    new(targetSession.EdgeHost, 2499, ConnectedServerRegion, targetSession.EdgeHost, ConnectedServerRegion)
                };
            }

            var ordered = OrderCandidates(candidates);
            var avoidAddr = Volatile.Read(ref _sessions[1 - slot])?.RemoteAddress;
            var (replacement, chosen, lastError) = await DialEdgeSessionAsync(
                ordered, updateProgress: false, ct, avoidAddr).ConfigureAwait(false);

            if (IsSuperseded(attempt) || _disposed || State != ConnectionState.Connected)
            {
                if (replacement is not null) await replacement.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (replacement is null || chosen is null)
            {
                Log($"In-place HTTP/2 session replacement failed (slot {slot})"
                    + (lastError is null ? "." : $": {lastError.Message}"));
                if (HasOtherHealthySession(slot))
                {
                    Volatile.Write(ref _sessions[slot], null);
                    RetireSession(targetSession);
                    return;
                }
                SetState(ConnectionState.Error);
                return;
            }

            AttachActiveSession(replacement, chosen, slot);
            RememberSuccessfulEdge(chosen);
            RetireSession(targetSession);
            Log($"Replaced Firefox VPN HTTP/2 session in place (slot {slot}, {chosen.Authority}); local proxies remain up.");

            if (!isFailure && targetSession.IsConnected && targetSession.ActiveStreamCount > 0)
                _ = Task.Run(() => StaggerStreamMigrationAsync(targetSession, replacement, slot, attempt, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _disposed)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "In-place HTTP/2 session replacement errored: {Message}", ex.Message);
            if (!IsSuperseded(attempt) && !_disposed && State == ConnectionState.Connected
                && FindSessionSlot(targetSession) >= 0 && !HasOtherHealthySession(slot))
            {
                SetState(ConnectionState.Error);
            }
        }
        finally
        {
            slotLock.Release();
        }
    }

    private async Task StaggerStreamMigrationAsync(
        FoxyH2Session drainingSession, FoxyH2Session replacement, int slot, int attempt, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
            if (IsSuperseded(attempt) || _disposed || !drainingSession.IsConnected || !replacement.CanOpenNewStreams)
                return;

            var openCount = drainingSession.ActiveStreamCount;
            if (openCount <= 0) return;

            if (openCount > 1)
            {
                drainingSession.CloseOldestStreams(1);
                await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                if (!IsSuperseded(attempt) && !_disposed && drainingSession.IsConnected && replacement.CanOpenNewStreams)
                    drainingSession.CloseOldestStreams(drainingSession.ActiveStreamCount);
            }
            else if (HasOtherHealthySession(slot, requireActiveStreams: true))
            {
                drainingSession.CloseOldestStreams(1);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void NoteEdgeFailed(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        lock (_failedEdges)
        {
            _failedEdges.RemoveAll(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase));
            _failedEdges.Insert(0, (host, DateTime.UtcNow));
            if (_failedEdges.Count > MaxRememberedFailedEdges) _failedEdges.RemoveAt(_failedEdges.Count - 1);
        }
        Log("Edge " + host + " marked as failed; the next attempt will prefer a different node.");
    }

    private string[] FailedEdgesSnapshot()
    {
        lock (_failedEdges)
        {
            _failedEdges.RemoveAll(e => DateTime.UtcNow - e.FailedUtc > FailedEdgeCooldown);
            return _failedEdges.Select(e => e.Host).ToArray();
        }
    }

    private void ResetRouteInfo()
    {
        ConnectedServerRegion = _settings.Settings.FirefoxServerCountry ?? "";
        CurrentRouteIp = "";
        CurrentRouteSni = PrimarySession?.EdgeHost ?? "";
        RaiseRouteChanged();
    }

    private void RememberSuccessfulEdge(ProxyCandidate candidate)
    {
        var s = _settings.Settings;
        if (string.Equals(s.FirefoxLastEdgeHost, candidate.Host, StringComparison.OrdinalIgnoreCase)
            && s.FirefoxLastEdgePort == candidate.Port) return;

        s.FirefoxLastEdgeHost = candidate.Host;
        s.FirefoxLastEdgePort = candidate.Port;
        _settings.SaveQuietly();
    }

    private List<ProxyCandidate> OrderCandidates(List<ProxyCandidate> candidates)
    {
        var avoid = FailedEdgesSnapshot();
        var ordered = GuardianClient.OrderCandidates(candidates,
            _settings.Settings.FirefoxLastEdgeHost, _settings.Settings.FirefoxLastEdgePort, avoid);
        if (avoid.Length != 0)
            Log($"Edge order: {string.Join(", ", ordered.Take(4).Select(c => c.Host))}  "
                + $"({avoid.Length} demoted: {string.Join(", ", avoid)})");
        return ordered;
    }

    private async Task StopCoreAsync()
    {
        try { _connectCts?.Cancel(); } catch { }
        await CleanupSessionAsync().ConfigureAwait(false);
        ResetRouteInfo();
        SetState(ConnectionState.Disconnected);
        Log("Firefox VPN disconnected.");
    }

    private FirefoxAccountInfo? ResolveActiveAccount()
    {
        var accounts = _settings.Settings.FirefoxAccounts.ToArray();
        if (accounts.Length == 0) return null;

        var activeId = _settings.Settings.FirefoxActiveAccountId;
        var account = accounts.FirstOrDefault(a => a.Id == activeId) ?? accounts[0];
        if (account.HasUsableCredentials) return account;

        Log("The stored credentials for " + account.Email
            + " could not be read on this machine. Remove the account and sign in again.");
        return null;
    }

    private async Task<ProxyPassResult?> EnsureProxyPassWithFailoverAsync(FirefoxAccountInfo account, CancellationToken ct)
    {
        var accounts = _settings.Settings.FirefoxAccounts.ToArray();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { account.Id };
        var current = account;

        for (var round = 1; round <= 3; round++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (current.TokenExpiryEpochSeconds <= now + 300)
                {
                    Log("Access token for " + current.Email + " is expiring; refreshing...");
                    if (!await _fxa.RefreshAccessTokenAsync(current, ct, () => _settings.Save())
                            .ConfigureAwait(false))
                    {
                        Log("Could not refresh the access token for " + current.Email + "; sign in again.");
                        break;
                    }
                }

                var pass = await Guardian.FetchProxyPassAsync(current.AccessToken, ct).ConfigureAwait(false);
                ApplyProxyPass(current, pass);
                Log("Active account " + current.Email + ": " + current.QuotaRemainingDisplay
                    + " (" + current.QuotaUsedDisplay + ")");
                return pass;
            }
            catch (FirefoxQuotaExceededException)
            {
                Log("Monthly quota exhausted for account " + current.Email + ". Looking for another account...");
                current.QuotaRemainingBytes = 0;
                _settings.Save();

                var next = accounts.FirstOrDefault(a => !tried.Contains(a.Id) && a.QuotaRemainingBytes > 0);
                if (next is null)
                {
                    Log("Every Firefox account has run out of quota.");
                    break;
                }

                Log("Switching to alternate account: " + next.Email);
                _settings.Settings.FirefoxActiveAccountId = next.Id;
                _settings.Save();
                tried.Add(next.Id);
                current = next;
            }
            catch (FirefoxAuthTokenInvalidException)
            {
                Log("Access token rejected for " + current.Email + "; attempting a refresh...");
                if (!await _fxa.RefreshAccessTokenAsync(current, ct).ConfigureAwait(false)) break;
                _settings.Save();
            }
        }

        return null;
    }

    private async Task<string?> RefreshProxyPassAsync(CancellationToken ct)
    {
        var account = ResolveActiveAccount();
        if (account is null) return null;

        try
        {
            var pass = await EnsureProxyPassWithFailoverAsync(account, ct).ConfigureAwait(false);
            if (pass is null) return null;

            _currentProxyPassToken = pass.Token;
            _currentProxyPassExpiryEpochSeconds = pass.ExpiresAtEpochSeconds;
            UpdateAllSessionTokens(pass.Token);
            Log("Proxy pass refreshed after the edge rejected the previous one.");
            return pass.Token;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh the proxy pass: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<Stream?> OpenConnectWithPassRetryAsync(
        FoxyH2Session session, string host, int port, CancellationToken ct)
    {
        for (var swapHop = 0; ; swapHop++)
        {
            if (!session.CanOpenNewStreams && swapHop == 0)
            {
                session = await AwaitUsableSessionAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("HTTP/2 session stopped accepting streams.");
                continue;
            }

            try
            {
                return await session.OpenConnectStreamAsync(host, port, _currentProxyPassToken, ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (!session.CanOpenNewStreams && swapHop == 0)
            {
                var freshSession = await AwaitUsableSessionAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                if (freshSession is null || ReferenceEquals(freshSession, session)) throw;
                session = freshSession;
            }
            catch (FirefoxAuthTokenInvalidException)
            {
                break;
            }
        }

        var refreshed = await RefreshProxyPassAsync(ct).ConfigureAwait(false);
        if (refreshed is null) return null;

        try
        {
            return await session.OpenConnectStreamAsync(host, port, refreshed, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FirefoxQuotaExceededException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Retry with the refreshed proxy pass failed: {Message}", ex.Message);
            return null;
        }
    }

    private void NotifyIfCurrentSessionDead(FoxyH2Session session, string reason)
    {
        if (!session.IsConnected && FindSessionSlot(session) >= 0)
            session.RaiseFailed(reason);
    }

    private void UpdateAllSessionTokens(string token)
    {
        for (var i = 0; i < _sessions.Length; i++)
            Volatile.Read(ref _sessions[i])?.UpdateProxyPassToken(token);
    }

    private void ApplyProxyPass(FirefoxAccountInfo account, ProxyPassResult pass)
    {
        var changed = account.ProxyPassToken != pass.Token
                      || account.ProxyPassExpiryEpochSeconds != pass.ExpiresAtEpochSeconds
                      || (pass.QuotaLimit.HasValue && account.QuotaLimitBytes != pass.QuotaLimit.Value)
                      || (pass.QuotaRemaining.HasValue && account.QuotaRemainingBytes != pass.QuotaRemaining.Value)
                      || (pass.QuotaReset.HasValue && account.QuotaResetEpochSeconds != pass.QuotaReset.Value);

        account.ProxyPassToken = pass.Token;
        account.ProxyPassExpiryEpochSeconds = pass.ExpiresAtEpochSeconds;
        if (pass.QuotaLimit.HasValue) account.QuotaLimitBytes = pass.QuotaLimit.Value;
        if (pass.QuotaRemaining.HasValue) account.QuotaRemainingBytes = pass.QuotaRemaining.Value;
        if (pass.QuotaReset.HasValue) account.QuotaResetEpochSeconds = pass.QuotaReset.Value;
        account.LastUsedAtUtc = DateTime.UtcNow;
        account.LastQuotaCheckUtc = DateTime.UtcNow;
        account.QuotaErrorMessage = "";

        if (changed) _settings.SaveQuietly();
    }

    private void StartLocalSocksListener(CancellationToken ct)
    {
        StopLocalSocksListener();
        var s = _settings.Settings;
        var requested = s.UseCustomProxyPorts && s.LocalSocksProxyPort > 0 ? s.LocalSocksProxyPort : DefaultSocksPort;

        try
        {
            _socksListener = new TcpListener(IPAddress.Loopback, requested);
            _socksListener.Start();
            SocksProxyPort = requested;
        }
        catch
        {
            _socksListener = new TcpListener(IPAddress.Loopback, 0);
            _socksListener.Start();
            SocksProxyPort = ((IPEndPoint)_socksListener.LocalEndpoint).Port;
        }

        Log($"Local SOCKS5 proxy listening on 127.0.0.1:{SocksProxyPort}");
        _ = Task.Run(() => AcceptSocksClientsAsync(_socksListener, ct));
    }

    private async Task AcceptSocksClientsAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => HandleSocksClientAsync(client, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_disposed) break;
                _logger.LogDebug(ex, "Error accepting SOCKS client: {Message}", ex.Message);
                try
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleSocksClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var greeting = new byte[2];
                await ReadExactAsync(stream, greeting, 2, ct).ConfigureAwait(false);
                if (greeting[0] != 5) return;

                var methods = new byte[greeting[1]];
                await ReadExactAsync(stream, methods, methods.Length, ct).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 5, 0 }, ct).ConfigureAwait(false);

                var header = new byte[4];
                await ReadExactAsync(stream, header, 4, ct).ConfigureAwait(false);
                if (header[0] != 5 || header[1] != 1)
                {
                    await stream.WriteAsync(SocksReply(7), ct).ConfigureAwait(false);
                    return;
                }

                string targetHost;
                switch (header[3])
                {
                    case 1:
                    {
                        var ip = new byte[4];
                        await ReadExactAsync(stream, ip, 4, ct).ConfigureAwait(false);
                        targetHost = new IPAddress(ip).ToString();
                        break;
                    }
                    case 3:
                    {
                        var length = await ReadByteAsync(stream, ct).ConfigureAwait(false);
                        if (length <= 0) return;
                        var name = new byte[length];
                        await ReadExactAsync(stream, name, length, ct).ConfigureAwait(false);
                        targetHost = Encoding.ASCII.GetString(name);
                        break;
                    }
                    case 4:
                    {
                        var ip = new byte[16];
                        await ReadExactAsync(stream, ip, 16, ct).ConfigureAwait(false);
                        targetHost = new IPAddress(ip).ToString();
                        break;
                    }
                    default:
                        await stream.WriteAsync(SocksReply(8), ct).ConfigureAwait(false);
                        return;
                }

                var portBytes = new byte[2];
                await ReadExactAsync(stream, portBytes, 2, ct).ConfigureAwait(false);
                var targetPort = (portBytes[0] << 8) | portBytes[1];

                var session = await AwaitUsableSessionAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (session is null)
                {
                    await stream.WriteAsync(SocksReply(5), ct).ConfigureAwait(false);
                    return;
                }

                Stream? tunnel;
                try
                {
                try
                {
                    tunnel = await OpenConnectWithPassRetryAsync(session, targetHost, targetPort, ct)
                        .ConfigureAwait(false);
                }
                catch (FirefoxTargetRejectedException ex)
                {
                    _logger.LogDebug("Edge rejected tunnel to {Host}:{Port} ({Message}); keeping session open.", targetHost, targetPort, ex.Message);
                    await stream.WriteAsync(SocksReply(2), ct).ConfigureAwait(false);
                    return;
                }
                }
                catch (FirefoxQuotaExceededException)
                {
                    _logger.LogWarning("Account quota exceeded while opening a stream.");
                    await stream.WriteAsync(SocksReply(4), ct).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Tunnel stream to {Host}:{Port} failed: {Message}", targetHost, targetPort,
                        ex.Message);
                    NotifyIfCurrentSessionDead(session, "HTTP/2 session stopped accepting streams.");
                    await stream.WriteAsync(SocksReply(4), ct).ConfigureAwait(false);
                    return;
                }

                if (tunnel is null)
                {
                    _logger.LogWarning("Proxy pass rejected by the edge; reconnecting is required.");
                    session.RaiseFailed("edge rejected the proxy pass authorization");
                    await stream.WriteAsync(SocksReply(4), ct).ConfigureAwait(false);
                    return;
                }

                await stream.WriteAsync(SocksReply(0), ct).ConfigureAwait(false);
                await using (tunnel)
                {
                    await RelayAsync(stream, tunnel, ct).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }
    }

    private static byte[] SocksReply(byte code) => new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 };

    private void StartLocalHttpListener(CancellationToken ct)
    {
        StopLocalHttpListener();
        var s = _settings.Settings;
        var requested = s.UseCustomProxyPorts && s.LocalHttpProxyPort > 0 ? s.LocalHttpProxyPort : DefaultHttpPort;

        try
        {
            _httpListener = new TcpListener(IPAddress.Loopback, requested);
            _httpListener.Start();
            HttpProxyPort = requested;
        }
        catch
        {
            _httpListener = new TcpListener(IPAddress.Loopback, 0);
            _httpListener.Start();
            HttpProxyPort = ((IPEndPoint)_httpListener.LocalEndpoint).Port;
        }

        Log($"Local HTTP proxy listening on 127.0.0.1:{HttpProxyPort}");
        _ = Task.Run(() => AcceptHttpClientsAsync(_httpListener, ct));
    }

    private async Task AcceptHttpClientsAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => HandleHttpClientAsync(client, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_disposed) break;
                _logger.LogDebug(ex, "Error accepting HTTP client: {Message}", ex.Message);
                try
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleHttpClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headerCts.CancelAfter(TimeSpan.FromSeconds(15));

                var buffer = ArrayPool<byte>.Shared.Rent(32768);
                try
                {
                    var total = 0;
                    var headerEnd = -1;
                    while (total < 8192)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(total, 8192 - total), headerCts.Token)
                            .ConfigureAwait(false);
                        if (read <= 0) return;
                        total += read;

                        for (var i = 3; i < total; i++)
                        {
                            if (buffer[i - 3] == 13 && buffer[i - 2] == 10 && buffer[i - 1] == 13 && buffer[i] == 10)
                            {
                                headerEnd = i + 1;
                                break;
                            }
                        }
                        if (headerEnd >= 0) break;
                    }
                    if (headerEnd < 0) return;

                    var head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
                    var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
                    if (lineEnd < 0) return;

                    var requestLine = head.Substring(0, lineEnd).Trim().Split(' ', 3);
                    if (requestLine.Length < 2) return;

                    var verb = requestLine[0];
                    var target = requestLine[1];

                    if (string.Equals(verb, "CONNECT", StringComparison.OrdinalIgnoreCase))
                    {
                        SplitHostPort(target, 443, out var host, out var port);
                        var tunnel = await OpenTunnelAsync(host, port, stream, ct).ConfigureAwait(false);
                        if (tunnel is null) return;

                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct)
                            .ConfigureAwait(false);

                        var leftover = total - headerEnd;
                        if (leftover > 0)
                            await tunnel.WriteAsync(buffer.AsMemory(headerEnd, leftover), ct).ConfigureAwait(false);

                        await using (tunnel)
                        {
                            await RelayAsync(stream, tunnel, ct).ConfigureAwait(false);
                        }
                        return;
                    }

                    string targetHost;
                    int targetPort;
                    if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
                    {
                        targetHost = uri.Host;
                        targetPort = uri.Port > 0 ? uri.Port : 80;
                    }
                    else
                    {
                        targetHost = "";
                        targetPort = 80;
                        foreach (var line in head.Split("\r\n"))
                        {
                            if (!line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) continue;
                            SplitHostPort(line[5..].Trim(), 80, out targetHost, out targetPort);
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(targetHost)) return;

                    var plain = await OpenTunnelAsync(targetHost, targetPort, stream, ct).ConfigureAwait(false);
                    if (plain is null) return;

                    await plain.WriteAsync(buffer.AsMemory(0, total), ct).ConfigureAwait(false);
                    await using (plain)
                    {
                        await RelayAsync(stream, plain, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch
            {
            }
        }
    }

    private async Task<Stream?> OpenTunnelAsync(string host, int port, Stream client, CancellationToken ct)
    {
        var session = await AwaitUsableSessionAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (session is null)
        {
            await WriteHttpErrorAsync(client, "502 Bad Gateway", ct).ConfigureAwait(false);
            return null;
        }

        Stream? tunnel;
        try
        {
            tunnel = await OpenConnectWithPassRetryAsync(session, host, port, ct).ConfigureAwait(false);
        }
        catch (FirefoxQuotaExceededException)
        {
            await WriteHttpErrorAsync(client, "502 Bad Gateway", ct).ConfigureAwait(false);
            return null;
        }
        catch (FirefoxTargetRejectedException ex)
        {
            _logger.LogDebug("Edge rejected HTTP CONNECT to {Host}:{Port} ({Message}); keeping session open.", host, port, ex.Message);
            await WriteHttpErrorAsync(client, "403 Forbidden", ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to open a stream to {Host}:{Port}: {Message}", host, port, ex.Message);
            NotifyIfCurrentSessionDead(session, "HTTP/2 session stopped accepting streams.");
            await WriteHttpErrorAsync(client, "504 Gateway Timeout", ct).ConfigureAwait(false);
            return null;
        }

        if (tunnel is null)
        {
            _logger.LogWarning("Proxy pass rejected on {Host}:{Port}; reconnect required.", host, port);
            session.RaiseFailed("edge rejected the proxy pass authorization");
            await WriteHttpErrorAsync(client, "502 Bad Gateway", ct).ConfigureAwait(false);
        }

        return tunnel;
    }

    private static Task WriteHttpErrorAsync(Stream stream, string status, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\n\r\n"), ct).AsTask();

    private static void SplitHostPort(string value, int defaultPort, out string host, out int port)
    {
        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out var parsed))
        {
            host = value[..colon].Trim('[', ']');
            port = parsed;
        }
        else
        {
            host = value.Trim('[', ']');
            port = defaultPort;
        }
    }

    private void StartProxyPassRenewal(int attempt, CancellationToken ct)
    {
        _renewalCts?.Cancel();
        _renewalCts?.Dispose();
        _renewalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _renewalCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                var delay = CalculateRenewalDelay(_currentProxyPassExpiryEpochSeconds);
                _logger.LogInformation("Proxy pass renewal scheduled in {Seconds}s.", (int)delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (IsSuperseded(attempt) || State != ConnectionState.Connected) break;

                var account = ResolveActiveAccount();
                if (account is null) break;

                try
                {
                    var pass = await EnsureProxyPassWithFailoverAsync(account, token).ConfigureAwait(false);
                    if (pass is not null)
                    {
                        _currentProxyPassToken = pass.Token;
                        _currentProxyPassExpiryEpochSeconds = pass.ExpiresAtEpochSeconds;
                        UpdateAllSessionTokens(pass.Token);
                        var valid = pass.ExpiresAtEpochSeconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        Log($"Proxy pass renewed in place (valid for {valid}s); the tunnel keeps running.");
                    }
                    else
                    {
                        Log("Background proxy pass renewal returned nothing; retrying in 30s.");
                        await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background proxy pass renewal failed: {Message}", ex.Message);
                    Log("Background proxy pass renewal failed (" + ex.Message + "); retrying in 30s...");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, token);
    }

    private static TimeSpan CalculateRenewalDelay(long expiresAtEpochSeconds)
    {
        var remaining = expiresAtEpochSeconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remaining <= 0) return TimeSpan.FromSeconds(15);
        var half = (long)(remaining * 0.5);
        var beforeExpiry = remaining - 30;
        return TimeSpan.FromSeconds(Math.Clamp(Math.Min(half, beforeExpiry), 15, 1800));
    }

    private FoxyH2Session? SelectUsableSession()
    {
        FoxyH2Session? best = null;
        var bestCount = int.MaxValue;
        for (var i = 0; i < _sessions.Length; i++)
        {
            var s = Volatile.Read(ref _sessions[i]);
            if (s is not { CanOpenNewStreams: true }) continue;
            var count = s.ActiveStreamCount;
            if (count < bestCount)
            {
                best = s;
                bestCount = count;
            }
        }
        return best;
    }

    private async Task<FoxyH2Session?> AwaitUsableSessionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var session = SelectUsableSession();
        if (session is not null) return session;

        var deadline = DateTime.UtcNow + timeout;
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            session = SelectUsableSession();
            if (session is not null) return session;
            if (State == ConnectionState.Disconnected) break;
        }
        return null;
    }

    private async Task RelayAsync(Stream client, Stream tunnel, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Task.WhenAll(
                PumpAsync(client, tunnel, linked, sent: true),
                PumpAsync(tunnel, client, linked, sent: false)).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await FoxyH2Stream.SendEndStreamIfHttp2Async(tunnel, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task PumpAsync(Stream from, Stream to, CancellationTokenSource linked, bool sent)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var read = await from.ReadAsync(buffer.AsMemory(0, 32768), linked.Token).ConfigureAwait(false);
                if (read <= 0) break;

                await to.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                if (sent) Interlocked.Add(ref _bytesSent, read);
                else Interlocked.Add(ref _bytesReceived, read);
                MaybeNotifyBytesTransferred();
            }
        }
        catch
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try
            {
                await to.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
            try
            {
                linked.Cancel();
            }
            catch
            {
            }
        }
    }

    private async Task ProbeExitIpAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct).ConfigureAwait(false);

            var proxies = new List<WebProxy>();
            if (HttpProxyPort > 0) proxies.Add(new WebProxy($"http://127.0.0.1:{HttpProxyPort}"));
            if (SocksProxyPort > 0) proxies.Add(new WebProxy($"socks5://127.0.0.1:{SocksProxyPort}"));

            foreach (var proxy in proxies)
            {
                try
                {
                    using var handler = new SocketsHttpHandler
                    {
                        Proxy = proxy,
                        ConnectTimeout = TimeSpan.FromSeconds(8),
                    };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

                    try
                    {
                        var trace = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", ct)
                            .ConfigureAwait(false);
                        string? ip = null;
                        string? loc = null;
                        foreach (var raw in trace.Split('\n'))
                        {
                            var line = raw.Trim();
                            if (line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase)) ip = line[3..].Trim();
                            else if (line.StartsWith("loc=", StringComparison.OrdinalIgnoreCase)) loc = line[4..].Trim();
                        }

                        if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _))
                        {
                            CurrentRouteIp = ip;
                            if (!string.IsNullOrEmpty(loc)) ConnectedServerRegion = loc.ToUpperInvariant();
                            RaiseRouteChanged();
                            Log($"Detected Firefox VPN exit IP: {ip} ({ConnectedServerRegion})");
                            return;
                        }
                    }
                    catch
                    {
                    }

                    foreach (var url in new[]
                             { "https://api.ipify.org", "https://icanhazip.com", "https://ifconfig.me/ip" })
                    {
                        try
                        {
                            var text = (await client.GetStringAsync(url, ct).ConfigureAwait(false)).Trim();
                            if (!IPAddress.TryParse(text, out _)) continue;
                            CurrentRouteIp = text;
                            RaiseRouteChanged();
                            Log("Detected Firefox VPN exit IP: " + text);
                            return;
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        var b = new byte[1];
        await ReadExactAsync(stream, b, 1, ct).ConfigureAwait(false);
        return b[0];
    }

    private void StopLocalHttpListener()
    {
        if (_httpListener is not null)
        {
            try
            {
                _httpListener.Stop();
            }
            catch
            {
            }
            _httpListener = null;
        }
        HttpProxyPort = 0;
    }

    private void StopLocalSocksListener()
    {
        if (_socksListener is not null)
        {
            try
            {
                _socksListener.Stop();
            }
            catch
            {
            }
            _socksListener = null;
        }
        SocksProxyPort = 0;
    }

    private void StopLocalListeners()
    {
        StopLocalHttpListener();
        StopLocalSocksListener();
    }

    private async Task CleanupSessionAsync()
    {
        _renewalCts?.Cancel();
        _renewalCts?.Dispose();
        _renewalCts = null;

        StopLocalListeners();

        for (var i = 0; i < _sessions.Length; i++)
        {
            var session = Interlocked.Exchange(ref _sessions[i], null);
            if (session is null) continue;
            session.SessionDraining -= OnSessionDraining;
            session.SessionFailed -= OnSessionFailed;
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        FoxyH2Session[] retired;
        lock (_retiredSessions)
        {
            retired = _retiredSessions.ToArray();
            _retiredSessions.Clear();
        }
        foreach (var stale in retired)
        {
            try
            {
                await stale.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private void SetState(ConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void SetProgress(int percent, string text)
    {
        ConnectProgressPercent = Math.Clamp(percent, 0, 100);
        ConnectProgressText = text;
        ConnectProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MaybeNotifyBytesTransferred()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastBytesNotifyTick);
        if (now - last < 200 || Interlocked.CompareExchange(ref _lastBytesNotifyTick, now, last) != last) return;
        BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Log(string message)
    {
        _logger.LogInformation("{Message}", message);
        LogLineAppended?.Invoke(this, LogTags.Tag(LogTags.Foxy, message));
    }

    private void RaiseNotice(Notice notice) => NoticeReceived?.Invoke(this, notice);

    private void RaiseRouteChanged() => RouteChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunGatedAsync(Func<Task> action)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _connectCts?.Cancel(); } catch { }
        _connectCts?.Dispose();
        StopLocalListeners();

        for (var i = 0; i < _sessions.Length; i++)
        {
            try
            {
                Interlocked.Exchange(ref _sessions[i], null)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        lock (_guardianGate)
        {
            try
            {
                _guardian?.Dispose();
            }
            catch
            {
            }
            _guardian = null;
        }

        _lifecycleGate.Dispose();
    }
}
