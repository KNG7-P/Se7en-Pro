using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Se7enPro.Services;

namespace Se7enPro.Models;

public sealed class FirefoxAccountInfo : INotifyPropertyChanged
{
    private const long DefaultQuotaBytes = 53687091200L;
    private static readonly object InstancesLock = new();
    private static readonly List<WeakReference<FirefoxAccountInfo>> Instances = new();

    static FirefoxAccountInfo()
    {
        Loc.Changed += DispatchLocaleChanged;
    }

    private static void DispatchLocaleChanged()
    {
        List<FirefoxAccountInfo> alive;
        lock (InstancesLock)
        {
            alive = new List<FirefoxAccountInfo>(Instances.Count);
            for (var i = Instances.Count - 1; i >= 0; i--)
            {
                if (Instances[i].TryGetTarget(out var target))
                {
                    alive.Add(target);
                }
                else
                {
                    Instances.RemoveAt(i);
                }
            }
        }

        foreach (var item in alive)
        {
            item.RaiseQuotaDependents();
        }
    }

    public FirefoxAccountInfo()
    {
        lock (InstancesLock)
        {
            Instances.Add(new WeakReference<FirefoxAccountInfo>(this));
        }
    }

    private string _sessionToken = "";
    private string _accessToken = "";
    private string _refreshToken = "";
    private long _tokenExpiryEpochSeconds;
    private readonly object _tokenSync = new();

    private string _proxyPassToken = "";
    private long _proxyPassExpiryEpochSeconds;

    private long _quotaLimitBytes = DefaultQuotaBytes;
    private long _quotaRemainingBytes = DefaultQuotaBytes;
    private long _quotaResetEpochSeconds;

    private bool _isActive;
    private bool _isRefreshing;
    private DateTime? _lastUsedAtUtc;
    private DateTime? _lastQuotaCheckUtc;
    private string _quotaErrorMessage = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("sessionToken")]
    public string SessionToken
    {
        get => _sessionToken;
        set { _sessionToken = value; Raise(); }
    }

    [JsonPropertyName("accessToken")]
    public string AccessToken
    {
        get => _accessToken;
        set { _accessToken = value; Raise(); }
    }

    [JsonPropertyName("refreshToken")]
    public string RefreshToken
    {
        get => _refreshToken;
        set { _refreshToken = value; Raise(); }
    }

    [JsonPropertyName("tokenExpiryEpochSeconds")]
    public long TokenExpiryEpochSeconds
    {
        get => _tokenExpiryEpochSeconds;
        set { _tokenExpiryEpochSeconds = value; Raise(); }
    }

    [JsonIgnore]
    public bool HasUsableCredentials =>
        !string.IsNullOrWhiteSpace(_accessToken) && !string.IsNullOrWhiteSpace(_refreshToken);

    [JsonPropertyName("proxyPassToken")]
    public string ProxyPassToken
    {
        get => _proxyPassToken;
        set { _proxyPassToken = value; Raise(); }
    }

    [JsonPropertyName("proxyPassExpiryEpochSeconds")]
    public long ProxyPassExpiryEpochSeconds
    {
        get => _proxyPassExpiryEpochSeconds;
        set { _proxyPassExpiryEpochSeconds = value; Raise(); }
    }

    [JsonPropertyName("quotaLimitBytes")]
    public long QuotaLimitBytes
    {
        get => _quotaLimitBytes;
        set { _quotaLimitBytes = value; Raise(); RaiseQuotaDependents(); }
    }

    [JsonPropertyName("quotaRemainingBytes")]
    public long QuotaRemainingBytes
    {
        get => _quotaRemainingBytes;
        set { _quotaRemainingBytes = value; Raise(); RaiseQuotaDependents(); }
    }

    [JsonPropertyName("quotaResetEpochSeconds")]
    public long QuotaResetEpochSeconds
    {
        get => _quotaResetEpochSeconds;
        set { _quotaResetEpochSeconds = value; Raise(); RaiseQuotaDependents(); }
    }

    [JsonPropertyName("isActive")]
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; Raise(); }
    }

    [JsonIgnore]
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { _isRefreshing = value; Raise(); }
    }

    [JsonPropertyName("addedAtUtc")]
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastUsedAtUtc")]
    public DateTime? LastUsedAtUtc
    {
        get => _lastUsedAtUtc;
        set { _lastUsedAtUtc = value; Raise(); }
    }

    [JsonPropertyName("lastQuotaCheckUtc")]
    public DateTime? LastQuotaCheckUtc
    {
        get => _lastQuotaCheckUtc;
        set { _lastQuotaCheckUtc = value; Raise(); RaiseQuotaDependents(); }
    }

    [JsonPropertyName("quotaErrorMessage")]
    public string QuotaErrorMessage
    {
        get => _quotaErrorMessage;
        set { _quotaErrorMessage = value; Raise(); RaiseQuotaDependents(); }
    }

    [JsonIgnore]
    public bool HasQuotaError => !string.IsNullOrWhiteSpace(QuotaErrorMessage);

    [JsonIgnore]
    public string LastCheckedText =>
        LastQuotaCheckUtc.HasValue
            ? string.Format(Loc.Of("checked {0}"), LastQuotaCheckUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
            : Loc.Of("never checked");

    [JsonIgnore]
    public double QuotaUsedPercentage
    {
        get
        {
            if (QuotaLimitBytes <= 0) return 0;
            return Math.Clamp((double)(QuotaLimitBytes - QuotaRemainingBytes) / QuotaLimitBytes * 100.0, 0.0, 100.0);
        }
    }

    [JsonIgnore]
    public double QuotaRemainingPercentage
    {
        get
        {
            if (QuotaLimitBytes <= 0) return 100;
            return Math.Clamp((double)QuotaRemainingBytes / QuotaLimitBytes * 100.0, 0.0, 100.0);
        }
    }

    [JsonIgnore]
    public string QuotaRemainingDisplay => string.Format(Loc.Of("{0:F2} GB left"), ToGb(QuotaRemainingBytes));

    [JsonIgnore]
    public string QuotaUsedDisplay => string.Format(Loc.Of("{0:F2} GB used"), ToGb(QuotaLimitBytes - QuotaRemainingBytes));

    [JsonIgnore]
    public string QuotaLimitDisplay => $"{ToGb(QuotaLimitBytes):F2} GB";

    [JsonIgnore]
    public string QuotaDisplay => $"{ToGb(QuotaRemainingBytes):F2} GB / {ToGb(QuotaLimitBytes):F2} GB";

    [JsonIgnore]
    public string QuotaSummaryText =>
        string.Format(Loc.Of("{0:F2} GB of {1:F2} GB remaining"), ToGb(QuotaRemainingBytes), ToGb(QuotaLimitBytes));

    [JsonIgnore]
    public string QuotaResetDisplay
    {
        get
        {
            if (QuotaResetEpochSeconds <= 0) return "";
            var when = DateTimeOffset.FromUnixTimeSeconds(QuotaResetEpochSeconds).ToLocalTime();
            return string.Format(Loc.Of("Resets {0}"), when.ToString("yyyy-MM-dd"));
        }
    }

    [JsonIgnore]
    public string QuotaProgressBarColor =>
        QuotaRemainingBytes <= 0 ? "#EF4444"
        : ToGb(QuotaRemainingBytes) < 5.0 ? "#F59E0B"
        : "#10B981";

    private static double ToGb(long bytes) => Math.Max(0, bytes) / 1073741824.0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void RaiseQuotaDependents()
    {
        Raise(nameof(HasQuotaError));
        Raise(nameof(LastCheckedText));
        Raise(nameof(QuotaUsedPercentage));
        Raise(nameof(QuotaRemainingPercentage));
        Raise(nameof(QuotaRemainingDisplay));
        Raise(nameof(QuotaUsedDisplay));
        Raise(nameof(QuotaLimitDisplay));
        Raise(nameof(QuotaDisplay));
        Raise(nameof(QuotaSummaryText));
        Raise(nameof(QuotaResetDisplay));
        Raise(nameof(QuotaProgressBarColor));
    }

    public void ApplyRefreshedTokens(string accessToken, string refreshToken, long expiryEpochSeconds, Action? persistUnderLock = null)
    {
        lock (_tokenSync)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _tokenExpiryEpochSeconds = expiryEpochSeconds;
            persistUnderLock?.Invoke();
        }
        Raise(nameof(AccessToken));
        Raise(nameof(RefreshToken));
        Raise(nameof(TokenExpiryEpochSeconds));
        Raise(nameof(HasUsableCredentials));
    }
}
