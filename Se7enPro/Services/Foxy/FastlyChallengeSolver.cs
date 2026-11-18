using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services.Foxy;

public sealed class FastlyChallengeSolver
{
    internal const string SolverUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:158.0) Gecko/20100101 Firefox/158.0";

    private const string PowAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private const string FxaApiHost = "https://api.accounts.firefox.com";
    private const string FxaSiteUrl = "https://accounts.firefox.com";

    private const int MaxPostBackRounds = 8;

    private static readonly Regex PrefixRegex = new("/_fs-ch-[A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex InitCallRegex = new(
        "init\\((\\[[^\\]]*\\]),\\s*\"([^\"]+)\",\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly HttpMessageHandler _handler;
    private readonly CookieContainer _cookies;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FastlyChallengeSolver(SocketsHttpHandler handler)
    {
        _handler = handler;
        if (handler.CookieContainer is null)
        {
            handler.CookieContainer = new CookieContainer();
            handler.UseCookies = true;
        }
        _cookies = handler.CookieContainer;
    }

    public FastlyChallengeSolver(HttpMessageHandler handler, CookieContainer cookies)
    {
        _handler = handler;
        _cookies = cookies;
    }

    public async Task SolveAndInstallAsync(
        Func<string, CancellationToken, Task<string>>? captchaSolver = null,
        CancellationToken ct = default,
        string? challengedBody = null,
        string? challengedOrigin = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Exception? lastError = null;
            if (!string.IsNullOrWhiteSpace(challengedBody) && IsChallengePage(challengedBody))
            {
                var origin = string.IsNullOrWhiteSpace(challengedOrigin) ? FxaApiHost : challengedOrigin.TrimEnd('/');
                try
                {
                    await SolveChallengeBodyAsync(origin, challengedBody, captchaSolver, ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            foreach (var baseUrl in new[] { FxaSiteUrl, FxaApiHost })
            {
                try
                {
                    await SolveOnHostAsync(baseUrl, captchaSolver, ct).ConfigureAwait(false);
                    return;
                }
                catch (NoChallengePageException)
                {
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError ??= ex;
                }
            }
            throw lastError ?? new NoChallengePageException();
        }
        finally
        {
            _gate.Release();
        }
    }

    private HttpClient CreateSolverClient()
    {
        var client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(180),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SolverUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
        return client;
    }

    private async Task SolveChallengeBodyAsync(
        string baseUrl,
        string page,
        Func<string, CancellationToken, Task<string>>? captchaSolver,
        CancellationToken ct)
    {
        using var client = CreateSolverClient();
        var prefix = PrefixRegex.Match(page).Value;
        if (prefix.Length == 0)
            throw new InvalidOperationException($"Fastly challenge prefix not found on {baseUrl}.");

        var prefixUrl = baseUrl.TrimEnd('/') + prefix;
        var script = await GetTextAsync(client, prefixUrl + "/script.js?reload=true", ct).ConfigureAwait(false);
        await CompletePostBackRoundsAsync(client, baseUrl.TrimEnd('/'), prefixUrl, script, captchaSolver, verifyProbe: false, ct).ConfigureAwait(false);
    }

    private async Task SolveOnHostAsync(
        string baseUrl,
        Func<string, CancellationToken, Task<string>>? captchaSolver,
        CancellationToken ct)
    {
        using var client = CreateSolverClient();
        var pageUrl = baseUrl + "/";

        string? prefixUrl = null;
        string? script = null;
        Exception? fetchError = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ClearStateCookies();
            var page = await GetTextAsync(client, pageUrl, ct).ConfigureAwait(false);
            if (!IsChallengePage(page)) throw new NoChallengePageException();

            var prefix = PrefixRegex.Match(page).Value;
            if (prefix.Length == 0)
                throw new InvalidOperationException($"Fastly challenge prefix not found on {baseUrl}.");

            prefixUrl = baseUrl + prefix;
            try
            {
                script = await GetTextAsync(client, prefixUrl + "/script.js?reload=true", ct).ConfigureAwait(false);
                fetchError = null;
                break;
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                throw;
            }
            catch (InvalidOperationException ex) when (attempt < 3 && ex.Message.Contains("HTTP 400", StringComparison.Ordinal))
            {
                fetchError = ex;
            }
        }

        if (fetchError is not null || string.IsNullOrEmpty(prefixUrl) || string.IsNullOrEmpty(script))
            throw fetchError ?? new InvalidOperationException($"Failed to load Fastly challenge script from {baseUrl}.");

        await CompletePostBackRoundsAsync(client, baseUrl, prefixUrl, script, captchaSolver, verifyProbe: true, ct).ConfigureAwait(false);
    }

    private async Task CompletePostBackRoundsAsync(
        HttpClient client,
        string baseUrl,
        string prefixUrl,
        string script,
        Func<string, CancellationToken, Task<string>>? captchaSolver,
        bool verifyProbe,
        CancellationToken ct)
    {
        var pageUrl = baseUrl + "/";
        var (challenges, token) = ParseChallengeInit(script);

        for (var round = 0; round < MaxPostBackRounds; round++)
        {
            var answers = new List<object>(challenges.Count);
            foreach (var challenge in challenges)
                answers.Add(await AnswerChallengeAsync(client, prefixUrl, token, challenge, captchaSolver, ct).ConfigureAwait(false));

            var payload = JsonSerializer.Serialize(new { token, data = answers });
            using var resp = await PostAsync(client, prefixUrl + "/fst-post-back", payload, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Fastly challenge post-back returned HTTP {(int)resp.StatusCode}.");

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (string.Equals(status, "success", StringComparison.Ordinal))
            {
                if (verifyProbe)
                {
                    var probe = await GetTextAsync(client, pageUrl, ct).ConfigureAwait(false);
                    if (IsChallengePage(probe))
                        throw new InvalidOperationException("Fastly challenge cookie was not accepted on this exit IP.");
                }
                CopyCookiesToApiHost(pageUrl);
                return;
            }

            if (!doc.RootElement.TryGetProperty("ch", out var ch) || ch.ValueKind != JsonValueKind.Array ||
                !doc.RootElement.TryGetProperty("tok", out var tok) || string.IsNullOrEmpty(tok.GetString()))
                throw new InvalidOperationException("Unexpected Fastly challenge post-back response.");

            challenges = ParseChallenges(ch);
            token = tok.GetString()!;
        }

        throw new InvalidOperationException($"Fastly challenge did not complete within {MaxPostBackRounds} rounds.");
    }

    private void ClearStateCookies()
    {
        var jar = _cookies;
        foreach (var host in new[] { FxaApiHost, FxaSiteUrl, "https://oauth.accounts.firefox.com" })
        {
            var uri = new Uri(host + "/");
            foreach (Cookie cookie in jar.GetCookies(uri))
            {
                if (cookie.Name.StartsWith("_fs_ch_", StringComparison.OrdinalIgnoreCase))
                    cookie.Expired = true;
            }
        }
    }

    private static bool IsChallengePage(string body) =>
        body.Contains("/_fs-ch-", StringComparison.Ordinal) &&
        body.Contains("Client Challenge", StringComparison.Ordinal);

    private static async Task<string> GetTextAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 406 && IsChallengePage(body))
                return body;
            throw new InvalidOperationException($"GET {url} returned HTTP {(int)resp.StatusCode}.");
        }
        return body;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.TryAddWithoutValidation("Origin", OriginOf(url));
        return await client.SendAsync(req, ct).ConfigureAwait(false);
    }

    private static string OriginOf(string url)
    {
        var idx = url.IndexOf("/_fs-ch-", StringComparison.Ordinal);
        return idx > 0 ? url[..idx] : url;
    }

    private static (List<JsonElement> Challenges, string Token) ParseChallengeInit(string script)
    {
        var matches = InitCallRegex.Matches(script);
        if (matches.Count == 0)
            throw new InvalidOperationException("Fastly challenge init call not found in the challenge script.");

        using var doc = JsonDocument.Parse(matches[^1].Groups[1].Value);
        var challenges = ParseChallenges(doc.RootElement);
        if (challenges.Count == 0)
            throw new InvalidOperationException("Fastly challenge list is empty.");
        return (challenges, matches[^1].Groups[2].Value);
    }

    private static List<JsonElement> ParseChallenges(JsonElement array)
    {
        var list = new List<JsonElement>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
            list.Add(item.Clone());
        return list;
    }

    private static async Task<object> AnswerChallengeAsync(
        HttpClient client,
        string prefixUrl,
        string token,
        JsonElement challenge,
        Func<string, CancellationToken, Task<string>>? captchaSolver,
        CancellationToken ct)
    {
        var type = challenge.TryGetProperty("ty", out var t) ? t.GetString() : null;
        var data = challenge.TryGetProperty("data", out var d) ? d : default;
        switch (type)
        {
            case "pow":
            {
                var baseStr = data.TryGetProperty("base", out var b) ? b.GetString() ?? "" : "";
                var hash = data.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";
                var answer = SolvePow(baseStr, hash)
                             ?? throw new InvalidOperationException("No proof-of-work solution found.");
                return new
                {
                    ty = "pow",
                    @base = baseStr,
                    answer,
                    hmac = data.TryGetProperty("hmac", out var m) ? m.GetString() : null,
                    expires = data.TryGetProperty("expires", out var e) ? e.GetString() : null,
                };
            }
            case "pat":
                return new { ty = "pat", auth = await FetchPatAsync(client, prefixUrl, token, ct).ConfigureAwait(false) };
            case "clientmetrics":
                return ClientMetricsAnswer();
            case "captcha":
            {
                if (captchaSolver is null)
                {
                    throw new InvalidOperationException(
                        "Mozilla's sign-in now asks for an image CAPTCHA. Sign in once at accounts.firefox.com, then retry here.");
                }
                var imageB64 = data.TryGetProperty("image_b64", out var img) ? img.GetString() : null;
                if (string.IsNullOrEmpty(imageB64) ||
                    !imageB64.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Unrecognized Fastly CAPTCHA challenge data.");
                }
                var answer = await captchaSolver(imageB64, ct).ConfigureAwait(false);
                return new { ty = "captcha", answer = (answer ?? "").Trim() };
            }
            default:
                throw new InvalidOperationException($"Unsupported Fastly challenge type \"{type}\".");
        }
    }

    private static async Task<string> FetchPatAsync(
        HttpClient client, string prefixUrl, string token, CancellationToken ct)
    {
        var url = prefixUrl + "/pat?token=" + Uri.EscapeDataString(token);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        req.Headers.Accept.ParseAdd("text/plain");
        req.Headers.TryAddWithoutValidation("Origin", OriginOf(prefixUrl));
        req.Content.Headers.ContentType = new("application/json");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            return "";
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Fastly PAT request returned HTTP {(int)resp.StatusCode}.");

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var auth = doc.RootElement.TryGetProperty("auth", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(auth))
            throw new InvalidOperationException("Fastly PAT response carried no auth token.");
        return auth;
    }

    private static object ClientMetricsAnswer() => new
    {
        ty = "clientmetrics",
        webdriver = false,
        bot_detection_result = new { bot_detected = false, bot_kind = (string?)null },
        browser_metrics = new { client_data = "{}", error_trace = (string?)null },
        detector_results = new { },
        v = 2,
    };

    public static string? SolvePow(string baseStr, string targetHex)
    {
        byte[] target;
        try
        {
            target = Convert.FromHexString(targetHex);
        }
        catch
        {
            return null;
        }
        if (target.Length != 32) return null;

        using var sha = SHA256.Create();
        var buffer = new byte[Encoding.UTF8.GetByteCount(baseStr) + 2];
        var prefixLength = Encoding.UTF8.GetBytes(baseStr, buffer);
        Span<byte> hash = stackalloc byte[32];

        foreach (var a in PowAlphabet)
        {
            buffer[prefixLength] = (byte)a;
            foreach (var b in PowAlphabet)
            {
                buffer[prefixLength + 1] = (byte)b;
                sha.TryComputeHash(buffer, hash, out _);
                if (hash.SequenceEqual(target)) return new string(new[] { a, b });
            }
        }
        return null;
    }

    private void CopyCookiesToApiHost(string pageUrl)
    {
        var jar = _cookies;
        var pageUri = new Uri(pageUrl);
        var pageCookies = jar.GetCookies(pageUri);
        if (pageCookies.Count == 0)
            throw new InvalidOperationException("Fastly challenge completed but no cookies were issued.");

        foreach (var target in new[] { FxaSiteUrl + "/", FxaApiHost + "/", "https://oauth.accounts.firefox.com/" })
        {
            var targetUri = new Uri(target);
            if (string.Equals(targetUri.Host, pageUri.Host, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (Cookie c in pageCookies)
            {
                if (c.Expired) continue;
                jar.Add(targetUri, new Cookie(c.Name, c.Value, "/", targetUri.Host)
                {
                    Secure = true,
                    HttpOnly = c.HttpOnly,
                    Expires = c.Expires != DateTime.MinValue ? c.Expires : DateTime.UtcNow.AddMinutes(30),
                });
            }
        }
    }

    private sealed class NoChallengePageException : Exception
    {
        public NoChallengePageException() : base("Host did not serve a Fastly challenge page.") { }
    }
}
