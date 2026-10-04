using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

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

    /// <summary>
    /// Pre-provisioned identities, so the registration endpoint is not on the critical
    /// path of every connect.
    /// </summary>
    private readonly IdentityPool _pool;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public IdentityProvisioner(
        ILogger<IdentityProvisioner> logger,
        ISettingsService settings,
        ShardEngine shardEngine,
        V2RayEngine v2rayEngine,
        IdentityPool pool)
    {
        _logger = logger;
        _settings = settings;
        _shardEngine = shardEngine;
        _v2rayEngine = v2rayEngine;
        _pool = pool;
    }

    /// <summary>How many identities of each kind to keep on hand. 0 disables the pool.</summary>
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

    /// <summary>Whether the work dir already holds both halves of the selected gool.</summary>
    private bool GoolIsComplete(string workDir)
    {
        var layout = AetherExtras.GoolLayoutFor(_settings.Settings);
        if (layout.DeviceIsMasque)
        {
            return IsValidMasqueToml(Path.Combine(workDir, layout.DeviceFile));
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
                    _logger.LogInformation("[Identity] Stopping temporary SHARD session used for provisioning...");
                    try { await _shardEngine.StopAsync(); } catch { }
                    await Task.Delay(300, CancellationToken.None);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tries to satisfy this method from the pool, writing the files the engine reads.
    /// </summary>
    /// <returns>True when the working directory now holds a usable identity.</returns>
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

                    // Same call again, which hands out a DIFFERENT identity: the second
                    // leg exists so the two hops do not share an identity.
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
                // Do not keep a bad checkout recorded against this workDir; it would make
                // a later failure retire an identity that was never actually used.
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

    /// <summary>
    /// Retires the identities a failed connection was using, so the next attempt picks a
    /// different one instead of retrying a blocked identity forever.
    /// </summary>
    public void ReportConnectFailure(string workDir, string reason) => _pool.ReportFailure(workDir, reason);

    /// <summary>Records that a connection succeeded, clearing the active mapping.</summary>
    public void ReportConnectSuccess(string workDir) => _pool.ReportSuccess(workDir);

    /// <summary>
    /// Tops the pool up while connectivity exists.
    ///
    /// <summary>Tops up the identity pool while online connectivity is available.</summary>
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

    /// <summary>Provisions a single identity for the reserve pool.</summary>
    private async Task<PooledIdentity?> ProvisionOneAsync(IdentityKind kind, CancellationToken ct)
    {
        var relays = _settings.Settings.IdentityRelayUrls;
        var routeCount = 1 + IdentityRelay.ParseRelays(relays).Count;
        if (routeCount == 1) return null; // no relay configured and direct failed

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12 * routeCount + 5));

        var routes = IdentityRelay.BuildRoutes(
            directProxy: null, relayList: relays, tunnelProxy: null, tunnelLabel: "tunnel");

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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Identity] Failed starting SHARD for provisioning");
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Identity] Failed starting V2Ray fallback");
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
        System.Net.IWebProxy? proxy = socksPort.HasValue && socksPort.Value > 0
            ? new System.Net.WebProxy($"socks5://127.0.0.1:{socksPort.Value}")
            : null;
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
        IdentityFileGuard.RestrictToCurrentUser(workDir);

        var routes = new List<ProvisioningRoute>
        {
            new("direct", proxy, RelayBaseUrl: null),
        };
        if (extraRoutes is not null) routes.AddRange(extraRoutes);
        if (proxy is not null)
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
                    break;
                }

                // Classic gool is genuinely two independent WireGuard hops, so two devices.
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

    /// <summary>Stores an on-demand provisioned identity into the reserve pool.</summary>
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

    /// <summary>Sends registration request across candidate routes until successful.</summary>
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

        try
        {
            var respStr = await SendToRegistrationAsync(
                HttpMethod.Patch, $"reg/{reg.Id}", patchBody, reg.Token, routes, ct);

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
            _logger.LogWarning(ex,
                "[Identity] MASQUE key enrollment failed on every route; keeping the assigned endpoint");
        }

        return (certPem, keyPem, assignedEndpoint);
    }

    private static string BuildWarpToml(DeviceRegResult r) =>
$"""
device_id = "{r.Id}"
access_token = "{r.Token}"
cert_pem = ""
key_pem = ""
cert_issued_at = 0
ipv4 = "{r.Ipv4}"
ipv6 = "{r.Ipv6}"
wg_private_key = "{r.PrivateKeyBase64}"
wg_peer_public_key = "{r.PeerPublicKeyBase64}"
client_id = "{r.ClientId}"
organization = ""
gateway_proxy = "{r.GatewayProxy}"
assigned_endpoint = "{r.AssignedEndpoint}"
""";

    private static string BuildMasqueToml(DeviceRegResult r, string certPem, string keyPem, string assignedEndpoint)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"device_id = \"{r.Id}\"");
        sb.AppendLine($"access_token = \"{r.Token}\"");
        sb.AppendLine("cert_pem = \"\"\"");
        sb.AppendLine(certPem);
        sb.AppendLine("\"\"\"");
        sb.AppendLine("key_pem = \"\"\"");
        sb.AppendLine(keyPem);
        sb.AppendLine("\"\"\"");
        sb.AppendLine($"cert_issued_at = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        sb.AppendLine($"ipv4 = \"{r.Ipv4}\"");
        sb.AppendLine($"ipv6 = \"{r.Ipv6}\"");
        sb.AppendLine($"wg_private_key = \"{r.PrivateKeyBase64}\"");
        sb.AppendLine($"wg_peer_public_key = \"{r.PeerPublicKeyBase64}\"");
        sb.AppendLine($"client_id = \"{r.ClientId}\"");
        sb.AppendLine("organization = \"\"");
        sb.AppendLine($"gateway_proxy = \"{r.GatewayProxy}\"");
        sb.AppendLine($"assigned_endpoint = \"{assignedEndpoint}\"");
        return sb.ToString();
    }
}
