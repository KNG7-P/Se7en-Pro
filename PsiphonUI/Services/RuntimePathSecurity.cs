using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PsiphonUI.Services;

internal static class RuntimePathSecurity
{
    private const string AppDirectoryName = "PsiphonUI";

    public static string GetRuntimeDirectory(string componentName, bool preferMachineSecure)
    {
        var root = preferMachineSecure && AdminElevation.IsAdministrator()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var appRoot = Path.Combine(root, AppDirectoryName);
        var runtimeRoot = Path.Combine(appRoot, "runtime");
        var path = Path.Combine(runtimeRoot, componentName);
        Directory.CreateDirectory(path);

        if (preferMachineSecure && AdminElevation.IsAdministrator())
        {
            TryRestrictToElevatedWriters(appRoot);
            TryRestrictToElevatedWriters(runtimeRoot);
            TryRestrictToElevatedWriters(path);
        }

        return path;
    }

    public static string GetRuntimeRoot(bool machineSecure)
    {
        var root = machineSecure
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return Path.Combine(root, AppDirectoryName, "runtime");
    }

    public static string ResolveSystem32Tool(string fileName)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = Path.Combine(systemDirectory, fileName);
        return File.Exists(path) ? path : fileName;
    }

    private static void TryRestrictToElevatedWriters(string path)
    {
        try
        {
            var security = new DirectorySecurity();
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            const PropagationFlags propagation = PropagationFlags.None;

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                inheritance,
                propagation,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inheritance,
                propagation,
                AccessControlType.Allow));

            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch
        {
            // Best effort: process launch still works on systems where ACL edits are blocked.
        }
    }
}
