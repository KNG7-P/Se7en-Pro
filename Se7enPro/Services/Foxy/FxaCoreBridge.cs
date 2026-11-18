using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services.Foxy;

internal static class FxaCoreBridge
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _proc;
    private static string? _listenAddr;

    public static string ExecutablePath =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "foxy", "Se7enPro.FxaCore.exe");

    public static bool IsAvailable => File.Exists(ExecutablePath);

    public static async Task<string?> EnsureStartedAsync(ILogger? logger, CancellationToken ct)
    {
        if (_proc is { HasExited: false } && !string.IsNullOrEmpty(_listenAddr))
            return _listenAddr;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_proc is { HasExited: false } && !string.IsNullOrEmpty(_listenAddr))
                return _listenAddr;

            CleanupLocked();

            if (!IsAvailable)
                return null;

            var psi = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var proc = Process.Start(psi);
            if (proc is null)
                return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                if (line is null)
                    break;
                if (line.StartsWith("READY ", StringComparison.Ordinal))
                {
                    _proc = proc;
                    _listenAddr = line["READY ".Length..].Trim();
                    logger?.LogInformation("Started Se7enPro.FxaCore.exe on {Addr}", _listenAddr);
                    return _listenAddr;
                }
            }

            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to start Se7enPro.FxaCore.exe; falling back to .NET TLS.");
            CleanupLocked();
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void CleanupLocked()
    {
        _listenAddr = null;
        if (_proc is null) return;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc.Dispose(); } catch { }
        _proc = null;
    }
}

internal sealed class FxaCoreDelegatingHandler : DelegatingHandler
{
    private readonly CookieContainer _cookies;
    private readonly Func<IWebProxy?>? _proxyProvider;
    private readonly ILogger? _logger;

    public FxaCoreDelegatingHandler(
        HttpMessageHandler innerHandler,
        CookieContainer cookies,
        Func<IWebProxy?>? proxyProvider = null,
        ILogger? logger = null,
        FoxyConnectProxy? directConnectProxy = null) : base(innerHandler)
    {
        _cookies = cookies;
        _proxyProvider = proxyProvider;
        _logger = logger;
        _ = directConnectProxy;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var originalUri = request.RequestUri;
        if (originalUri is null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        ApplyRequestCookies(request, originalUri);

        if (originalUri.Host.EndsWith(".firefox.com", StringComparison.OrdinalIgnoreCase) &&
            FxaCoreBridge.IsAvailable)
        {
            var addr = await FxaCoreBridge.EnsureStartedAsync(_logger, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(addr))
            {
                request.Headers.Remove("X-Fxa-Target");
                request.Headers.Remove("X-Fxa-Proxy");
                request.Headers.TryAddWithoutValidation("X-Fxa-Target", originalUri.AbsoluteUri);

                var proxyUri = ResolveProxyUri(originalUri);
                if (!string.IsNullOrEmpty(proxyUri))
                    request.Headers.TryAddWithoutValidation("X-Fxa-Proxy", proxyUri);

                request.RequestUri = new Uri($"http://{addr}/");
                try
                {
                    var resp = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    CaptureResponseCookies(resp, originalUri);
                    return resp;
                }
                finally
                {
                    request.RequestUri = originalUri;
                }
            }
        }

        var fallbackResp = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        CaptureResponseCookies(fallbackResp, originalUri);
        return fallbackResp;
    }

    private void ApplyRequestCookies(HttpRequestMessage request, Uri targetUri)
    {
        request.Headers.Remove("Cookie");
        var header = _cookies.GetCookieHeader(targetUri);
        if (!string.IsNullOrEmpty(header))
            request.Headers.TryAddWithoutValidation("Cookie", header);
    }

    private void CaptureResponseCookies(HttpResponseMessage response, Uri targetUri)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            return;
        foreach (var raw in setCookies)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                StoreParsedCookie(targetUri, raw);
            }
            catch
            {
                try { _cookies.SetCookies(targetUri, raw); } catch { }
            }
        }
    }

    private void StoreParsedCookie(Uri targetUri, string rawHeader)
    {
        var parts = rawHeader.Split(';');
        if (parts.Length == 0) return;

        var first = parts[0].Trim();
        var eq = first.IndexOf('=');
        if (eq <= 0) return;

        var name = first[..eq].Trim();
        var value = first[(eq + 1)..].Trim();
        if (string.IsNullOrEmpty(name)) return;

        var path = "/";
        var domain = targetUri.Host;
        var httpOnly = false;
        long? maxAgeSeconds = null;

        for (var i = 1; i < parts.Length; i++)
        {
            var attr = parts[i].Trim();
            if (attr.Length == 0) continue;

            if (attr.Equals("httponly", StringComparison.OrdinalIgnoreCase))
            {
                httpOnly = true;
            }
            else if (attr.StartsWith("path=", StringComparison.OrdinalIgnoreCase))
            {
                var p = attr[5..].Trim();
                if (!string.IsNullOrEmpty(p))
                    path = p;
            }
            else if (attr.StartsWith("domain=", StringComparison.OrdinalIgnoreCase))
            {
                var d = attr[7..].Trim().TrimStart('.');
                if (!string.IsNullOrEmpty(d))
                    domain = d;
            }
            else if (attr.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(attr[8..].Trim(), out var parsedMaxAge))
                    maxAgeSeconds = parsedMaxAge;
            }
        }

        if (!targetUri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
            !targetUri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
        {
            domain = targetUri.Host;
        }

        var isFastlyCookie = name.StartsWith("_fs_ch_", StringComparison.OrdinalIgnoreCase);
        var mirrorUris = isFastlyCookie
            ? new[]
            {
                new Uri("https://accounts.firefox.com/"),
                new Uri("https://api.accounts.firefox.com/"),
                new Uri("https://oauth.accounts.firefox.com/"),
            }
            : Array.Empty<Uri>();

        ExpireMatchingCookies(targetUri, name);
        foreach (var mirror in mirrorUris)
            ExpireMatchingCookies(mirror, name);

        if (maxAgeSeconds.HasValue && maxAgeSeconds.Value <= 0 && string.IsNullOrEmpty(value))
            return;

        var expiresUtc = maxAgeSeconds.HasValue && maxAgeSeconds.Value > 600
            ? DateTime.UtcNow.AddSeconds(maxAgeSeconds.Value)
            : DateTime.UtcNow.AddMinutes(10);

        _cookies.Add(targetUri, new Cookie(name, value, path, targetUri.Host)
        {
            Secure = true,
            HttpOnly = httpOnly,
            Expires = expiresUtc,
        });

        foreach (var mirror in mirrorUris)
        {
            if (string.Equals(mirror.Host, targetUri.Host, StringComparison.OrdinalIgnoreCase))
                continue;
            _cookies.Add(mirror, new Cookie(name, value, "/", mirror.Host)
            {
                Secure = true,
                HttpOnly = httpOnly,
                Expires = expiresUtc,
            });
        }
    }

    private void ExpireMatchingCookies(Uri uri, string name)
    {
        foreach (Cookie existing in _cookies.GetCookies(uri))
        {
            if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
                existing.Expired = true;
        }
    }

    internal string? ResolveProxyUri(Uri targetUri)
    {
        var proxy = _proxyProvider?.Invoke();
        if (proxy is not null && !proxy.IsBypassed(targetUri))
        {
            var pUri = proxy.GetProxy(targetUri);
            if (pUri is not null && !pUri.Equals(targetUri) && !FoxyConnectProxy.IsOwnProxyUri(pUri))
            {
                if (proxy.Credentials?.GetCredential(pUri, "Basic") is { } cred &&
                    !string.IsNullOrEmpty(cred.UserName))
                {
                    var ub = new UriBuilder(pUri)
                    {
                        UserName = cred.UserName,
                        Password = cred.Password ?? ""
                    };
                    return ub.Uri.AbsoluteUri;
                }
                return pUri.AbsoluteUri;
            }
        }

        return null;
    }
}
