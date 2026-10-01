using System;
using System.Security.Cryptography;
using System.Text;

namespace Se7enPro.Services;

internal static class ProtectedSecret
{
    private const string Prefix = "dpapi:v1:";

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext ?? "";

        try
        {
            var clear = Encoding.UTF8.GetBytes(plaintext);
            try
            {
                var blob = ProtectedData.Protect(
                    clear, optionalEntropy: null, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(blob);
            }
            finally
            {
                Array.Clear(clear, 0, clear.Length);
            }
        }
        catch (Exception)
        {
            
            
            
            
            System.Diagnostics.Debug.WriteLine(
                "[secrets] DPAPI unavailable; falling back to plaintext storage");
            return plaintext;
        }
    }

        public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored ?? "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;

        try
        {
            var blob = Convert.FromBase64String(stored[Prefix.Length..]);
            var clear = ProtectedData.Unprotect(
                blob, optionalEntropy: null, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(clear);
            }
            finally
            {
                Array.Clear(clear, 0, clear.Length);
            }
        }
        catch (Exception ex)
        {
            
            
            
            System.Diagnostics.Debug.WriteLine($"[secrets] could not unprotect a stored value: {ex.Message}");
            return "";
        }
    }

    public static bool LooksProtected(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);
}
