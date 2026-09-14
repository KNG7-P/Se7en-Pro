using System;
using System.Runtime.InteropServices;

namespace Se7enPro.Services;

internal static class BinaryTrust
{
    private static readonly Guid WintrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSaferFlag = 0x100;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    internal sealed record TrustResult(bool Trusted, string Detail)
    {
        internal static TrustResult Ok(string detail) => new(true, detail);
        internal static TrustResult Fail(string detail) => new(false, detail);
    }

    internal static TrustResult VerifyAuthenticode(
        string filePath,
        string requiredSubjectFragment,
        bool allowRevocationCheckFailure = true)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
        {
            return TrustResult.Fail($"file not found: {filePath}");
        }

        var signatureCheck = VerifyEmbeddedSignature(filePath, allowRevocationCheckFailure);
        if (!signatureCheck.Trusted) return signatureCheck;

        if (string.IsNullOrWhiteSpace(requiredSubjectFragment))
        {
            return signatureCheck;
        }

        string subject;
        try
        {

            var cert = System.Security.Cryptography.X509Certificates.X509Certificate
                .CreateFromSignedFile(filePath);
            subject = cert.Subject ?? "";
        }
        catch (Exception ex)
        {
            return TrustResult.Fail($"signature is valid but the signer could not be read: {ex.Message}");
        }

        if (subject.IndexOf(requiredSubjectFragment, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return TrustResult.Fail(
                $"signed by an unexpected publisher (subject '{subject}' does not contain "
                + $"'{requiredSubjectFragment}')");
        }

        return TrustResult.Ok($"Authenticode signature valid; signer subject '{subject}'");
    }

    private static TrustResult VerifyEmbeddedSignature(string filePath, bool allowRevocationCheckFailure)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeWholeChain,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = null,
                dwProvFlags = WtdSaferFlag | WtdCacheOnlyUrlRetrieval,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero,
            };

            var action = WintrustActionGenericVerifyV2;
            int result;
            try
            {
                result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            finally
            {
                data.dwStateAction = WtdStateActionClose;
                try { WinVerifyTrust(IntPtr.Zero, ref action, ref data); } catch { }
            }

            return Interpret(result, allowRevocationCheckFailure);
        }
        catch (Exception ex)
        {
            return TrustResult.Fail($"trust verification could not run: {ex.Message}");
        }
        finally
        {
            try { Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr); } catch { }
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private static TrustResult Interpret(int status, bool allowRevocationCheckFailure)
    {
        const int Ok = 0;
        const int TrustEBadDigest = unchecked((int)0x80096010);
        const int TrustENoSignerCert = unchecked((int)0x80096002);
        const int TrustENosignature = unchecked((int)0x800B0100);
        const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
        const int TrustESubjectNotTrusted = unchecked((int)0x800B0004);
        const int CertEUntrustedroot = unchecked((int)0x800B0109);
        const int CertEExpired = unchecked((int)0x800B0101);
        const int CertEChaining = unchecked((int)0x800B010A);
        const int CryptERevocationOffline = unchecked((int)0x80092013);
        const int CryptENoRevocationCheck = unchecked((int)0x80092012);

        return status switch
        {
            Ok => TrustResult.Ok("Authenticode signature valid"),
            TrustENosignature => TrustResult.Fail("the file is not signed at all"),
            TrustEBadDigest => TrustResult.Fail("the file has been modified since it was signed"),
            TrustEExplicitDistrust => TrustResult.Fail("the signing certificate is explicitly distrusted"),
            TrustESubjectNotTrusted => TrustResult.Fail("the signature is not trusted on this machine"),
            TrustENoSignerCert => TrustResult.Fail("the signer certificate is missing"),
            CertEUntrustedroot => TrustResult.Fail("the signing chain does not reach a trusted root"),
            CertEExpired => TrustResult.Fail("the signing certificate has expired"),
            CertEChaining => TrustResult.Fail("the signing certificate chain is broken"),
            CryptERevocationOffline or CryptENoRevocationCheck when allowRevocationCheckFailure =>
                TrustResult.Ok("Authenticode signature valid (revocation status unavailable offline)"),
            CryptERevocationOffline or CryptENoRevocationCheck =>
                TrustResult.Fail("revocation status could not be checked"),
            _ => TrustResult.Fail($"trust verification failed (0x{status:X8})"),
        };
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
