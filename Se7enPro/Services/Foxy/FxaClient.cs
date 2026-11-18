using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Se7enPro.Models;

namespace Se7enPro.Services.Foxy;

public sealed class FxaClient
{
    private const string FxaAuthServer = "https://api.accounts.firefox.com/v1";
    private const string FxaOAuthTokenUrl = "https://oauth.accounts.firefox.com/v1/token";
    private const string ClientId = "5882386c6d801776";
    private const string OAuthScope = "profile https://identity.mozilla.com/apps/vpn";
    private const int FxaErrnoInvalidParameter = 107;

    private readonly ILogger _logger;
    private readonly CookieContainer _cookies = new();
    private readonly FastlyChallengeSolver _challengeSolver;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public FxaClient(ILogger logger, Func<IWebProxy?>? proxyProvider = null)
    {
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = FoxyNetwork.ConnectDirectWithDohFallbackAsync,
            UseProxy = false,
            Proxy = null,
        };

        var coreHandler = new FxaCoreDelegatingHandler(handler, _cookies, proxyProvider, logger);
        _challengeSolver = new FastlyChallengeSolver(coreHandler, _cookies);
        _http = new HttpClient(coreHandler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public async Task<FxaLoginResult> StartLoginAsync(
        string email,
        string password,
        CancellationToken ct = default,
        Func<string, CancellationToken, Task<string>>? captchaSolver = null)
    {
        email = email.Trim();
        password = password.Trim();
        try
        {
            var authPw = FxaCrypto.DeriveAuthPw(email, password);
            var (doc, sessionToken) = await LoginAttemptAsync(email, authPw, withVerificationMethod: true, captchaSolver, ct)
                .ConfigureAwait(false);

            var verified = doc.RootElement.TryGetProperty("verified", out var v) && v.GetBoolean();
            if (!verified)
            {
                _logger.LogInformation("FxA login initiated for {Email}; email verification code required.", email);
                return new FxaLoginResult(false, Requires2Fa: true, sessionToken, null, null);
            }

            return new FxaLoginResult(true, false, sessionToken,
                await CompleteLoginAsync(email, sessionToken, captchaSolver, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FxA login failed for {Email}: {Message}", email, ex.Message);
            return new FxaLoginResult(false, false, null, null, ex.Message);
        }
    }

    public async Task<FxaLoginResult> SubmitVerificationCodeAsync(
        string email,
        string sessionToken,
        string code,
        CancellationToken ct = default,
        Func<string, CancellationToken, Task<string>>? captchaSolver = null)
    {
        email = email.Trim();
        code = code.Trim();
        try
        {
            var body = JsonSerializer.Serialize(new { code });
            await FxaRequestAsync("POST", "/session/verify_code", sessionToken, body, ct, captchaSolver).ConfigureAwait(false);
            return new FxaLoginResult(true, false, sessionToken,
                await CompleteLoginAsync(email, sessionToken, captchaSolver, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FxA verification failed for {Email}: {Message}", email, ex.Message);
            return new FxaLoginResult(false, true, sessionToken, null, ex.Message);
        }
    }

    public async Task<FxaLoginResult> CompleteBrowserLoginAsync(
        string? email,
        string sessionToken,
        CancellationToken ct = default)
    {
        email = email?.Trim() ?? "";
        sessionToken = sessionToken.Trim();
        try
        {
            if (string.IsNullOrWhiteSpace(sessionToken))
                return new FxaLoginResult(false, false, null, null, "Firefox browser login did not return a session token.");

            var account = await CompleteLoginAsync(email, sessionToken, null, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(account.Email))
            {
                var fetchedEmail = await TryFetchProfileEmailAsync(account.AccessToken, ct).ConfigureAwait(false);
                account.Email = string.IsNullOrWhiteSpace(fetchedEmail) ? "firefox-account@mozilla" : fetchedEmail;
            }
            return new FxaLoginResult(true, false, sessionToken, account, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FxA browser login completion failed: {Message}", ex.Message);
            return new FxaLoginResult(false, false, null, null, ex.Message);
        }
    }

    private async Task<string?> TryFetchProfileEmailAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://profile.accounts.firefox.com/v1/profile");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString()?.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> RefreshAccessTokenAsync(
        FirefoxAccountInfo account, CancellationToken ct = default, Action? persistUnderLock = null)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            _logger.LogWarning("Cannot refresh Firefox account {Email}: refresh token is empty.", account.Email);
            return false;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                client_id = ClientId,
                grant_type = "refresh_token",
                refresh_token = account.RefreshToken,
                scope = OAuthScope,
            });
            using var doc = await FxaRequestAsync("POST", "/oauth/token", null, body, ct).ConfigureAwait(false);

            var accessToken = doc.RootElement.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogWarning("FxA refresh for {Email} returned no access_token.", account.Email);
                return false;
            }

            var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            long? expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s)
                ? s : null;
            var expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (expiresIn ?? 3600);

            account.ApplyRefreshedTokens(accessToken,
                string.IsNullOrWhiteSpace(refreshToken) ? account.RefreshToken : refreshToken, expiry, persistUnderLock);
            _logger.LogInformation("Refreshed access token for Firefox account {Email}.", account.Email);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh token for {Email}: {Message}", account.Email, ex.Message);
            return false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<FirefoxAccountInfo> CompleteLoginAsync(
        string email,
        string sessionToken,
        Func<string, CancellationToken, Task<string>>? captchaSolver,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            client_id = ClientId,
            grant_type = "fxa-credentials",
            scope = OAuthScope,
            access_type = "offline",
        });

        JsonDocument doc;
        try
        {
            doc = await FxaRequestAsync("POST", "/oauth/token", sessionToken, body, ct, captchaSolver).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFastly406OrChallengeFailure(ex))
        {
            _logger.LogInformation("Direct /oauth/token hit Fastly 406; falling back to PKCE OAuth authorization code exchange.");
            doc = await ExchangeSessionWithOAuthCodeAsync(sessionToken, ct).ConfigureAwait(false);
        }

        using (doc)
        {
            var accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
            var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt64() : 86400;

            return new FirefoxAccountInfo
            {
                Email = email,
                SessionToken = sessionToken,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                TokenExpiryEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn,
                AddedAtUtc = DateTime.UtcNow,
                IsActive = true,
            };
        }
    }

    private static bool IsFastly406OrChallengeFailure(Exception ex) =>
        ex is FxaException { StatusCode: 406 } ||
        (ex is InvalidOperationException ioe &&
         (ioe.Message.Contains("CAPTCHA", StringComparison.OrdinalIgnoreCase) ||
          ioe.Message.Contains("Fastly", StringComparison.OrdinalIgnoreCase)));

    private async Task<JsonDocument> ExchangeSessionWithOAuthCodeAsync(string sessionToken, CancellationToken ct)
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64UrlEncode(challengeBytes);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var authorizeUrl = new Uri(FxaAuthServer + "/oauth/authorization");
        var authorizeBody = JsonSerializer.Serialize(new
        {
            client_id = ClientId,
            response_type = "code",
            scope = OAuthScope,
            state,
            access_type = "offline",
            code_challenge = challenge,
            code_challenge_method = "S256",
        });
        var authorizeBytes = Encoding.UTF8.GetBytes(authorizeBody);
        var (tokenId, hmacKey) = FxaCrypto.DeriveHawkCredentials(sessionToken);

        using var authReq = new HttpRequestMessage(HttpMethod.Post, authorizeUrl);
        ApplyOAuthJsonHeaders(authReq);
        authReq.Headers.TryAddWithoutValidation("Authorization",
            FxaCrypto.BuildHawkHeader("POST", authorizeUrl, tokenId, hmacKey, authorizeBytes));
        authReq.Content = new ByteArrayContent(authorizeBytes);
        authReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var authResp = await _http.SendAsync(authReq, ct).ConfigureAwait(false);
        var authText = await authResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!authResp.IsSuccessStatusCode)
            throw ParseFxaError(authText, (int)authResp.StatusCode);

        using var authDoc = JsonDocument.Parse(authText);
        var code = authDoc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        var respState = authDoc.RootElement.TryGetProperty("state", out var st) ? st.GetString() : null;
        if (string.IsNullOrEmpty(code) || !string.Equals(respState, state, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid OAuth authorization response from Firefox Accounts.");

        var tokenBody = JsonSerializer.Serialize(new
        {
            client_id = ClientId,
            grant_type = "authorization_code",
            code,
            code_verifier = verifier,
        });
        var tokenBytes = Encoding.UTF8.GetBytes(tokenBody);
        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, new Uri(FxaOAuthTokenUrl));
        ApplyOAuthJsonHeaders(tokenReq);
        tokenReq.Content = new ByteArrayContent(tokenBytes);
        tokenReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var tokenResp = await _http.SendAsync(tokenReq, ct).ConfigureAwait(false);
        var tokenText = await tokenResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!tokenResp.IsSuccessStatusCode)
            throw ParseFxaError(tokenText, (int)tokenResp.StatusCode);

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(tokenText) ? "{}" : tokenText);
    }

    private static void ApplyOAuthJsonHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", FastlyChallengeSolver.SolverUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json;q=0.9,*/*;q=0.2");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US, en");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        req.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        req.Headers.TryAddWithoutValidation("Priority", "u=4");
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static FxaException ParseFxaError(string text, int statusCode)
    {
        int? errno = null;
        var message = text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("errno", out var n)) errno = n.GetInt32();
            if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString() ?? text;
        }
        catch
        {
        }
        return new FxaException(message, errno, statusCode);
    }

    private async Task<(JsonDocument Doc, string SessionToken)> LoginAttemptAsync(
        string email,
        string authPw,
        bool withVerificationMethod,
        Func<string, CancellationToken, Task<string>>? captchaSolver,
        CancellationToken ct)
    {
        var body = withVerificationMethod
            ? JsonSerializer.Serialize(new { email, authPW = authPw, verificationMethod = "email-2fa" })
            : JsonSerializer.Serialize(new { email, authPW = authPw });
        try
        {
            var doc = await FxaRequestAsync("POST", "/account/login", null, body, ct, captchaSolver).ConfigureAwait(false);
            var sessionToken = doc.RootElement.GetProperty("sessionToken").GetString() ?? "";
            return (doc, sessionToken);
        }
        catch (FxaException ex) when (ex.Errno == FxaErrnoInvalidParameter && withVerificationMethod)
        {
            return await LoginAttemptAsync(email, authPw, false, captchaSolver, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonDocument> FxaRequestAsync(
        string method,
        string path,
        string? sessionToken,
        string body,
        CancellationToken ct,
        Func<string, CancellationToken, Task<string>>? captchaSolver = null)
    {
        var url = new Uri(FxaAuthServer + path);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        var captchaSolvedInRequest = false;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            req.Headers.TryAddWithoutValidation("User-Agent", FastlyChallengeSolver.SolverUserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

            if (!string.IsNullOrEmpty(sessionToken))
            {
                var (tokenId, hmacKey) = FxaCrypto.DeriveHawkCredentials(sessionToken);
                req.Headers.TryAddWithoutValidation("Authorization",
                    FxaCrypto.BuildHawkHeader(method, url, tokenId, hmacKey, bodyBytes));
            }

            req.Content = new ByteArrayContent(bodyBytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotAcceptable)
            {
                if (captchaSolvedInRequest)
                {
                    throw new FxaException(
                        "Fastly rejected the login request even after CAPTCHA verification. Use 'Sign in via Browser', enable Personal Relay, or switch your IP.",
                        null,
                        406);
                }
                _logger.LogWarning("FxA returned HTTP 406 (Fastly challenge); solving it...");
                Func<string, CancellationToken, Task<string>>? trackedSolver = captchaSolver is null
                    ? null
                    : async (dataUri, token) =>
                    {
                        captchaSolvedInRequest = true;
                        return await captchaSolver(dataUri, token).ConfigureAwait(false);
                    };
                await _challengeSolver.SolveAndInstallAsync(trackedSolver, ct, text, "https://api.accounts.firefox.com").ConfigureAwait(false);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                int? errno = null;
                var message = text;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("errno", out var n)) errno = n.GetInt32();
                    if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString() ?? text;
                }
                catch
                {
                }
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    message = string.IsNullOrWhiteSpace(message)
                        ? "Too many requests (HTTP 429). Mozilla temporarily rate-limited this IP; switch VPN/proxy IP or wait before retrying."
                        : $"{message} (HTTP 429 — switch IP/proxy or wait before retrying)";
                }
                throw new FxaException(message, errno, (int)resp.StatusCode);
            }

            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }

        throw new FxaException("Exceeded maximum retry attempts for Fastly challenge.", null, 406);
    }
}
