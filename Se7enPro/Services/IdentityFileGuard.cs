using System;
using System.Collections.Generic;
using System.IO;

namespace Se7enPro.Services;

/// <summary>
/// Provides filesystem access control helpers for identity files.
/// </summary>
internal static class IdentityFileGuard
{
    /// <summary>
    /// Restricts directory access permissions to SYSTEM, Administrators, and the current user.
    /// </summary>
    public static void RestrictToCurrentUser(string dir)
    {
        try
        {
            var sd = new System.Security.AccessControl.DirectorySecurity();
            sd.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var sids = new List<System.Security.Principal.SecurityIdentifier>
            {
                new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
            };
            try
            {
                using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (current.User is not null)
                {
                    sids.Add(new System.Security.Principal.SecurityIdentifier(current.User.Value!));
                }
            }
            catch { }

            foreach (var sid in sids)
            {
                sd.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    sid,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                    System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            new DirectoryInfo(dir).SetAccessControl(sd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[identity] could not restrict the identity directory ACL: {ex.Message}");
        }
    }
}