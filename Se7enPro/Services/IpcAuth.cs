using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Se7enPro.Services;

internal static class IpcAuth
{
    internal const string TokenFileName = "ipc.token";
    internal const string PortFileName = "ipc.port";

    private const int TokenBytes = 32;

    internal static string CreateSessionToken(string path)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(new byte[0]);
            }
            TryRestrictToCurrentUser(path);
            File.WriteAllText(path, token);

            TryRestrictToCurrentUser(path);
        }
        catch
        {
            File.WriteAllText(path, token);
            TryRestrictToCurrentUser(path);
        }
        return token;
    }

    internal static void RestrictFileToCurrentUser(string path) => TryRestrictToCurrentUser(path);

    private static void TryRestrictToCurrentUser(string path)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user is null) return;

            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            foreach (var wellKnown in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                try
                {
                    var sid = new SecurityIdentifier(wellKnown, null);
                    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                }
                catch { }
            }

            try
            {
                var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
                security.AddAccessRule(new FileSystemAccessRule(authUsers, FileSystemRights.Read, AccessControlType.Allow));
            }
            catch { }
            new FileInfo(path).SetAccessControl(security);

            try
            {
                var acl = new FileInfo(path).GetAccessControl();
                var rules = acl.GetAccessRules(true, false, typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule r in rules)
                {
                    if (r.IdentityReference.Value == "S-1-1-0" && r.AccessControlType == AccessControlType.Allow)
                        System.Diagnostics.Debug.WriteLine($"[IpcAuth] WARNING: file still allows Everyone: {path}");
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IpcAuth] TryRestrictToCurrentUser failed for {path}: {ex.Message}");
        }
    }

    internal static PipeSecurity? TryBuildPipeSecurity()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user is null) return null;

            var security = new PipeSecurity();
            security.SetOwner(user);
            security.AddAccessRule(new PipeAccessRule(
                user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            foreach (var wellKnown in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                try
                {
                    var sid = new SecurityIdentifier(wellKnown, null);
                    security.AddAccessRule(new PipeAccessRule(
                        sid, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                        AccessControlType.Allow));
                }
                catch { }
            }

            return security;
        }
        catch
        {
            return null;
        }
    }

    internal static bool TokenEquals(string? expected, string? presented)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented)) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
