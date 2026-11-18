using System;
using System.Collections.Generic;
using System.IO;

namespace Se7enPro.Services;

internal static class IdentityFileGuard
{
    public static bool RestrictToCurrentUser(string dir, out string? error)
    {
        error = null;
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
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}