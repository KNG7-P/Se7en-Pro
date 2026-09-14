using System.Net;

namespace Se7enPro.Services;

internal static class LanExposurePolicy
{

    internal static IPAddress ResolveBindAddress(
        bool allowLan,
        string? username,
        string? password,
        bool engineEnforcesCredentials,
        out string reason)
    {
        reason = "";
        if (!allowLan) return IPAddress.Loopback;

        var hasAuth = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password);
        if (hasAuth)
        {
            reason = "LAN sharing is active with authentication enabled.";
        }
        else
        {
            reason = "LAN sharing is active on all network interfaces.";
        }

        return IPAddress.Any;
    }
}
