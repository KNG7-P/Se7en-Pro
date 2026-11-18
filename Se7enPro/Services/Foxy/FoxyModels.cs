using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Se7enPro.Services.Foxy;

public sealed class VpnCountry
{
    public VpnCountry(string name, string code, List<VpnCity> cities)
    {
        Name = name;
        Code = code;
        Cities = cities;
    }

    public string Name { get; }
    public string Code { get; }
    public List<VpnCity> Cities { get; }
}

public sealed class VpnCity
{
    public VpnCity(string name, string code, List<VpnServerNode> servers)
    {
        Name = name;
        Code = code;
        Servers = servers;
    }

    public string Name { get; }
    public string Code { get; }
    public List<VpnServerNode> Servers { get; }
}

public sealed class VpnServerNode
{
    public VpnServerNode(string hostname, int port, bool quarantined = false)
    {
        Hostname = hostname;
        Port = port;
        Quarantined = quarantined;
    }

    public string Hostname { get; }
    public int Port { get; }
    public bool Quarantined { get; }
}

public sealed record ProxyCandidate(string Host, int Port, string CountryCode, string CountryName, string CityCode)
{
    [JsonIgnore]
    public string Authority => $"{Host}:{Port}";
}

public sealed record ProxyPassResult(
    string Token,
    long ExpiresAtEpochSeconds,
    long? QuotaLimit,
    long? QuotaRemaining,
    long? QuotaReset);

public sealed record UpstreamProxySettings(
    bool Enabled,
    string Type,
    string Host,
    int Port,
    string? Username = null,
    string? Password = null);

public sealed record FxaLoginResult(
    bool Success,
    bool Requires2Fa,
    string? SessionToken,
    Se7enPro.Models.FirefoxAccountInfo? Account,
    string? ErrorMessage);

public sealed class FxaException : Exception
{
    public FxaException(string message, int? errno, int statusCode) : base(message)
    {
        Errno = errno;
        StatusCode = statusCode;
    }

    public int? Errno { get; }
    public int StatusCode { get; }
}

public sealed class FirefoxQuotaExceededException : Exception
{
    public FirefoxQuotaExceededException(string message) : base(message) { }
}

public sealed class FirefoxAuthTokenInvalidException : Exception
{
    public FirefoxAuthTokenInvalidException(string message) : base(message) { }
}

public sealed class FirefoxTargetRejectedException : Exception
{
    public FirefoxTargetRejectedException(string message) : base(message) { }
}
