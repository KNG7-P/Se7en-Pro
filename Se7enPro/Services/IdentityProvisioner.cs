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
    private readonly ShardEngine _shardEngine;
    private readonly V2RayEngine _v2rayEngine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IdentityProvisioner(
        ILogger<IdentityProvisioner> logger,
        ShardEngine shardEngine,
        V2RayEngine v2rayEngine)
    {
        _logger = logger;
        _shardEngine = shardEngine;
        _v2rayEngine = v2rayEngine;
    }

        public bool HasValidIdentity(string workDir, ConnectionMethod method)
    {
        try
        {
            return method switch
            {
                ConnectionMethod.WireGuard => IsValidWarpToml(Path.Combine(workDir, "aether.toml")),
                ConnectionMethod.WarpOnWarp => IsValidWarpToml(Path.Combine(workDir, "aether.toml"))
                                            && IsValidWarpToml(Path.Combine(workDir, "aether-secondary.toml")),
                ConnectionMethod.Masque => IsValidMasqueToml(Path.Combine(workDir, "aether-masque.toml")),
                _ => true,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect identity file in {WorkDir}", workDir);
            return false;
        }
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
                _logger.LogInformation("[Identity] Direct registration probe failed or blocked ({Msg}). Acquiring SHARD proxy...", ex.Message);
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

    private async Task ProvisionInternalAsync(
        string workDir,
        ConnectionMethod method,
        int? socksPort,
        CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        RestrictToCurrentUser(workDir);

        switch (method)
        {
            case ConnectionMethod.WireGuard:
            {
                var reg = await RegisterDeviceAsync(socksPort, ct);
                var toml = BuildWarpToml(reg);
                var target = Path.Combine(workDir, "aether.toml");
                await WriteSecretFileAsync(target, toml, ct);
                break;
            }

            case ConnectionMethod.WarpOnWarp:
            {
                var primary = await RegisterDeviceAsync(socksPort, ct);
                var tomlPrimary = BuildWarpToml(primary);
                var targetPrimary = Path.Combine(workDir, "aether.toml");
                await WriteSecretFileAsync(targetPrimary, tomlPrimary, ct);

                var secondary = await RegisterDeviceAsync(socksPort, ct);
                var tomlSecondary = BuildWarpToml(secondary);
                var targetSecondary = Path.Combine(workDir, "aether-secondary.toml");
                await WriteSecretFileAsync(targetSecondary, tomlSecondary, ct);
                break;
            }

            case ConnectionMethod.Masque:
            {
                var reg = await RegisterDeviceAsync(socksPort, ct);
                var (certPem, keyPem, assignedEndpoint) = await EnrollMasqueKeyAsync(reg, socksPort, ct);
                var toml = BuildMasqueToml(reg, certPem, keyPem, assignedEndpoint);
                var target = Path.Combine(workDir, "aether-masque.toml");
                await WriteSecretFileAsync(target, toml, ct);
                break;
            }
        }
    }

        private static async Task WriteSecretFileAsync(string target, string content, CancellationToken ct)
    {
        var temp = target + ".tmp";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
        File.Move(temp, target, overwrite: true);
    }

        private void RestrictToCurrentUser(string dir)
    {
        try
        {
            var sd = new System.Security.AccessControl.DirectorySecurity();
            sd.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var sids = new List<System.Security.Principal.SecurityIdentifier>
            {
                new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
            };
            try
            {
                using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (current.User is not null)
                {
                    sids.Add(new System.Security.Principal.SecurityIdentifier(current.User.Value!));
                }
            }
            catch {  }

            foreach (var sid in sids)
            {
                sd.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    sid,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                    System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            new System.IO.DirectoryInfo(dir).SetAccessControl(sd);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not restrict the identity work directory ACL");
        }
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

    private static async Task<DeviceRegResult> RegisterDeviceAsync(int? socksPort, CancellationToken ct)
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

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        if (socksPort.HasValue && socksPort.Value > 0)
        {
            handler.Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort.Value}");
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/{ApiVersion}/reg");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("CF-Client-Version", ClientVersion);

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Registration failed: {(int)resp.StatusCode} {resp.ReasonPhrase} - {errBody}");
        }

        var respJson = await resp.Content.ReadAsStringAsync(ct);
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

    private async Task<(string CertPem, string KeyPem, string AssignedEndpoint)> EnrollMasqueKeyAsync(
        DeviceRegResult reg,
        int? socksPort,
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

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        if (socksPort.HasValue && socksPort.Value > 0)
        {
            handler.Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort.Value}");
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"{ApiBaseUrl}/{ApiVersion}/reg/{reg.Id}");
        req.Content = new StringContent(patchBody, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("CF-Client-Version", ClientVersion);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {reg.Token}");

        using var resp = await client.SendAsync(req, ct);
        var assignedEndpoint = reg.AssignedEndpoint;
        if (resp.IsSuccessStatusCode)
        {
            var respStr = await resp.Content.ReadAsStringAsync(ct);
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
        else
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[Identity] MASQUE key enrollment returned non-success ({Status}): {Body}", resp.StatusCode, errBody);
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
