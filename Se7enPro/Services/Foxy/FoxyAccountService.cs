using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;
using Se7enPro.Services.Lantern;
using Se7enPro.Services.Veep;

namespace Se7enPro.Services.Foxy;

public sealed class FoxyAccountService
{
    private static readonly Uri FxaProbeUri = new("https://api.accounts.firefox.com/v1/account/status");

    private readonly ISettingsService _settings;
    private readonly ILogger<FoxyAccountService> _logger;
    private readonly ITunnelCoreManager? _tunnel;
    private readonly LanternEngine? _lanternEngine;
    private readonly VeepEngine? _veepEngine;
    private readonly AetherEngine? _aetherEngine;
    private readonly V2RayEngine? _v2rayEngine;
    private readonly FxaClient _fxa;
    private FoxyConnectProxy? _browserUpstreamProxy;
    private GuardianClient? _guardian;
    private string? _temporaryLoginProxy;

    public FoxyAccountService(
        ILogger<FoxyAccountService> logger,
        ISettingsService settings,
        ITunnelCoreManager? tunnel = null,
        LanternEngine? lanternEngine = null,
        VeepEngine? veepEngine = null,
        AetherEngine? aetherEngine = null,
        V2RayEngine? v2rayEngine = null)
    {
        _logger = logger;
        _settings = settings;
        _tunnel = tunnel;
        _lanternEngine = lanternEngine;
        _veepEngine = veepEngine;
        _aetherEngine = aetherEngine;
        _v2rayEngine = v2rayEngine;
        _fxa = new FxaClient(logger, EffectiveProxyProvider);
    }

    internal ConnectionMethod? ActiveMainTunnelMethod =>
        _tunnel is { State: ConnectionState.Connected } ? _tunnel.ActiveMethod : null;

    internal string DescribeConfiguredV2RayNode()
    {
        var cfg = _v2rayEngine?.ResolveActiveConfig();
        if (cfg is null || V2RayEngine.ValidateOutboundNode(cfg) is not null)
            return "";
        return !string.IsNullOrWhiteSpace(cfg.Name)
            ? cfg.Name.Trim()
            : $"{cfg.Address}:{cfg.Port}";
    }

    internal Task<FoxyLoginTransportSession> AcquireLoginTransportAsync(
        FoxyLoginTransportKind kind,
        Action<string>? onProgress = null,
        CancellationToken ct = default) =>
        FoxyLoginTransportStarter.StartAsync(
            kind,
            _tunnel,
            _lanternEngine,
            _veepEngine,
            _aetherEngine,
            _v2rayEngine,
            _logger,
            onProgress,
            ct);

    internal void SetTemporaryLoginProxy(string? socksEndpoint) =>
        _temporaryLoginProxy = string.IsNullOrWhiteSpace(socksEndpoint) ? null : socksEndpoint.Trim();

    internal System.Net.IWebProxy? EffectiveProxyProvider()
    {
        if (!string.IsNullOrWhiteSpace(_temporaryLoginProxy))
        {
            return new System.Net.WebProxy("socks5://" + _temporaryLoginProxy);
        }

        if (!string.IsNullOrWhiteSpace(FoxyEngine.UpstreamProxyOverride))
        {
            return new System.Net.WebProxy("socks5://" + FoxyEngine.UpstreamProxyOverride.Trim());
        }

        return FoxyNetwork.ResolveEffectiveProxy(
            _settings.Settings,
            _tunnel,
            FxaProbeUri,
            FoxyConnectProxy.IsOwnProxyUri);
    }

    public Task<FxaLoginResult> StartLoginAsync(
        string email,
        string password,
        CancellationToken ct = default,
        Func<string, CancellationToken, Task<string>>? captchaSolver = null) =>
        _fxa.StartLoginAsync(email, password, ct, captchaSolver);

    public Task<FxaLoginResult> SubmitVerificationCodeAsync(
        string email,
        string sessionToken,
        string code,
        CancellationToken ct = default,
        Func<string, CancellationToken, Task<string>>? captchaSolver = null) =>
        _fxa.SubmitVerificationCodeAsync(email, sessionToken, code, ct, captchaSolver);

    public Task<FxaLoginResult> CompleteBrowserLoginAsync(
        string? email,
        string sessionToken,
        CancellationToken ct = default) =>
        _fxa.CompleteBrowserLoginAsync(email, sessionToken, ct);

    internal string GetBrowserProxyServerArg()
    {
        var proxy = EffectiveProxyProvider();
        if (proxy is not null && !proxy.IsBypassed(FxaProbeUri))
        {
            var pUri = proxy.GetProxy(FxaProbeUri);
            if (pUri is not null && !pUri.Equals(FxaProbeUri) && !FoxyConnectProxy.IsOwnProxyUri(pUri))
            {
                var cred = proxy.Credentials?.GetCredential(pUri, "Basic");
                if (cred is not null && !string.IsNullOrEmpty(cred.UserName))
                {
                    _browserUpstreamProxy ??= new FoxyConnectProxy(EffectiveProxyProvider, restrictToSignInHosts: true);
                    _browserUpstreamProxy.EnsureStarted();
                    return _browserUpstreamProxy.WebViewProxyArg;
                }
                return $"{pUri.Scheme}://{pUri.Host}:{pUri.Port}";
            }
        }

        FoxyConnectProxy.Default.EnsureStarted();
        return FoxyConnectProxy.Default.WebViewProxyArg;
    }

    public FirefoxAccountInfo StoreAccount(FirefoxAccountInfo account)
    {
        var accounts = _settings.Settings.FirefoxAccounts;

        var existing = accounts.FirstOrDefault(a =>
            string.Equals(a.Email, account.Email, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.SessionToken = account.SessionToken;
            existing.AccessToken = account.AccessToken;
            existing.RefreshToken = account.RefreshToken;
            existing.TokenExpiryEpochSeconds = account.TokenExpiryEpochSeconds;
            existing.AddedAtUtc = DateTime.UtcNow;
            existing.QuotaErrorMessage = "";
            account = existing;
        }
        else
        {
            accounts.Add(account);
        }

        if (string.IsNullOrEmpty(_settings.Settings.FirefoxActiveAccountId) || accounts.Count == 1)
            SetActive(account);
        else
            _settings.Save();

        return account;
    }

    public void SetActive(FirefoxAccountInfo account)
    {
        foreach (var a in _settings.Settings.FirefoxAccounts) a.IsActive = ReferenceEquals(a, account);
        _settings.Settings.FirefoxActiveAccountId = account.Id;
        _settings.Save();
    }

    public void Remove(FirefoxAccountInfo account)
    {
        _settings.Settings.FirefoxAccounts.Remove(account);

        if (string.Equals(_settings.Settings.FirefoxActiveAccountId, account.Id, StringComparison.Ordinal))
        {
            var next = _settings.Settings.FirefoxAccounts.FirstOrDefault();
            _settings.Settings.FirefoxActiveAccountId = next?.Id ?? "";
            foreach (var a in _settings.Settings.FirefoxAccounts) a.IsActive = ReferenceEquals(a, next);
        }
        _settings.Save();
    }

    public async Task RefreshQuotaAsync(FirefoxAccountInfo account, CancellationToken ct = default)
    {
        if (!account.HasUsableCredentials)
        {
            account.QuotaErrorMessage = "Sign in again: the stored tokens are unreadable.";
            return;
        }

        account.IsRefreshing = true;
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (account.TokenExpiryEpochSeconds <= now + 300)
                await _fxa.RefreshAccessTokenAsync(account, ct, () => _settings.Save()).ConfigureAwait(false);

            ProxyPassResult? pass = null;
            for (var attempt = 0; attempt < 2 && pass is null; attempt++)
            {
                try
                {
                    pass = await Guardian.FetchProxyPassAsync(account.AccessToken, ct).ConfigureAwait(false);
                }
                catch (FirefoxAuthTokenInvalidException) when (attempt == 0)
                {
                    if (!await _fxa.RefreshAccessTokenAsync(account, ct, () => _settings.Save()).ConfigureAwait(false))
                        throw;
                }
            }

            if (pass is null)
            {
                account.QuotaErrorMessage = "Mozilla did not return a quota for this account.";
                return;
            }

            if (pass.QuotaLimit.HasValue) account.QuotaLimitBytes = pass.QuotaLimit.Value;
            if (pass.QuotaRemaining.HasValue) account.QuotaRemainingBytes = pass.QuotaRemaining.Value;
            if (pass.QuotaReset.HasValue) account.QuotaResetEpochSeconds = pass.QuotaReset.Value;
            account.ProxyPassToken = pass.Token;
            account.ProxyPassExpiryEpochSeconds = pass.ExpiresAtEpochSeconds;
            account.LastQuotaCheckUtc = DateTime.UtcNow;
            account.QuotaErrorMessage = "";
            _settings.Save();
        }
        catch (FirefoxQuotaExceededException)
        {
            account.QuotaRemainingBytes = 0;
            account.LastQuotaCheckUtc = DateTime.UtcNow;
            account.QuotaErrorMessage = "";
            _settings.Save();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quota refresh failed for {Email}: {Message}", account.Email, ex.Message);
            account.QuotaErrorMessage = ex.Message;
            account.LastQuotaCheckUtc = DateTime.UtcNow;
        }
        finally
        {
            account.IsRefreshing = false;
        }
    }

    public async Task RefreshAllQuotasAsync(CancellationToken ct = default)
    {
        foreach (var account in _settings.Settings.FirefoxAccounts.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            await RefreshQuotaAsync(account, ct).ConfigureAwait(false);
        }
    }

    private GuardianClient Guardian => _guardian ??= new GuardianClient(_logger, proxyProvider: EffectiveProxyProvider);
}
