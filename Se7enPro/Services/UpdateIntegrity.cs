using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Se7enPro.Services;

internal static class UpdateIntegrity
{
    private static readonly Regex Sha256Line = new(
        @"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>\S.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly string[] AllowedDownloadHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "raw.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

        public static void EnsureTrustedUrl(string? url, string what)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException($"{what}: no download URL was provided.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{what}: \"{url}\" is not a valid absolute URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{what}: refusing to download over {uri.Scheme} — only HTTPS is allowed.");
        }

        if (!AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{what}: refusing to download from untrusted host \"{uri.Host}\".");
        }
    }

        public static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

        public static string VerifyAgainstManifest(string manifestText, string fileName, string filePath)
    {
        if (string.IsNullOrWhiteSpace(manifestText))
        {
            throw new InvalidOperationException(
                "The release did not publish a SHA256SUMS manifest, so the download cannot be verified. " +
                "Refusing to install an unverified binary.");
        }

        string? expected = null;
        foreach (Match m in Sha256Line.Matches(manifestText))
        {
            var name = m.Groups["name"].Value.Trim();
            
            
            var leaf = name.Replace('\\', '/').Split('/').LastOrDefault() ?? name;
            if (leaf.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals(Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase))
            {
                expected = m.Groups["hash"].Value.ToLowerInvariant();
                break;
            }
        }

        if (expected is null)
        {
            throw new InvalidOperationException(
                $"SHA256SUMS has no entry for \"{fileName}\". Refusing to install an unverified binary.");
        }

        var actual = ComputeSha256(filePath);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Integrity check failed for {fileName}.\n" +
                $"  expected SHA-256 {expected}\n" +
                $"  actual   SHA-256 {actual}\n" +
                "The download was tampered with or corrupted. Nothing was installed.");
        }

        return expected;
    }

        public static void HardenStagingDirectory(string dir)
    {
        Directory.CreateDirectory(dir);

        
        
        
        try
        {
            var sd = new DirectorySecurity();
            sd.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var sids = new System.Collections.Generic.List<SecurityIdentifier>
            {
                new(WellKnownSidType.LocalSystemSid, null),
                new(WellKnownSidType.BuiltinAdministratorsSid, null),
            };

            try
            {
                if (WindowsIdentity.GetCurrent().User is { } currentUser)
                {
                    sids.Add(currentUser);
                }
            }
            catch { }

            foreach (var sid in sids)
            {
                sd.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            new DirectoryInfo(dir).SetAccessControl(sd);
        }
        catch (Exception)
        {
            
            
            
            Debug.WriteLine("[update] could not harden the staging directory ACL");
        }
    }
}
