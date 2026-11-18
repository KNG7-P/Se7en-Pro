using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;
using Se7enPro.Services.Lantern;
using Se7enPro.Services.Foxy;
using Se7enPro.Services.Veep;

namespace Se7enPro.Services;

public sealed class IdentityProvisioner
{
    private const string ApiBaseUrl = "https://api.cloudflareclient.com";
    private const string ApiVersion = "v0a4471";
    private const string UserAgent = "okhttp/3.12.1";
    private const string ClientVersion = "a-6.41-2158";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILogger<IdentityProvisioner> _logger;
    private readonly ISettingsService _settings;
    private readonly ShardEngine _shardEngine;
    private readonly V2RayEngine _v2rayEngine;
    private readonly LanternEngine? _lanternEngine;
    private readonly FoxyEngine? _foxyEngine;
    private readonly VeepEngine? _veepEngine;

    private readonly IdentityPool _pool;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public IdentityProvisioner(
        ILogger<IdentityProvisioner> logger,
        ISettingsService settings,
        ShardEngine shardEngine,
        V2RayEngine v2rayEngine,
        IdentityPool pool,
        LanternEngine? lanternEngine = null,
        FoxyEngine? foxyEngine = null,
        VeepEngine? veepEngine = null)
    {
        _logger = logger;
        _settings = settings;
        _shardEngine = shardEngine;
        _v2rayEngine = v2rayEngine;
        _pool = pool;
        _lanternEngine = lanternEngine;
        _foxyEngine = foxyEngine;
        _veepEngine = veepEngine;
    }

    public static string GetAetherWorkDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Se7en",
            "aether");

    public record AetherIdentitiesStatus(
        bool WireGuard,
        bool WarpOnWarp,
        bool Masque,
        bool MasqueInMasque);

    public AetherIdentitiesStatus CheckAetherIdentitiesStatus(string? workDir = null)
    {
        workDir ??= GetAetherWorkDirectory();
        return new AetherIdentitiesStatus(
            WireGuard: HasValidIdentity(workDir, ConnectionMethod.WireGuard),
            WarpOnWarp: HasValidIdentity(workDir, ConnectionMethod.WarpOnWarp),
            Masque: HasValidIdentity(workDir, ConnectionMethod.Masque),
            MasqueInMasque: HasValidIdentity(workDir, ConnectionMethod.MasqueInMasque)
        );
    }

    private int PoolTarget => Math.Clamp(_settings.Settings.IdentityPoolTarget, 0, 8);

        public bool HasValidIdentity(string workDir, ConnectionMethod method)
    {
        try
        {
            return method switch
            {
                ConnectionMethod.WireGuard => IsValidWarpToml(Path.Combine(workDir, "aether.toml")),
                ConnectionMethod.WarpOnWarp => GoolIsComplete(workDir),
                ConnectionMethod.Masque => IsValidMasqueToml(Path.Combine(workDir, "aether-masque.toml")),
                ConnectionMethod.MasqueInMasque => IsValidMasqueToml(Path.Combine(workDir, "aether-masque.toml"))
                                                && IsValidMasqueToml(Path.Combine(workDir, "aether-masque-secondary.toml")),
                _ => true,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect identity file in {WorkDir}", workDir);
            return false;
        }
    }

    private bool GoolIsComplete(string workDir)
    {
        var layout = AetherExtras.GoolLayoutFor(_settings.Settings);
        if (layout.DeviceIsMasque)
        {
            return IsValidMasqueToml(Path.Combine(workDir, layout.DeviceFile))
                && IsValidWarpToml(Path.Combine(workDir, layout.WireGuardFile));
        }

        return IsValidWarpToml(Path.Combine(workDir, layout.DeviceFile))
            && IsValidWarpToml(Path.Combine(workDir, layout.WireGuardFile));
    }

    private static bool IsValidWarpToml(string path)
    {
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        if (text.Length < 50) return false;
        return text.Contains("device_id") && text.Contains("access_token") && text.Contains("wg_private_key");
    }

    private static bool IsValidMasqueToml(string path)
    {
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        if (text.Length < 100) return false;
        if (!text.Contains("device_id") || !text.Contains("access_token")) return false;
        if (!text.Contains("BEGIN CERTIFICATE") || !text.Contains("BEGIN PRIVATE KEY")) return false;

        var match = System.Text.RegularExpressions.Regex.Match(text, @"cert_issued_at\s*=\s*(\d+)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var issuedAt))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (issuedAt == 0 || now < issuedAt) return false;
            var age = now - issuedAt;
            
            if (age + (7 * 86400) >= (365 * 86400)) return false;
        }
        return true;
    }

    public async Task EnsureIdentityAsync(
        string workDir,
        ConnectionMethod method,
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct)
    {
        if (HasValidIdentity(workDir, method))
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (HasValidIdentity(workDir, method))
            {
                return;
            }

            if (TryCheckoutFromPool(workDir, method, progressCallback))
            {
                return;
            }

            var relays = IdentityRelay.ParseRelays(_settings.Settings.IdentityRelayUrls);
            if (relays.Count > 0)
            {
                progressCallback?.Invoke((12, "Trying a bootstrap relay..."));
                try
                {
                    using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    relayCts.CancelAfter(TimeSpan.FromSeconds(10 * relays.Count + 5));

                    await ProvisionInternalAsync(
                        workDir, method, proxy: null, relayCts.Token,
                        IdentityRelay.BuildRoutes(
                            directProxy: null, relayList: _settings.Settings.IdentityRelayUrls,
                            tunnelProxy: null, tunnelLabel: "tunnel"));

                    if (HasValidIdentity(workDir, method))
                    {
                        _logger.LogInformation(
                            "[Identity] Provisioned through a bootstrap relay ({Count} configured).", relays.Count);
                        progressCallback?.Invoke((55, "Cloudflare identity provisioned successfully."));
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(
                        "[Identity] Bootstrap relay(s) did not work ({Msg}). Falling back.", ex.Message);
                }
            }

            progressCallback?.Invoke((15, "Checking Cloudflare WARP identity..."));
            try
            {
                using var directCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                directCts.CancelAfter(TimeSpan.FromSeconds(3));
                await ProvisionInternalAsync(workDir, method, socksPort: null, directCts.Token);
                if (HasValidIdentity(workDir, method))
                {
                    _logger.LogInformation("[Identity] Direct identity registration succeeded.");
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation("[Identity] Direct registration probe failed or blocked ({Msg}). Acquiring proxy...", ex.Message);
            }

            _logger.LogWarning("[Identity] Cloudflare account API is blocked by carrier. Acquiring proxy to provision identity...");
            progressCallback?.Invoke((20, "Connecting to SHARD to provision WARP identity..."));

            var (socksPort, startedOurselves) = await AcquireProxyAsync(progressCallback, ct);
            if (socksPort <= 0)
            {
                _logger.LogError("[Identity] Failed to acquire a working SOCKS5 proxy to provision WARP identity.");
                return;
            }

            try
            {
                progressCallback?.Invoke((45, "Registering Cloudflare WARP identity via SHARD..."));
                await ProvisionInternalAsync(workDir, method, socksPort, ct);
                progressCallback?.Invoke((55, "Cloudflare identity provisioned successfully."));
                _logger.LogInformation("[Identity] WARP identity successfully provisioned for {Method} into {WorkDir}.", method, workDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Identity] Registration via SHARD failed: {Message}", ex.Message);
                throw;
            }
            finally
            {
                if (startedOurselves)
                {
                    _logger.LogInformation("[Identity] Stopping temporary proxy session used for provisioning...");
                    try { await _shardEngine.StopAsync(); } catch { }
                    try { await _v2rayEngine.StopAsync(); } catch { }
                    await Task.Delay(300, CancellationToken.None);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryCheckoutFromPool(
        string workDir,
        ConnectionMethod method,
        Action<(int Percent, string Text)>? progressCallback)
    {
        if (PoolTarget <= 0) return false;

        var kind = method switch
        {
            ConnectionMethod.WireGuard => IdentityKind.WireGuard,
            ConnectionMethod.WarpOnWarp => AetherExtras.IsClassicGool(_settings.Settings)
                ? IdentityKind.WireGuard
                : IdentityKind.Masque,
            ConnectionMethod.Masque or ConnectionMethod.MasqueInMasque => IdentityKind.Masque,
            _ => (IdentityKind?)null,
        };
        if (kind is null) return false;
        var identityKind = kind.Value;

        try
        {
            Directory.CreateDirectory(workDir);

            switch (method)
            {
                case ConnectionMethod.WireGuard:
                {
                    if (!_pool.TryCheckout(identityKind, workDir, out var slot) || slot is null) return false;
                    WriteWorkFile(Path.Combine(workDir, "aether.toml"), slot.Toml);
                    break;
                }

                case ConnectionMethod.WarpOnWarp:
                {
                    if (!AetherExtras.IsClassicGool(_settings.Settings))
                    {
                        if (!_pool.TryCheckout(IdentityKind.Masque, workDir, out var slot) || slot is null) return false;
                        WriteWorkFile(Path.Combine(workDir, "aether-masque.toml"), slot.Toml);
                        _logger.LogInformation(
                            "[IdentityPool] Checked out outer MASQUE identity from pool for gool; aether will register inner identity through the tunnel.");
                        break;
                    }

                    if (!_pool.TryCheckout(identityKind, workDir, out var primary) || primary is null) return false;
                    WriteWorkFile(Path.Combine(workDir, "aether.toml"), primary.Toml);

                    if (!_pool.TryCheckout(identityKind, workDir, out var secondary) || secondary is null)
                    {
                        _logger.LogWarning(
                            "[IdentityPool] Only one usable WireGuard identity; WARP-on-WARP needs two.");
                        return false;
                    }
                    WriteWorkFile(Path.Combine(workDir, "aether-secondary.toml"), secondary.Toml);
                    break;
                }

                case ConnectionMethod.Masque:
                {
                    if (!_pool.TryCheckout(identityKind, workDir, out var slot) || slot is null) return false;
                    WriteWorkFile(Path.Combine(workDir, "aether-masque.toml"), slot.Toml);
                    break;
                }

                case ConnectionMethod.MasqueInMasque:
                {
                    if (!_pool.TryCheckout(identityKind, workDir, out var primary) || primary is null) return false;
                    WriteWorkFile(Path.Combine(workDir, "aether-masque.toml"), primary.Toml);

                    if (!_pool.TryCheckout(identityKind, workDir, out var secondary) || secondary is null)
                    {
                        _logger.LogWarning(
                            "[IdentityPool] Only one usable MASQUE identity; MASQUE-in-MASQUE needs two.");
                        return false;
                    }
                    WriteWorkFile(Path.Combine(workDir, "aether-masque-secondary.toml"), secondary.Toml);
                    break;
                }
            }

            if (!HasValidIdentity(workDir, method))
            {
                _pool.ReportFailure(workDir, "the pooled identity did not validate");
                return false;
            }

            _logger.LogInformation(
                "[IdentityPool] Satisfied {Method} from the pool; no registration needed.", method);
            progressCallback?.Invoke((15, "Using a stored Cloudflare identity..."));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdentityPool] Checkout failed; falling back to registration");
            return false;
        }
    }

    private static void WriteWorkFile(string target, string content)
    {
        var temp = target + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, target, overwrite: true);
    }

    public void ReportConnectFailure(string workDir, string reason) => _pool.ReportFailure(workDir, reason);

    public void ReportConnectSuccess(string workDir) => _pool.ReportSuccess(workDir);

    public async Task RefillPoolAsync(
        ConnectionMethod method,
        int? targetOverride = null,
        CancellationToken ct = default)
    {
        var target = targetOverride ?? PoolTarget;
        if (target <= 0) return;

        var kind = method switch
        {
            ConnectionMethod.WarpOnWarp => AetherExtras.IsClassicGool(_settings.Settings)
                ? IdentityKind.WireGuard
                : IdentityKind.Masque,
            ConnectionMethod.WireGuard => IdentityKind.WireGuard,
            ConnectionMethod.Masque or ConnectionMethod.MasqueInMasque => IdentityKind.Masque,
            _ => (IdentityKind?)null,
        };
        if (kind is null) return;
        var identityKind = kind.Value;
        if (!_pool.NeedsRefill(identityKind, target)) return;

        var want = target - _pool.Count(identityKind);
        if (want <= 0) return;

        var added = 0;
        for (var i = 0; i < want; i++)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var material = await ProvisionOneAsync(identityKind, ct);
                if (material is null) break;
                if (_pool.Add(material, keepPerKind: target)) added++;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    "[IdentityPool] Top-up stopped after {Added} identity/identities: {Msg}",
                    added, ex.Message);
                break;
            }
        }

        if (added > 0)
        {
            _logger.LogInformation(
                "[IdentityPool] Topped up to {Count} usable {Kind} identity/identities.",
                _pool.Count(identityKind), identityKind);
        }
    }

    private (IWebProxy? Proxy, string Label) TryGetActiveTunnelProxy()
    {
        var s = _settings.Settings;
        var lanExposed = LanPolicy.CanExpose(s);

        if (_shardEngine.State == ConnectionState.Connected && _shardEngine.SocksProxyPort > 0)
        {
            var proxy = new WebProxy($"socks5://127.0.0.1:{_shardEngine.SocksProxyPort}");
            if (lanExposed && !string.IsNullOrEmpty(s.LanProxyUsername))
            {
                proxy.Credentials = new NetworkCredential(s.LanProxyUsername, s.LanProxyPassword ?? "");
            }
            return (proxy, "shard");
        }
        if (_v2rayEngine.State == ConnectionState.Connected && _v2rayEngine.SocksProxyPort > 0)
        {
            var proxy = new WebProxy($"socks5://127.0.0.1:{_v2rayEngine.SocksProxyPort}");
            if (lanExposed && !V2RayEngine.TryGetSocksPortOverride().HasValue && !string.IsNullOrEmpty(s.LanProxyUsername))
            {
                proxy.Credentials = new NetworkCredential(s.LanProxyUsername, s.LanProxyPassword ?? "");
            }
            return (proxy, "v2ray");
        }
        if (_lanternEngine is { State: ConnectionState.Connected, SocksProxyPort: > 0 })
        {
            return (new WebProxy($"socks5://127.0.0.1:{_lanternEngine.SocksProxyPort}"), "lantern");
        }
        if (_veepEngine is { State: ConnectionState.Connected, SocksProxyPort: > 0 })
        {
            return (new WebProxy($"socks5://127.0.0.1:{_veepEngine.SocksProxyPort}"), "veep");
        }
        if (_foxyEngine is { State: ConnectionState.Connected, SocksProxyPort: > 0 })
        {
            return (new WebProxy($"socks5://127.0.0.1:{_foxyEngine.SocksProxyPort}"), "foxy");
        }

        return (null, "tunnel");
    }

    private async Task<PooledIdentity?> ProvisionOneAsync(IdentityKind kind, CancellationToken ct)
    {
        var relays = _settings.Settings.IdentityRelayUrls;
        var (tunnelProxy, tunnelLabel) = TryGetActiveTunnelProxy();
        var routeCount = 1 + IdentityRelay.ParseRelays(relays).Count + (tunnelProxy is not null ? 1 : 0);
        if (routeCount == 1) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12 * routeCount + 5));

        var routes = IdentityRelay.BuildRoutes(
            directProxy: null, relayList: relays, tunnelProxy: tunnelProxy, tunnelLabel: tunnelLabel);

        var reg = await RegisterDeviceAsync(routes, cts.Token);
        if (kind == IdentityKind.WireGuard)
        {
            return new PooledIdentity(
                IdentityKind.WireGuard, reg.Id, BuildWarpToml(reg), CertIssuedAt: 0);
        }

        var (certPem, keyPem, assignedEndpoint) = await EnrollMasqueKeyAsync(reg, routes, cts.Token);
        return new PooledIdentity(
            IdentityKind.Masque, reg.Id, BuildMasqueToml(reg, certPem, keyPem, assignedEndpoint),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private async Task<(int SocksPort, bool StartedOurselves)> AcquireProxyAsync(
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct)
    {
        
        if (_shardEngine.State == ConnectionState.Connected && _shardEngine.SocksProxyPort > 0)
        {
            _logger.LogInformation("[Identity] Reusing live SHARD SOCKS listener on port {Port}", _shardEngine.SocksProxyPort);
            return (_shardEngine.SocksProxyPort, false);
        }

        
        _logger.LogInformation("[Identity] Starting SHARD engine in background for identity registration...");
        progressCallback?.Invoke((20, "Starting SHARD core..."));

        try
        {
            await _shardEngine.StartAsync();

            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 35000 && !ct.IsCancellationRequested)
            {
                if (_shardEngine.State == ConnectionState.Connected && _shardEngine.SocksProxyPort > 0)
                {
                    _logger.LogInformation("[Identity] SHARD listener ready on 127.0.0.1:{Port}", _shardEngine.SocksProxyPort);
                    return (_shardEngine.SocksProxyPort, true);
                }

                if (_shardEngine.State == ConnectionState.Error)
                {
                    _logger.LogWarning("[Identity] SHARD encountered error during startup");
                    break;
                }

                await Task.Delay(250, ct);
            }
            try { await _shardEngine.StopAsync(); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Identity] Failed starting SHARD for provisioning");
            try { await _shardEngine.StopAsync(); } catch { }
        }

        
        var v2Config = _v2rayEngine.ResolveActiveConfig();
        if (v2Config != null)
        {
            _logger.LogInformation("[Identity] Falling back to V2Ray node for identity provisioning...");
            progressCallback?.Invoke((25, "Starting V2Ray proxy to provision WARP identity..."));
            try
            {
                await _v2rayEngine.StartAsync();
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 25000 && !ct.IsCancellationRequested)
                {
                    if (_v2rayEngine.State == ConnectionState.Connected && _v2rayEngine.SocksProxyPort > 0)
                    {
                        return (_v2rayEngine.SocksProxyPort, true);
                    }
                    await Task.Delay(250, ct);
                }
                try { await _v2rayEngine.StopAsync(); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Identity] Failed starting V2Ray fallback");
                try { await _v2rayEngine.StopAsync(); } catch { }
            }
        }

        return (-1, false);
    }

    private Task ProvisionInternalAsync(
        string workDir,
        ConnectionMethod method,
        int? socksPort,
        CancellationToken ct)
    {
        System.Net.IWebProxy? proxy = null;
        if (socksPort.HasValue && socksPort.Value > 0)
        {
            var wp = new System.Net.WebProxy($"socks5://127.0.0.1:{socksPort.Value}");
            var s = _settings.Settings;
            if (LanPolicy.CanExpose(s) && !string.IsNullOrEmpty(s.LanProxyUsername) &&
                (socksPort.Value == _shardEngine.SocksProxyPort || socksPort.Value == _v2rayEngine.SocksProxyPort))
            {
                wp.Credentials = new NetworkCredential(s.LanProxyUsername, s.LanProxyPassword ?? "");
            }
            proxy = wp;
        }
        return ProvisionInternalAsync(workDir, method, proxy, ct);
    }

    private async Task ProvisionInternalAsync(
        string workDir,
        ConnectionMethod method,
        System.Net.IWebProxy? proxy,
        CancellationToken ct,
        IReadOnlyList<ProvisioningRoute>? extraRoutes = null)
    {
        Directory.CreateDirectory(workDir);
        IdentityFileGuard.RestrictToCurrentUser(workDir, out var aclError);
        if (aclError is not null)
        {
            _logger.LogWarning(
                "Could not restrict the ACL for identity directory {WorkDir}: {Error}",
                workDir, aclError);
        }

        var routes = new List<ProvisioningRoute>();
        if (extraRoutes is { Count: > 0 })
        {
            routes.AddRange(extraRoutes);
        }
        else
        {
            routes.Add(new("direct", proxy, RelayBaseUrl: null));
        }
        if (proxy is not null && !routes.Exists(r => ReferenceEquals(r.Proxy, proxy)))
        {
            routes.Add(new ProvisioningRoute("tunnel", proxy, RelayBaseUrl: null));
        }

        switch (method)
        {
            case ConnectionMethod.WireGuard:
            {
                var reg = await RegisterDeviceAsync(routes, ct);
                var toml = BuildWarpToml(reg);
                var target = Path.Combine(workDir, "aether.toml");
                await WriteSecretFileAsync(target, toml, ct);
                SeedPool(IdentityKind.WireGuard, reg.Id, toml, certIssuedAt: 0);
                break;
            }

            case ConnectionMethod.WarpOnWarp:
            {
                var layout = AetherExtras.GoolLayoutFor(_settings.Settings);

                if (layout.DeviceIsMasque)
                {
                    if (!IsValidMasqueToml(Path.Combine(workDir, layout.DeviceFile)))
                    {
                        var device = await RegisterDeviceAsync(routes, ct);
                        var (certPem, keyPem, assignedEndpoint) =
                            await EnrollMasqueKeyAsync(device, routes, ct);

                        var masqueToml = BuildMasqueToml(device, certPem, keyPem, assignedEndpoint);
                        await WriteSecretFileAsync(
                            Path.Combine(workDir, layout.DeviceFile), masqueToml, ct);

                        SeedPool(IdentityKind.Masque, device.Id, masqueToml,
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    }

                    if (!IsValidWarpToml(Path.Combine(workDir, layout.WireGuardFile)))
                    {
                        var innerDevice = await RegisterDeviceAsync(routes, ct);
                        var innerToml = BuildWarpToml(innerDevice);
                        await WriteSecretFileAsync(
                            Path.Combine(workDir, layout.WireGuardFile), innerToml, ct);

                        SeedPool(IdentityKind.WireGuard, innerDevice.Id, innerToml, certIssuedAt: 0);
                    }
                    break;
                }

                var primary = await RegisterDeviceAsync(routes, ct);
                var tomlPrimary = BuildWarpToml(primary);
                var targetPrimary = Path.Combine(workDir, layout.DeviceFile);
                await WriteSecretFileAsync(targetPrimary, tomlPrimary, ct);
                SeedPool(IdentityKind.WireGuard, primary.Id, tomlPrimary, certIssuedAt: 0);

                var secondary = await RegisterDeviceAsync(routes, ct);
                var tomlSecondary = BuildWarpToml(secondary);
                var targetSecondary = Path.Combine(workDir, layout.WireGuardFile);
                await WriteSecretFileAsync(targetSecondary, tomlSecondary, ct);
                SeedPool(IdentityKind.WireGuard, secondary.Id, tomlSecondary, certIssuedAt: 0);
                break;
            }

            case ConnectionMethod.Masque:
            {
                var target = Path.Combine(workDir, "aether-masque.toml");
                if (!IsValidMasqueToml(target))
                {
                    var reg = await RegisterDeviceAsync(routes, ct);
                    var (certPem, keyPem, assignedEndpoint) = await EnrollMasqueKeyAsync(reg, routes, ct);
                    var toml = BuildMasqueToml(reg, certPem, keyPem, assignedEndpoint);
                    await WriteSecretFileAsync(target, toml, ct);
                    SeedPool(IdentityKind.Masque, reg.Id, toml, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                break;
            }

            case ConnectionMethod.MasqueInMasque:
            {
                var targetPrimary = Path.Combine(workDir, "aether-masque.toml");
                if (!IsValidMasqueToml(targetPrimary))
                {
                    var primary = await RegisterDeviceAsync(routes, ct);
                    var (certPrimary, keyPrimary, assignedPrimary) = await EnrollMasqueKeyAsync(primary, routes, ct);
                    var tomlPrimary = BuildMasqueToml(primary, certPrimary, keyPrimary, assignedPrimary);
                    await WriteSecretFileAsync(targetPrimary, tomlPrimary, ct);
                    SeedPool(IdentityKind.Masque, primary.Id, tomlPrimary,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }

                var targetSecondary = Path.Combine(workDir, "aether-masque-secondary.toml");
                if (!IsValidMasqueToml(targetSecondary))
                {
                    var secondary = await RegisterDeviceAsync(routes, ct);
                    var (certSecondary, keySecondary, assignedSecondary) = await EnrollMasqueKeyAsync(secondary, routes, ct);
                    var tomlSecondary = BuildMasqueToml(secondary, certSecondary, keySecondary, assignedSecondary);
                    await WriteSecretFileAsync(targetSecondary, tomlSecondary, ct);
                    SeedPool(IdentityKind.Masque, secondary.Id, tomlSecondary,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                break;
            }
        }
    }

    private void SeedPool(IdentityKind kind, string deviceId, string toml, long certIssuedAt)
    {
        if (PoolTarget <= 0) return;

        try
        {
            if (_pool.Add(
                    new PooledIdentity(kind, deviceId, toml, certIssuedAt),
                    keepPerKind: PoolTarget))
            {
                _logger.LogInformation(
                    "[IdentityPool] Kept the freshly provisioned {Kind} identity as a reserve.", kind);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdentityPool] Could not keep the provisioned identity in reserve");
        }
    }

    private static async Task WriteSecretFileAsync(string target, string content, CancellationToken ct)
    {
        var temp = target + ".tmp";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
        File.Move(temp, target, overwrite: true);
    }


    private sealed record DeviceRegResult(
        string Id,
        string Token,
        string PrivateKeyBase64,
        string PeerPublicKeyBase64,
        string Ipv4,
        string Ipv6,
        string ClientId,
        string GatewayProxy,
        string AssignedEndpoint);

    private static async Task<DeviceRegResult> RegisterDeviceAsync(
        IReadOnlyList<ProvisioningRoute> routes,
        CancellationToken ct)
    {
        var (privB64, pubB64) = Curve25519Helper.GenerateKeyPair();
        var tos = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var serial = RandomNumberGenerator.GetHexString(16);

        var bodyObj = new
        {
            key = pubB64,
            install_id = "",
            fcm_token = "",
            tos = tos,
            model = "PC",
            serial_number = serial,
            os_version = "",
            key_type = "curve25519",
            tunnel_type = "wireguard",
            locale = "en_US"
        };
        var json = JsonSerializer.Serialize(bodyObj, JsonOpts);

        var respJson = await SendToRegistrationAsync(
            HttpMethod.Post, "reg", json, bearer: null, routes, ct);

        using var doc = JsonDocument.Parse(respJson);
        var root = doc.RootElement;

        var id = root.GetProperty("id").GetString() ?? "";
        var token = root.GetProperty("token").GetString() ?? "";
        var config = root.GetProperty("config");
        var addresses = config.GetProperty("interface").GetProperty("addresses");
        var ipv4 = addresses.GetProperty("v4").GetString() ?? "172.16.0.2";
        var ipv6 = addresses.GetProperty("v6").GetString() ?? "";
        var clientId = config.TryGetProperty("client_id", out var cProp) ? cProp.GetString() ?? "" : "";
        var peerPubKey = config.GetProperty("peers")[0].GetProperty("public_key").GetString() ?? "";
        var endpoint = config.GetProperty("peers")[0].GetProperty("endpoint").GetProperty("v4").GetString() ?? "162.159.192.1:2408";
        var gatewayProxy = config.TryGetProperty("services", out var sProp) && sProp.TryGetProperty("http_proxy", out var hProp)
            ? hProp.GetString() ?? "" : "";

        return new DeviceRegResult(id, token, privB64, peerPubKey, ipv4, ipv6, clientId, gatewayProxy, endpoint);
    }

    private static async Task<string> SendToRegistrationAsync(
        HttpMethod method,
        string path,
        string bodyJson,
        string? bearer,
        IReadOnlyList<ProvisioningRoute> routes,
        CancellationToken ct)
    {
        var failures = new List<string>();

        foreach (var route in routes)
        {
            using var perRoute = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perRoute.CancelAfter(TimeSpan.FromSeconds(12));

            try
            {
                using var handler = new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(8)
                };
                if (route.Proxy is not null) handler.Proxy = route.Proxy;

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

                var target = route.IsRelay
                    ? $"{route.RelayBaseUrl}/{ApiVersion}/{path}"
                    : $"{ApiBaseUrl}/{ApiVersion}/{path}";

                using var req = new HttpRequestMessage(method, target);
                req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                req.Headers.TryAddWithoutValidation("CF-Client-Version", ClientVersion);

                if (bearer is not null)
                {
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
                }

                if (route.IsRelay)
                {
                    req.Headers.TryAddWithoutValidation(IdentityRelay.RelayHeader, IdentityRelay.RelayHeaderValue);
                }

                using var resp = await client.SendAsync(req, perRoute.Token);

                if (resp.IsSuccessStatusCode)
                {
                    return await resp.Content.ReadAsStringAsync(perRoute.Token);
                }

                var err = await resp.Content.ReadAsStringAsync(perRoute.Token);
                failures.Add($"{route.Label}: HTTP {(int)resp.StatusCode}");
                if (route.IsDirect)
                {
                    throw new HttpRequestException(
                        $"Registration failed via {route.Label}: {(int)resp.StatusCode} - {err}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{route.Label}: {ex.GetType().Name}");
            }
        }

        throw new HttpRequestException(
            "Registration failed on every route -> " + string.Join("; ", failures));
    }

    private async Task<(string CertPem, string KeyPem, string AssignedEndpoint)> EnrollMasqueKeyAsync(
        DeviceRegResult reg,
        IReadOnlyList<ProvisioningRoute> routes,
        CancellationToken ct)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certReq = new CertificateRequest("CN=WARP", ecdsa, HashAlgorithmName.SHA256);
        using var x509 = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

        var certPem = x509.ExportCertificatePem().Trim();
        var keyPem = ecdsa.ExportPkcs8PrivateKeyPem().Trim();
        var spkiDer = ecdsa.ExportSubjectPublicKeyInfo();
        var spkiB64 = Convert.ToBase64String(spkiDer);

        var patchBody = JsonSerializer.Serialize(new
        {
            key = spkiB64,
            key_type = "secp256r1",
            tunnel_type = "masque"
        }, JsonOpts);

        var assignedEndpoint = reg.AssignedEndpoint;

        var respStr = await SendToRegistrationAsync(
            HttpMethod.Patch, $"reg/{reg.Id}", patchBody, reg.Token, routes, ct);

        try
        {
            using var doc = JsonDocument.Parse(respStr);
            if (doc.RootElement.TryGetProperty("config", out var config) &&
                config.TryGetProperty("peers", out var peers) &&
                peers.GetArrayLength() > 0 &&
                peers[0].TryGetProperty("endpoint", out var epObj) &&
                epObj.TryGetProperty("v4", out var epV4))
            {
                var v4Str = epV4.GetString();
                if (!string.IsNullOrEmpty(v4Str)) assignedEndpoint = v4Str;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Identity] Could not parse updated endpoint from MASQUE enrollment response");
        }

        return (certPem, keyPem, assignedEndpoint);
    }

    private static string TomlString(string? value)
    {
        var s = value ?? "";
        var sb = new StringBuilder(s.Length + 8);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20 || c == 0x7F)
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string BuildWarpToml(DeviceRegResult r) =>
$"""
device_id = {TomlString(r.Id)}
access_token = {TomlString(r.Token)}
cert_pem = ""
key_pem = ""
cert_issued_at = 0
ipv4 = {TomlString(r.Ipv4)}
ipv6 = {TomlString(r.Ipv6)}
wg_private_key = {TomlString(r.PrivateKeyBase64)}
wg_peer_public_key = {TomlString(r.PeerPublicKeyBase64)}
client_id = {TomlString(r.ClientId)}
organization = ""
gateway_proxy = {TomlString(r.GatewayProxy)}
assigned_endpoint = {TomlString(r.AssignedEndpoint)}
""";

    private static string BuildMasqueToml(DeviceRegResult r, string certPem, string keyPem, string assignedEndpoint)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"device_id = {TomlString(r.Id)}");
        sb.AppendLine($"access_token = {TomlString(r.Token)}");
        sb.AppendLine($"cert_pem = {TomlString(certPem)}");
        sb.AppendLine($"key_pem = {TomlString(keyPem)}");
        sb.AppendLine($"cert_issued_at = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        sb.AppendLine($"ipv4 = {TomlString(r.Ipv4)}");
        sb.AppendLine($"ipv6 = {TomlString(r.Ipv6)}");
        sb.AppendLine($"wg_private_key = {TomlString(r.PrivateKeyBase64)}");
        sb.AppendLine($"wg_peer_public_key = {TomlString(r.PeerPublicKeyBase64)}");
        sb.AppendLine($"client_id = {TomlString(r.ClientId)}");
        sb.AppendLine("organization = \"\"");
        sb.AppendLine($"gateway_proxy = {TomlString(r.GatewayProxy)}");
        sb.AppendLine($"assigned_endpoint = {TomlString(assignedEndpoint)}");
        return sb.ToString();
    }

    private async Task<(int SocksPort, bool StartedOurselves)> AcquireEngineProxyAsync(
        IConnectionEngine? engine,
        string engineLabel,
        int timeoutMs,
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct)
    {
        if (engine != null && engine.State == ConnectionState.Connected && engine.SocksProxyPort > 0)
        {
            _logger.LogInformation("[Identity] Reusing live {Engine} SOCKS listener on port {Port}", engineLabel, engine.SocksProxyPort);
            return (engine.SocksProxyPort, false);
        }

        if (engine == null)
        {
            _logger.LogWarning("[Identity] {Engine}Engine not available.", engineLabel);
            return (-1, false);
        }

        _logger.LogInformation("[Identity] Starting {Engine} engine for identity registration...", engineLabel);
        progressCallback?.Invoke((20, $"Starting {engineLabel} core..."));

        try
        {
            await engine.StartAsync();

            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
            {
                if (engine.State == ConnectionState.Connected && engine.SocksProxyPort > 0)
                {
                    _logger.LogInformation("[Identity] {Engine} listener ready on 127.0.0.1:{Port}", engineLabel, engine.SocksProxyPort);
                    return (engine.SocksProxyPort, true);
                }

                if (engine.State == ConnectionState.Error)
                {
                    _logger.LogWarning("[Identity] {Engine} encountered error during startup", engineLabel);
                    break;
                }

                await Task.Delay(250, ct);
            }
            try { await engine.StopAsync(); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Identity] Failed starting {Engine} for provisioning", engineLabel);
            try { await engine.StopAsync(); } catch { }
        }

        return (-1, false);
    }

    private Task<(int SocksPort, bool StartedOurselves)> AcquireLanternProxyAsync(
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct) =>
        AcquireEngineProxyAsync(_lanternEngine, "Lantern", 35000, progressCallback, ct);

    private Task<(int SocksPort, bool StartedOurselves)> AcquireVeepProxyAsync(
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct) =>
        AcquireEngineProxyAsync(_veepEngine, "Veep", 35000, progressCallback, ct);

    private Task<(int SocksPort, bool StartedOurselves)> AcquireFoxyProxyAsync(
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct) =>
        AcquireEngineProxyAsync(_foxyEngine, "Foxy", 35000, progressCallback, ct);

    private async Task<(int SocksPort, bool StartedOurselves, bool ClearPortOverride)> AcquireV2RayProxyAsync(
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct)
    {
        if (_v2rayEngine.State == ConnectionState.Connected && _v2rayEngine.SocksProxyPort > 0)
        {
            var existingPort = _v2rayEngine.SocksProxyPort;
            var existingProof = await SocksProbe.WaitForTunnelAsync(
                existingPort, DateTime.UtcNow.AddSeconds(12), ct, TimeSpan.FromMilliseconds(500));
            if (existingProof is not null)
            {
                _logger.LogInformation("[Identity] Reusing live V2Ray SOCKS listener on port {Port}", existingPort);
                return (existingPort, false, false);
            }
            _logger.LogWarning("[Identity] Live V2Ray proxy on port {Port} failed reachability check", existingPort);
            return (-1, false, false);
        }

        if (_v2rayEngine.State == ConnectionState.Connecting)
        {
            var waitDeadline = Stopwatch.StartNew();
            while (waitDeadline.ElapsedMilliseconds < 25000 && !ct.IsCancellationRequested)
            {
                if (_v2rayEngine.State == ConnectionState.Connected && _v2rayEngine.SocksProxyPort > 0)
                {
                    var livePort = _v2rayEngine.SocksProxyPort;
                    var liveProof = await SocksProbe.WaitForTunnelAsync(
                        livePort, DateTime.UtcNow.AddSeconds(12), ct, TimeSpan.FromMilliseconds(500));
                    return liveProof is not null ? (livePort, false, false) : (-1, false, false);
                }
                if (_v2rayEngine.State is ConnectionState.Error or ConnectionState.Disconnected)
                    return (-1, false, false);
                await Task.Delay(250, ct);
            }
            return (-1, false, false);
        }

        var v2Config = _v2rayEngine.ResolveActiveConfig();
        if (v2Config is null || V2RayEngine.ValidateOutboundNode(v2Config) is not null)
        {
            _logger.LogWarning("[Identity] No valid active V2Ray configuration available for identity provisioning.");
            return (-1, false, false);
        }

        _logger.LogInformation("[Identity] Starting V2Ray engine for identity registration...");
        progressCallback?.Invoke((20, "Starting V2Ray proxy..."));

        var overridePort = AllocateFreeLoopbackPortPair();
        V2RayEngine.SetSocksPortOverride(overridePort);
        try
        {
            await _v2rayEngine.StartAsync();

            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 25000 && !ct.IsCancellationRequested)
            {
                if (_v2rayEngine.State == ConnectionState.Connected && _v2rayEngine.SocksProxyPort > 0)
                {
                    var socksPort = _v2rayEngine.SocksProxyPort;
                    progressCallback?.Invoke((30, "Verifying V2Ray internet reachability..."));
                    var proof = await SocksProbe.WaitForTunnelAsync(
                        socksPort, DateTime.UtcNow.AddSeconds(12), ct, TimeSpan.FromMilliseconds(500));
                    if (proof is not null)
                    {
                        _logger.LogInformation("[Identity] V2Ray listener ready and verified on 127.0.0.1:{Port}", socksPort);
                        return (socksPort, true, true);
                    }
                    _logger.LogWarning("[Identity] V2Ray started on 127.0.0.1:{Port}, but failed reachability check.", socksPort);
                    break;
                }

                if (_v2rayEngine.State is ConnectionState.Error or ConnectionState.Disconnected)
                {
                    _logger.LogWarning("[Identity] V2Ray encountered error during startup");
                    break;
                }

                await Task.Delay(250, ct);
            }

            await ReleaseV2RayProxyAsync(startedOurselves: true, clearPortOverride: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Identity] Failed starting V2Ray for provisioning");
            await ReleaseV2RayProxyAsync(startedOurselves: true, clearPortOverride: true);
        }

        return (-1, false, false);
    }

    private async Task ReleaseV2RayProxyAsync(bool startedOurselves, bool clearPortOverride)
    {
        try
        {
            if (startedOurselves)
                await _v2rayEngine.StopAsync();
        }
        catch
        {
        }
        finally
        {
            if (clearPortOverride)
                V2RayEngine.ClearSocksPortOverride();
        }
    }

    private static int AllocateFreeLoopbackPortPair()
    {
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var basePort = Random.Shared.Next(24000, 44000);
            try
            {
                using var l1 = new TcpListener(IPAddress.Loopback, basePort);
                l1.Start();
                using var l2 = new TcpListener(IPAddress.Loopback, basePort + 1);
                l2.Start();
                return basePort;
            }
            catch
            {
            }
        }
        return 25808;
    }

    private static string GetAetherExePath(string workDir)
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var binName = OperatingSystem.IsWindows() ? "aether.exe" : "aether";
        var bundled = Path.Combine(appDir, "Resources", "aether", binName);
        var staged = Path.Combine(workDir, binName);
        if (File.Exists(staged)) return staged;
        if (File.Exists(bundled)) return bundled;
        return bundled;
    }

    private string GetAetherRegisterTarget(ConnectionMethod method) => method switch
    {
        ConnectionMethod.WireGuard => "wg",
        ConnectionMethod.WarpOnWarp => AetherExtras.IsClassicGool(_settings.Settings) ? "gool-classic" : "gool",
        ConnectionMethod.Masque => "masque",
        ConnectionMethod.MasqueInMasque => "mim",
        _ => "all"
    };

    private async Task<bool> RunAetherRegisterCommandAsync(
        string workDir,
        string[] extraArgs,
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct,
        string target = "all")
    {
        var exePath = GetAetherExePath(workDir);
        if (!File.Exists(exePath))
        {
            _logger.LogWarning("[Identity] aether binary not found at {Path}", exePath);
            return false;
        }

        Directory.CreateDirectory(workDir);
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var registerTarget = string.IsNullOrWhiteSpace(target) ? "all" : target.Trim();
        psi.ArgumentList.Add("--register");
        psi.ArgumentList.Add(registerTarget);
        foreach (var arg in extraArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogInformation("[Identity] Running aether --register {Target} in {WorkDir} with args: {Args}",
            registerTarget, workDir, string.Join(" ", psi.ArgumentList));

        try
        {
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var output = new StringBuilder();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                    _logger.LogInformation("[aether register] {Line}", e.Data);
                    if (e.Data.Contains("identity ready", StringComparison.OrdinalIgnoreCase)
                        || e.Data.Contains("identities ready", StringComparison.OrdinalIgnoreCase)
                        || e.Data.Contains("enrolling", StringComparison.OrdinalIgnoreCase)
                        || e.Data.Contains("registering", StringComparison.OrdinalIgnoreCase))
                    {
                        progressCallback?.Invoke((70, e.Data.Trim()));
                    }
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                    _logger.LogWarning("[aether register err] {Line}", e.Data);
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cancelReg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Identity] Failed executing aether register command");
            return false;
        }
    }

    private async Task EnsureMissingViaInternalAsync(
        string workDir,
        int socksPort,
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct)
    {
        var methods = new[]
        {
            ConnectionMethod.WireGuard,
            ConnectionMethod.WarpOnWarp,
            ConnectionMethod.Masque,
            ConnectionMethod.MasqueInMasque
        };

        foreach (var m in methods)
        {
            if (!HasValidIdentity(workDir, m))
            {
                _logger.LogInformation("[Identity] Provisioning missing identity for {Method} via SOCKS port {Port}...", m, socksPort);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    progressCallback?.Invoke((75, $"Provisioning {m} identity..."));
                    await ProvisionInternalAsync(workDir, m, socksPort, ct);
                    progressCallback?.Invoke((85, $"{m} identity updated."));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Identity] Failed internal provisioning for {Method}", m);
                }
            }
        }
    }

    private async Task<(bool Success, string Message)> FetchAllViaUpstreamProxyAsync(
        string workDir,
        string methodLabel,
        int socksPort,
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct)
    {
        progressCallback?.Invoke((50, $"Registering identities via {methodLabel}..."));
        await RunAetherRegisterCommandAsync(workDir, new[] { "--upstream", $"socks5://127.0.0.1:{socksPort}" }, progressCallback, ct);

        await EnsureMissingViaInternalAsync(workDir, socksPort, progressCallback, ct);

        var status = CheckAetherIdentitiesStatus(workDir);
        if (status.WireGuard && status.WarpOnWarp && status.Masque && status.MasqueInMasque)
        {
            progressCallback?.Invoke((100, "All identities received successfully."));
            return (true, $"All Aether identities received successfully via {methodLabel}.");
        }
        return (false, $"{methodLabel} registration completed with partial or missing identities.");
    }

    public async Task<(bool Success, string Message)> FetchAllAetherIdentitiesAsync(
        string method,
        Action<(int Percent, string Text)>? progressCallback = null,
        CancellationToken ct = default)
    {
        var workDir = GetAetherWorkDirectory();
        Directory.CreateDirectory(workDir);
        method = (method ?? "default").Trim().ToLowerInvariant();

        progressCallback?.Invoke((10, "Starting identity acquisition..."));

        try
        {
            if (method == "ech")
            {
                progressCallback?.Invoke((20, "Registering all identities using ECH..."));
                var s = _settings.Settings;
                var echArgs = new List<string> { "--ech", "auto" };
                var echDns = AetherExtras.BuildEchDnsField(s);
                if (!string.IsNullOrWhiteSpace(echDns))
                {
                    echArgs.Add("--ech-dns");
                    echArgs.Add(echDns);
                }
                if (!string.IsNullOrWhiteSpace(s.AetherEchDomain))
                {
                    echArgs.Add("--ech-domain");
                    echArgs.Add(s.AetherEchDomain.Trim());
                }

                await RunAetherRegisterCommandAsync(workDir, echArgs.ToArray(), progressCallback, ct);
                var status = CheckAetherIdentitiesStatus(workDir);
                if (status.WireGuard && status.WarpOnWarp && status.Masque && status.MasqueInMasque)
                {
                    progressCallback?.Invoke((100, "All identities received successfully."));
                    return (true, "All Aether identities received successfully via ECH.");
                }
                return (false, "ECH registration completed with partial or missing identities.");
            }
            else if (method == "lantern")
            {
                progressCallback?.Invoke((20, "Starting Lantern proxy..."));
                var (socksPort, startedOurselves) = await AcquireLanternProxyAsync(progressCallback, ct);
                if (socksPort <= 0)
                {
                    return (false, "Failed to connect to Lantern proxy.");
                }
                try
                {
                    return await FetchAllViaUpstreamProxyAsync(workDir, "Lantern", socksPort, progressCallback, ct);
                }
                finally
                {
                    if (startedOurselves && _lanternEngine != null)
                    {
                        try { await _lanternEngine.StopAsync(); } catch { }
                    }
                }
            }
            else if (method == "veep")
            {
                progressCallback?.Invoke((20, "Starting Veep proxy..."));
                var (socksPort, startedOurselves) = await AcquireVeepProxyAsync(progressCallback, ct);
                if (socksPort <= 0)
                {
                    return (false, "Failed to connect to Veep proxy.");
                }
                try
                {
                    return await FetchAllViaUpstreamProxyAsync(workDir, "Veep", socksPort, progressCallback, ct);
                }
                finally
                {
                    if (startedOurselves && _veepEngine != null)
                    {
                        try { await _veepEngine.StopAsync(); } catch { }
                    }
                }
            }
            else if (method == "v2ray")
            {
                var cfg = _v2rayEngine.ResolveActiveConfig();
                var nodeProblem = V2RayEngine.ValidateOutboundNode(cfg);
                if (nodeProblem is not null)
                {
                    return (false, $"No valid active V2Ray configuration found ({nodeProblem}). Add and select one in Settings -> V2Ray.");
                }
                progressCallback?.Invoke((20, "Starting V2Ray proxy..."));
                var (socksPort, startedOurselves, clearPortOverride) = await AcquireV2RayProxyAsync(progressCallback, ct);
                if (socksPort <= 0)
                {
                    return (false, "V2Ray proxy failed to connect or could not reach the internet.");
                }
                try
                {
                    return await FetchAllViaUpstreamProxyAsync(workDir, "V2Ray", socksPort, progressCallback, ct);
                }
                finally
                {
                    await ReleaseV2RayProxyAsync(startedOurselves, clearPortOverride);
                }
            }
            else if (method == "shard")
            {
                progressCallback?.Invoke((20, "Starting SHARD proxy..."));
                var (socksPort, startedOurselves) = await AcquireProxyAsync(progressCallback, ct);
                if (socksPort <= 0)
                {
                    return (false, "Failed to connect to SHARD proxy.");
                }
                try
                {
                    return await FetchAllViaUpstreamProxyAsync(workDir, "SHARD", socksPort, progressCallback, ct);
                }
                finally
                {
                    if (startedOurselves)
                    {
                        try { await _shardEngine.StopAsync(); } catch { }
                        try { await _v2rayEngine.StopAsync(); } catch { }
                    }
                }
            }
            else if (method == "foxy")
            {
                if (_settings.Settings.FirefoxAccounts == null || _settings.Settings.FirefoxAccounts.Count == 0)
                {
                    return (false, "No Firefox account found. Please add a Firefox account in Settings -> Foxy.");
                }
                progressCallback?.Invoke((20, "Starting Foxy proxy..."));
                var (socksPort, startedOurselves) = await AcquireFoxyProxyAsync(progressCallback, ct);
                if (socksPort <= 0)
                {
                    return (false, "Failed to connect to Foxy proxy.");
                }
                try
                {
                    return await FetchAllViaUpstreamProxyAsync(workDir, "Foxy", socksPort, progressCallback, ct);
                }
                finally
                {
                    if (startedOurselves && _foxyEngine != null)
                    {
                        try { await _foxyEngine.StopAsync(); } catch { }
                    }
                }
            }
            else
            {
                progressCallback?.Invoke((20, "Registering all identities using default method..."));
                var step = 0;
                foreach (var m in new[] { ConnectionMethod.WireGuard, ConnectionMethod.WarpOnWarp, ConnectionMethod.Masque, ConnectionMethod.MasqueInMasque })
                {
                    ct.ThrowIfCancellationRequested();
                    step++;
                    progressCallback?.Invoke((20 + (step * 18), $"Provisioning {m.ToDisplayName()} identity ({step}/4)..."));
                    await EnsureIdentityAsync(workDir, m, progressCallback, ct);
                    progressCallback?.Invoke((25 + (step * 18), $"{m.ToDisplayName()} identity check complete ({step}/4)."));
                }
                var status = CheckAetherIdentitiesStatus(workDir);
                if (status.WireGuard && status.WarpOnWarp && status.Masque && status.MasqueInMasque)
                {
                    progressCallback?.Invoke((100, "All identities received successfully."));
                    return (true, "All Aether identities received successfully via default method.");
                }
                return (false, "Default method completed with partial or missing identities.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while acquiring Aether identities with method {Method}", method);
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string Message)> FetchSingleAetherIdentityAsync(
        string identityType,
        string acqMethod,
        Action<(int Percent, string Text)>? progressCallback = null,
        CancellationToken ct = default)
    {
        var workDir = GetAetherWorkDirectory();
        Directory.CreateDirectory(workDir);
        identityType = (identityType ?? "").Trim().ToLowerInvariant();
        acqMethod = (acqMethod ?? "default").Trim().ToLowerInvariant();

        ConnectionMethod targetMethod = identityType switch
        {
            "wireguard" => ConnectionMethod.WireGuard,
            "warp" => ConnectionMethod.WarpOnWarp,
            "masque" => ConnectionMethod.Masque,
            "mim" => ConnectionMethod.MasqueInMasque,
            _ => ConnectionMethod.WireGuard
        };

        try
        {
            switch (targetMethod)
            {
                case ConnectionMethod.WireGuard:
                    File.Delete(Path.Combine(workDir, "aether.toml"));
                    break;
                case ConnectionMethod.WarpOnWarp:
                    File.Delete(Path.Combine(workDir, "aether-dual.toml"));
                    File.Delete(Path.Combine(workDir, "aether-gool.toml"));
                    File.Delete(Path.Combine(workDir, "aether-secondary.toml"));
                    File.Delete(Path.Combine(workDir, "aether-masque-gool.toml"));
                    if (!HasValidIdentity(workDir, ConnectionMethod.WireGuard))
                        File.Delete(Path.Combine(workDir, "aether.toml"));
                    if (!HasValidIdentity(workDir, ConnectionMethod.Masque))
                        File.Delete(Path.Combine(workDir, "aether-masque.toml"));
                    break;
                case ConnectionMethod.Masque:
                    File.Delete(Path.Combine(workDir, "aether-masque.toml"));
                    break;
                case ConnectionMethod.MasqueInMasque:
                    File.Delete(Path.Combine(workDir, "aether-masque-secondary.toml"));
                    if (!HasValidIdentity(workDir, ConnectionMethod.Masque))
                        File.Delete(Path.Combine(workDir, "aether-masque.toml"));
                    break;
            }
        }
        catch { }

        progressCallback?.Invoke((15, $"Provisioning {identityType} identity via {acqMethod}..."));

        try
        {
            await EnsureIdentityWithMethodAsync(workDir, targetMethod, acqMethod, progressCallback, ct);
            if (HasValidIdentity(workDir, targetMethod))
            {
                progressCallback?.Invoke((100, $"{identityType} identity received successfully."));
                return (true, $"Identity for {identityType} received successfully.");
            }
            return (false, $"Failed to receive valid identity for {identityType}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch single identity {Type} with method {Method}", identityType, acqMethod);
            return (false, ex.Message);
        }
    }

    private async Task ProvisionMethodViaSocksPortAsync(
        string workDir,
        ConnectionMethod method,
        int socksPort,
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct)
    {
        var regTarget = GetAetherRegisterTarget(method);
        await RunAetherRegisterCommandAsync(workDir, new[] { "--upstream", $"socks5://127.0.0.1:{socksPort}" }, progressCallback, ct, regTarget);
        if (!HasValidIdentity(workDir, method))
        {
            await ProvisionInternalAsync(workDir, method, socksPort, ct);
        }
    }

    public async Task EnsureIdentityWithMethodAsync(
        string workDir,
        ConnectionMethod method,
        string acqMethod,
        Action<(int Percent, string Text)>? progressCallback,
        CancellationToken ct)
    {
        acqMethod = (acqMethod ?? "default").Trim().ToLowerInvariant();
        if (acqMethod == "default")
        {
            await EnsureIdentityAsync(workDir, method, progressCallback, ct);
            return;
        }

        if (HasValidIdentity(workDir, method)) return;

        await _gate.WaitAsync(ct);
        try
        {
            if (HasValidIdentity(workDir, method)) return;

            if (acqMethod == "ech")
            {
                var s = _settings.Settings;
                var echArgs = new List<string> { "--ech", "auto" };
                var echDns = AetherExtras.BuildEchDnsField(s);
                if (!string.IsNullOrWhiteSpace(echDns))
                {
                    echArgs.Add("--ech-dns");
                    echArgs.Add(echDns);
                }
                if (!string.IsNullOrWhiteSpace(s.AetherEchDomain))
                {
                    echArgs.Add("--ech-domain");
                    echArgs.Add(s.AetherEchDomain.Trim());
                }
                var regTarget = GetAetherRegisterTarget(method);
                await RunAetherRegisterCommandAsync(workDir, echArgs.ToArray(), progressCallback, ct, regTarget);
                if (HasValidIdentity(workDir, method)) return;
            }
            else if (acqMethod == "lantern")
            {
                var (socksPort, startedOurselves) = await AcquireLanternProxyAsync(progressCallback, ct);
                if (socksPort > 0)
                {
                    try
                    {
                        await ProvisionMethodViaSocksPortAsync(workDir, method, socksPort, progressCallback, ct);
                    }
                    finally
                    {
                        if (startedOurselves && _lanternEngine != null)
                        {
                            try { await _lanternEngine.StopAsync(); } catch { }
                        }
                    }
                    if (HasValidIdentity(workDir, method)) return;
                }
            }
            else if (acqMethod == "veep")
            {
                var (socksPort, startedOurselves) = await AcquireVeepProxyAsync(progressCallback, ct);
                if (socksPort > 0)
                {
                    try
                    {
                        await ProvisionMethodViaSocksPortAsync(workDir, method, socksPort, progressCallback, ct);
                    }
                    finally
                    {
                        if (startedOurselves && _veepEngine != null)
                        {
                            try { await _veepEngine.StopAsync(); } catch { }
                        }
                    }
                    if (HasValidIdentity(workDir, method)) return;
                }
            }
            else if (acqMethod == "v2ray")
            {
                var (socksPort, startedOurselves, clearPortOverride) = await AcquireV2RayProxyAsync(progressCallback, ct);
                if (socksPort > 0)
                {
                    try
                    {
                        await ProvisionMethodViaSocksPortAsync(workDir, method, socksPort, progressCallback, ct);
                    }
                    finally
                    {
                        await ReleaseV2RayProxyAsync(startedOurselves, clearPortOverride);
                    }
                    if (HasValidIdentity(workDir, method)) return;
                }
            }
            else if (acqMethod == "shard")
            {
                var (socksPort, startedOurselves) = await AcquireProxyAsync(progressCallback, ct);
                if (socksPort > 0)
                {
                    try
                    {
                        await ProvisionMethodViaSocksPortAsync(workDir, method, socksPort, progressCallback, ct);
                    }
                    finally
                    {
                        if (startedOurselves)
                        {
                            try { await _shardEngine.StopAsync(); } catch { }
                            try { await _v2rayEngine.StopAsync(); } catch { }
                        }
                    }
                    if (HasValidIdentity(workDir, method)) return;
                }
            }
            else if (acqMethod == "foxy")
            {
                var (socksPort, startedOurselves) = await AcquireFoxyProxyAsync(progressCallback, ct);
                if (socksPort > 0)
                {
                    try
                    {
                        await ProvisionMethodViaSocksPortAsync(workDir, method, socksPort, progressCallback, ct);
                    }
                    finally
                    {
                        if (startedOurselves && _foxyEngine != null)
                        {
                            try { await _foxyEngine.StopAsync(); } catch { }
                        }
                    }
                    if (HasValidIdentity(workDir, method)) return;
                }
            }

            await EnsureIdentityAsync(workDir, method, progressCallback, ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
