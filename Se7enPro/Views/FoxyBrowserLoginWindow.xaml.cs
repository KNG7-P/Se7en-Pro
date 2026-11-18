using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Se7enPro.Models;
using Se7enPro.Services.Foxy;

namespace Se7enPro.Views;

public partial class FoxyBrowserLoginWindow : Window
{
    private const string FirefoxUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:158.0) Gecko/20100101 Firefox/158.0";
    private const string BaseLoginUrl = "https://accounts.firefox.com/?context=fx_desktop_v3&service=sync&action=email";
    private const string DefaultChannelId = "account_updates";
    private const string CmdFxaStatus = "fxaccounts:fxa_status";
    private const string CmdCanLinkAccount = "fxaccounts:can_link_account";
    private const string CmdLogin = "fxaccounts:login";
    private const string CmdOAuthLogin = "fxaccounts:oauth_login";

    private static readonly Regex SessionTokenHexRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private const string WebChannelHookJs = """
        (function() {
          if (window.__foxyChannelHooked) return;
          window.__foxyChannelHooked = true;
          var send = function(o) {
            try {
              var host = window.location.hostname;
              if (host !== 'accounts.firefox.com' && !host.endsWith('.firefox.com')) return;
              var payload = typeof o === 'string' ? o : JSON.stringify(o);
              if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(payload);
              }
            } catch (e) {}
          };
          var onChannel = function(e) {
            var d = e.detail;
            if (typeof d === 'string') { try { d = JSON.parse(d); } catch (ex) { return; } }
            if (d && d.message) send(d);
          };
          window.addEventListener('WebChannelMessageToChrome', onChannel);
          document.addEventListener('WebChannelMessageToChrome', onChannel);
          window.__foxyReply = function(id, messageId, command, data) {
            var detail = { id: id, message: { messageId: messageId, command: command, data: data } };
            window.dispatchEvent(new CustomEvent('WebChannelMessageToContent', { detail: detail }));
            document.dispatchEvent(new CustomEvent('WebChannelMessageToContent', { detail: detail }));
          };
        })();
        """;

    private readonly Func<string, string, CancellationToken, Task<FxaLoginResult>>? _completeLoginAsync;
    private readonly string _initialEmail;
    private readonly string? _proxyServerArg;
    private readonly string _userDataFolder;
    private string? _capturedSessionToken;
    private CancellationTokenSource? _followCts;

    public (string Email, string SessionToken)? Result { get; private set; }
    public FirefoxAccountInfo? AccountResult { get; private set; }

    public FoxyBrowserLoginWindow(
        string? proxyServerArg = null,
        string? initialEmail = null,
        Func<string, string, CancellationToken, Task<FxaLoginResult>>? completeLoginAsync = null)
    {
        _proxyServerArg = proxyServerArg;
        _initialEmail = (initialEmail ?? "").Trim();
        _completeLoginAsync = completeLoginAsync;
        _userDataFolder = Path.Combine(Path.GetTempPath(), "Se7enPro_Fxa_" + Guid.NewGuid().ToString("N"));
        InitializeComponent();
        Loaded += OnLoadedAsync;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            CoreWebView2EnvironmentOptions? options = null;
            if (!string.IsNullOrWhiteSpace(_proxyServerArg))
                options = new CoreWebView2EnvironmentOptions($"--proxy-server=\"{_proxyServerArg}\"");

            var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, options);
            await BrowserView.EnsureCoreWebView2Async(env);

            var core = BrowserView.CoreWebView2;
            core.Settings.UserAgent = FirefoxUserAgent;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.DocumentTitleChanged += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(core.DocumentTitle))
                    TitleText.Text = core.DocumentTitle;
            };
            core.WebMessageReceived += OnWebMessageReceived;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebChannelHookJs);
            var targetUrl = string.IsNullOrEmpty(_initialEmail)
                ? BaseLoginUrl
                : BaseLoginUrl + "&email=" + Uri.EscapeDataString(_initialEmail);
            core.Navigate(targetUrl);
        }
        catch (Exception ex)
        {
            LoadProgress.Visibility = Visibility.Collapsed;
            ShowError($"Unable to initialize embedded browser (WebView2): {ex.Message}");
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        LoadProgress.Visibility = Visibility.Visible;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            return;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
            return;
        }

        if (!IsAllowedHost(uri.Host))
            e.Cancel = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadProgress.Visibility = Visibility.Collapsed;
        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            ShowError($"Page load failed ({e.WebErrorStatus}). Check your connection or proxy settings.");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var sourceUri) ||
            !IsFirefoxHost(sourceUri.Host))
        {
            return;
        }

        string payload;
        try
        {
            payload = e.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(payload))
            return;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var channelId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? DefaultChannelId
                : DefaultChannelId;
            if (string.IsNullOrWhiteSpace(channelId))
                channelId = DefaultChannelId;

            if (!root.TryGetProperty("message", out var msgEl) || msgEl.ValueKind != JsonValueKind.Object)
                return;

            var command = msgEl.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() ?? "" : "";
            var messageId = msgEl.TryGetProperty("messageId", out var midEl) ? midEl.GetString() ?? "" : "";
            var hasData = msgEl.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object;

            switch (command)
            {
                case CmdFxaStatus:
                    ReplyWebChannel(channelId, messageId, CmdFxaStatus, "{\"capabilities\":{},\"signedInUser\":null}");
                    break;
                case CmdCanLinkAccount:
                    ReplyWebChannel(channelId, messageId, CmdCanLinkAccount, "{\"ok\":true}");
                    break;
                case CmdLogin:
                case CmdOAuthLogin:
                    if (!string.IsNullOrEmpty(messageId))
                        ReplyWebChannel(channelId, messageId, command, "{\"ok\":true}");

                    if (hasData)
                    {
                        var token = ReadString(dataEl, "sessionToken");
                        if (string.IsNullOrEmpty(token))
                            token = ReadString(dataEl, "token");
                        var email = ReadString(dataEl, "email");

                        if (SessionTokenHexRegex.IsMatch(token) &&
                            !string.Equals(token, _capturedSessionToken, StringComparison.Ordinal))
                        {
                            _capturedSessionToken = token;
                            var resolvedEmail = string.IsNullOrEmpty(email) ? _initialEmail : email;
                            Result = (resolvedEmail, token);
                            if (_completeLoginAsync is null)
                            {
                                DialogResult = true;
                                Close();
                            }
                            else
                            {
                                StartFollowingSession(resolvedEmail, token);
                            }
                        }
                    }
                    break;
                default:
                    if (!string.IsNullOrEmpty(messageId))
                        ReplyWebChannel(channelId, messageId, command, "{\"ok\":true}");
                    break;
            }
        }
        catch
        {
        }
    }

    private void StartFollowingSession(string email, string sessionToken)
    {
        _followCts?.Cancel();
        _followCts?.Dispose();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _followCts = cts;
        ErrorBanner.Visibility = Visibility.Collapsed;
        LoadProgress.Visibility = Visibility.Visible;

        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var res = await _completeLoginAsync!(email, sessionToken, cts.Token).ConfigureAwait(false);
                    if (res.Success && res.Account is not null)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (!IsLoaded) return;
                            AccountResult = res.Account;
                            DialogResult = true;
                            Close();
                        });
                        return;
                    }

                    var msg = res.ErrorMessage ?? "";
                    var isRateLimited =
                        msg.Contains("429", StringComparison.Ordinal) ||
                        msg.Contains("too many", StringComparison.OrdinalIgnoreCase);
                    var awaitingVerification = !isRateLimited && (
                        msg.Contains("104", StringComparison.Ordinal) ||
                        msg.Contains("138", StringComparison.Ordinal) ||
                        msg.Contains("unverified", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("verify", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("confirm", StringComparison.OrdinalIgnoreCase));
                    if (!awaitingVerification && !string.IsNullOrWhiteSpace(msg))
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            LoadProgress.Visibility = Visibility.Collapsed;
                            ShowError(msg);
                        });
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LoadProgress.Visibility = Visibility.Collapsed;
                        ShowError(ex.Message);
                    });
                    return;
                }

                try
                {
                    await Task.Delay(3000, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, cts.Token);
    }

    private void ReplyWebChannel(string channelId, string messageId, string command, string dataJson)
    {
        var core = BrowserView.CoreWebView2;
        if (core is null)
            return;

        var script =
            $"window.__foxyReply && window.__foxyReply({JsonSerializer.Serialize(channelId)}, {JsonSerializer.Serialize(messageId)}, {JsonSerializer.Serialize(command)}, {dataJson});";
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _ = core.ExecuteScriptAsync(script);
            }
            catch
            {
            }
        });
    }

    private static string ReadString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? "").Trim()
            : "";

    private static bool IsFirefoxHost(string host)
    {
        var h = host.ToLowerInvariant();
        return h == "accounts.firefox.com" || h.EndsWith(".firefox.com", StringComparison.Ordinal);
    }

    private static bool IsAllowedHost(string host)
    {
        var h = host.ToLowerInvariant();
        return IsFirefoxHost(h) ||
               h == "mozilla.org" || h.EndsWith(".mozilla.org", StringComparison.Ordinal) ||
               h == "mozilla.net" || h.EndsWith(".mozilla.net", StringComparison.Ordinal) ||
               h == "challenges.cloudflare.com" ||
               h == "hcaptcha.com" || h.EndsWith(".hcaptcha.com", StringComparison.Ordinal) ||
               h == "recaptcha.net" || h.EndsWith(".recaptcha.net", StringComparison.Ordinal) ||
               h == "google.com" || h.EndsWith(".google.com", StringComparison.Ordinal) ||
               h == "gstatic.com" || h.EndsWith(".gstatic.com", StringComparison.Ordinal) ||
               h == "googleapis.com" || h.EndsWith(".googleapis.com", StringComparison.Ordinal) ||
               h == "apple.com" || h.EndsWith(".apple.com", StringComparison.Ordinal);
    }

    private void ShowError(string message)
    {
        ErrorBannerText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            _followCts?.Cancel();
            _followCts?.Dispose();
        }
        catch
        {
        }
        try
        {
            BrowserView.Dispose();
        }
        catch
        {
        }
        try
        {
            if (Directory.Exists(_userDataFolder))
                Directory.Delete(_userDataFolder, recursive: true);
        }
        catch
        {
        }
    }
}
