using Se7enPro.Models;

namespace Se7enPro.Services;

internal static class LanPolicy
{
    public static bool HasCredentials(UserSettings s) =>
        s.LanAuthEnabled
        && !string.IsNullOrWhiteSpace(s.LanProxyUsername)
        && !string.IsNullOrEmpty(s.LanProxyPassword);

    public static bool CanExpose(UserSettings s) => s.AllowLanConnections && HasCredentials(s);
}
