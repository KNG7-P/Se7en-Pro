namespace Se7enPro.Services;

internal static class EngineTrust
{
    public static bool RunFromBundle =>
        AdminElevation.IsAdministrator() && AdminElevation.IsTrustedInstallPath();
}
