using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Models;
using Se7enPro.Services;
using Se7enPro.Services.Foxy;
using Se7enPro.Views;

namespace Se7enPro.ViewModels;

public sealed partial class FoxySettingsViewModel : ObservableObject
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ISettingsService _settings;
    private readonly FoxyAccountService _accountService;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private bool _isRefreshingAll;
    private bool _suppressSave;
    private CancellationTokenSource? _saveDebounceCts;

    private ObservableCollection<FirefoxAccountInfo> _accounts;

    public FoxySettingsViewModel(ISettingsService settings, FoxyAccountService accountService)
    {
        _settings = settings;
        _accountService = accountService;

        _accounts = settings.Settings.FirefoxAccounts;
        _accounts.CollectionChanged += OnAccountsChanged;
        _accountCount = _accounts.Count;

        _settings.SettingsChanged += OnSettingsChanged;

        SyncActiveFlags();
        LoadFromSettings();
    }

    public ObservableCollection<SettingOptionItem> ProxySchemes { get; } = new()
    {
        new("socks5", "SOCKS5 Proxy (Recommended)"),
        new("http", "HTTP / HTTPS Proxy"),
    };

    public bool IsDirectFoxyOnly => FoxyNetwork.IsDirectFoxyOnly(_settings.Settings);
    public bool IsChainedFoxyMode => !IsDirectFoxyOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomProxy))]
    private bool _customProxyEnabled;

    partial void OnCustomProxyEnabledChanged(bool value)
    {
        if (_suppressSave) return;
        _settings.Settings.FirefoxCustomProxyEnabled = value;
        ScheduleSave();
    }

    [ObservableProperty]
    private string _selectedCustomProxyScheme = "socks5";

    partial void OnSelectedCustomProxySchemeChanged(string value)
    {
        if (_suppressSave) return;
        _settings.Settings.FirefoxCustomProxyType = NormalizeScheme(value);
        ScheduleSave();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomProxy))]
    private string _customProxyHost = "";

    partial void OnCustomProxyHostChanged(string value)
    {
        if (_suppressSave) return;
        var trimmed = value?.Trim() ?? "";
        if (TryParseProxyInput(trimmed, out var scheme, out var host, out var port, out var user, out var pass))
        {
            _suppressSave = true;
            try
            {
                if (!string.IsNullOrEmpty(scheme)) SelectedCustomProxyScheme = NormalizeScheme(scheme);
                CustomProxyHost = host;
                if (port is > 0 and <= 65535) CustomProxyPort = port.ToString();
                if (!string.IsNullOrEmpty(user)) CustomProxyUsername = user;
                if (!string.IsNullOrEmpty(pass)) CustomProxyPassword = pass;
            }
            finally { _suppressSave = false; }
            PersistCustomProxyToSettings();
            ScheduleSave();
            return;
        }

        _settings.Settings.FirefoxCustomProxyHost = trimmed;
        ScheduleSave();
    }

    [ObservableProperty]
    private string _customProxyPort = "1080";

    partial void OnCustomProxyPortChanged(string value)
    {
        if (_suppressSave) return;
        _settings.Settings.FirefoxCustomProxyPort = ParsePortOrDefault(value);
        ScheduleSave();
    }

    [ObservableProperty]
    private string _customProxyUsername = "";

    partial void OnCustomProxyUsernameChanged(string value)
    {
        if (_suppressSave) return;
        _settings.Settings.FirefoxCustomProxyUsername = value ?? "";
        ScheduleSave();
    }

    [ObservableProperty]
    private string _customProxyPassword = "";

    partial void OnCustomProxyPasswordChanged(string value)
    {
        if (_suppressSave) return;
        _settings.Settings.FirefoxCustomProxyPassword = value ?? "";
        ScheduleSave();
    }

    public bool HasCustomProxy =>
        IsDirectFoxyOnly && CustomProxyEnabled && !string.IsNullOrWhiteSpace(CustomProxyHost);

    public void RefreshMethodAvailability()
    {
        OnPropertyChanged(nameof(IsDirectFoxyOnly));
        OnPropertyChanged(nameof(IsChainedFoxyMode));
        OnPropertyChanged(nameof(HasCustomProxy));
    }

    public void LoadFromSettings()
    {
        var s = _settings.Settings;
        _suppressSave = true;
        try
        {
            CustomProxyEnabled = s.FirefoxCustomProxyEnabled;
            SelectedCustomProxyScheme = NormalizeScheme(s.FirefoxCustomProxyType);
            CustomProxyHost = s.FirefoxCustomProxyHost ?? "";
            CustomProxyPort = (s.FirefoxCustomProxyPort is > 0 and <= 65535 ? s.FirefoxCustomProxyPort : 1080).ToString();
            CustomProxyUsername = s.FirefoxCustomProxyUsername ?? "";
            CustomProxyPassword = s.FirefoxCustomProxyPassword ?? "";
        }
        finally
        {
            _suppressSave = false;
        }
        RefreshAccountsFromSettings();
        RefreshMethodAvailability();
    }

    public void FlushPendingSave()
    {
        var pending = Interlocked.Exchange(ref _saveDebounceCts, null);
        if (pending is null) return;

        try
        {
            pending.Cancel();
            pending.Dispose();
        }
        catch { }
        _settings.Save();
    }

    private void ScheduleSave()
    {
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _saveDebounceCts, cts);
        try
        {
            prev?.Cancel();
            prev?.Dispose();
        }
        catch { }

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                Interlocked.CompareExchange(ref _saveDebounceCts, null, cts);
                _settings.Save();
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void PersistCustomProxyToSettings()
    {
        var s = _settings.Settings;
        s.FirefoxCustomProxyEnabled = CustomProxyEnabled;
        s.FirefoxCustomProxyType = NormalizeScheme(SelectedCustomProxyScheme);
        s.FirefoxCustomProxyHost = CustomProxyHost?.Trim() ?? "";
        s.FirefoxCustomProxyPort = ParsePortOrDefault(CustomProxyPort);
        s.FirefoxCustomProxyUsername = CustomProxyUsername ?? "";
        s.FirefoxCustomProxyPassword = CustomProxyPassword ?? "";
    }

    private static string NormalizeScheme(string? scheme) =>
        string.Equals(scheme?.Trim(), "http", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme?.Trim(), "https", StringComparison.OrdinalIgnoreCase)
            ? "http"
            : "socks5";

    private static int ParsePortOrDefault(string? raw) =>
        int.TryParse(raw?.Trim(), out var port) && port is > 0 and <= 65535 ? port : 1080;

    private static bool TryParseProxyInput(
        string raw, out string scheme, out string host, out int port, out string user, out string pass)
    {
        scheme = "";
        host = raw;
        port = 0;
        user = "";
        pass = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var hasScheme = raw.Contains("://", StringComparison.Ordinal);
        var hasCreds = raw.Contains('@', StringComparison.Ordinal);
        var lastColon = raw.LastIndexOf(':');
        var hasPort = lastColon > 0 && int.TryParse(raw[(lastColon + 1)..], out var p) && p is > 0 and <= 65535;
        if (!hasScheme && !hasCreds && !hasPort) return false;
        if (!hasScheme && raw.Count(c => c == ':') > 1 && !raw.StartsWith('[')) return false;

        var normalized = hasScheme ? raw : $"socks5://{raw}";
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        scheme = hasScheme ? uri.Scheme : "";
        host = uri.Host;
        port = uri.Port > 0 ? uri.Port : 0;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var idx = uri.UserInfo.IndexOf(':');
            if (idx >= 0)
            {
                user = Uri.UnescapeDataString(uri.UserInfo[..idx]);
                pass = Uri.UnescapeDataString(uri.UserInfo[(idx + 1)..]);
            }
            else
            {
                user = Uri.UnescapeDataString(uri.UserInfo);
            }
        }
        return true;
    }

    [RelayCommand]
    private void ClearCustomProxy()
    {
        _suppressSave = true;
        try
        {
            CustomProxyEnabled = false;
            SelectedCustomProxyScheme = "socks5";
            CustomProxyHost = "";
            CustomProxyPort = "1080";
            CustomProxyUsername = "";
            CustomProxyPassword = "";
        }
        finally { _suppressSave = false; }

        PersistCustomProxyToSettings();
        OnPropertyChanged(nameof(HasCustomProxy));
        var pending = Interlocked.Exchange(ref _saveDebounceCts, null);
        try
        {
            pending?.Cancel();
            pending?.Dispose();
        }
        catch { }
        _settings.Save();
    }

    public ObservableCollection<FirefoxAccountInfo> Accounts
    {
        get => _accounts;
        private set
        {
            if (ReferenceEquals(_accounts, value)) return;
            _accounts.CollectionChanged -= OnAccountsChanged;
            _accounts = value;
            _accounts.CollectionChanged += OnAccountsChanged;
            OnPropertyChanged();
            AccountCount = _accounts.Count;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccounts))]
    private int _accountCount;

    public bool HasAccounts => AccountCount > 0;

    private void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e) => AccountCount = Accounts.Count;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(RefreshAccountsFromSettings);
            return;
        }

        RefreshAccountsFromSettings();
    }

    private void RefreshAccountsFromSettings()
    {
        var current = _settings.Settings.FirefoxAccounts;
        if (!ReferenceEquals(_accounts, current))
        {
            Accounts = current;
        }
        else
        {
            AccountCount = _accounts.Count;
        }

        SyncActiveFlags();
    }

    public async Task RefreshIfStaleAsync()
    {
        if (!HasAccounts) return;
        if (DateTime.UtcNow - _lastRefreshUtc < AutoRefreshInterval) return;
        await RefreshAllQuotasAsync();
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        var dialog = new AddFoxyAccountDialog(_accountService);
        if (Application.Current?.MainWindow is { } owner) dialog.Owner = owner;
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        var account = _accountService.StoreAccount(dialog.Result);
        SyncActiveFlags();
        await RefreshQuotaAsync(account);
    }

    [RelayCommand]
    private void RemoveAccount(FirefoxAccountInfo? account)
    {
        if (account is null) return;

        var question = string.Format(Loc.Of("Remove {0} from Se7en? You can sign in again at any time."),
            account.Email);
        var owner = Application.Current?.MainWindow;
        var answer = owner is not null
            ? MessageBox.Show(owner, question, Loc.Of("Remove Firefox account"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(question, Loc.Of("Remove Firefox account"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _accountService.Remove(account);
        SyncActiveFlags();
    }

    [RelayCommand]
    private void SetActiveAccount(FirefoxAccountInfo? account)
    {
        if (account is null) return;
        _accountService.SetActive(account);
        SyncActiveFlags();
    }

    [RelayCommand]
    private async Task RefreshQuotaAsync(FirefoxAccountInfo? account)
    {
        if (account is null || account.IsRefreshing) return;
        var ok = false;
        try
        {
            await _accountService.RefreshQuotaAsync(account);
            ok = true;
        }
        catch
        {
        }

        SyncActiveFlags();
        if (ok) _lastRefreshUtc = DateTime.UtcNow;
    }

    [RelayCommand]
    private async Task RefreshAllQuotasAsync()
    {
        if (_isRefreshingAll || Accounts.Count == 0) return;
        _isRefreshingAll = true;
        try
        {
            foreach (var account in Accounts.ToArray())
            {
                try
                {
                    await _accountService.RefreshQuotaAsync(account);
                }
                catch
                {
                }
            }
        }
        finally
        {
            _isRefreshingAll = false;
            _lastRefreshUtc = DateTime.UtcNow;
            SyncActiveFlags();
        }
    }

    private void SyncActiveFlags()
    {
        var activeId = _settings.Settings.FirefoxActiveAccountId;
        foreach (var account in Accounts)
        {
            account.IsActive = string.Equals(account.Id, activeId, StringComparison.Ordinal);
        }
    }
}
