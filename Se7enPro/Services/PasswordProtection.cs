using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Se7enPro.Services;

internal static class PasswordProtection
{
    private const string Prefix = "enc:v1:";

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        if (plain.StartsWith(Prefix, StringComparison.Ordinal)) return plain;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var enc = ProtectedDataFallback.Protect(bytes);
            return Prefix + Convert.ToBase64String(enc);
        }
        catch
        {

            try { return Prefix + Convert.ToBase64String(XorFallback(Encoding.UTF8.GetBytes(plain))); }
            catch { return ""; }
        }
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        try
        {
            var b64 = value.Substring(Prefix.Length);
            var enc = Convert.FromBase64String(b64);

            byte[] dec;
            if (OperatingSystem.IsWindows())
            {
                try { dec = DpapiUnprotect(enc); }
                catch
                {

                    var xorDec = XorFallback(enc);
                    var s = Encoding.UTF8.GetString(xorDec);

                    if (s.IndexOf('\uFFFD') >= 0) return "";
                    return s;
                }
                return Encoding.UTF8.GetString(dec);
            }
            else
            {
                dec = XorFallback(enc);
                return Encoding.UTF8.GetString(dec);
            }
        }
        catch { return ""; }
    }

    private static byte[] DpapiUnprotect(byte[] enc) => ProtectedDataFallback.DpapiUnprotect(enc);
    private static byte[] XorFallback(byte[] data) => ProtectedDataFallback.XorPublic(data);

    public static bool IsEncrypted(string v) => !string.IsNullOrEmpty(v) && v.StartsWith(Prefix, StringComparison.Ordinal);
}

internal sealed class EncryptedStringConverter : System.Text.Json.Serialization.JsonConverter<string>
{
    public override string? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var raw = reader.GetString() ?? "";

        return PasswordProtection.Unprotect(raw);
    }
    public override void Write(System.Text.Json.Utf8JsonWriter writer, string value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStringValue(PasswordProtection.Protect(value ?? ""));
    }
}

internal static class ProtectedDataFallback
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Se7enPro.v1.");

    public static byte[] Protect(byte[] data)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return Dpapi.Protect(data, Entropy); } catch { }
        }
        return Xor(data, Entropy);
    }

    public static byte[] Unprotect(byte[] data)
    {
        if (OperatingSystem.IsWindows())
        {
            return Dpapi.Unprotect(data, Entropy);
        }
        return Xor(data, Entropy);
    }

    internal static byte[] DpapiUnprotect(byte[] data) => Dpapi.Unprotect(data, Entropy);
    internal static byte[] XorPublic(byte[] data) => Xor(data, Entropy);

    private static byte[] Xor(byte[] data, byte[] key)
    {
        var outp = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) outp[i] = (byte)(data[i] ^ key[i % key.Length]);
        return outp;
    }

    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)] struct DATA_BLOB { public int cbData; public IntPtr pbData; }
        [StructLayout(LayoutKind.Sequential)] struct CRYPTPROTECT_PROMPTSTRUCT { public int cbSize; public int dwPromptFlags; public IntPtr hwndApp; public IntPtr szPrompt; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, ref CRYPTPROTECT_PROMPTSTRUCT pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, ref CRYPTPROTECT_PROMPTSTRUCT pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr hMem);

        const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public static byte[] Protect(byte[] data, byte[] entropy)
        {
            var plainBlob = ToBlob(data);
            var entropyBlob = ToBlob(entropy);
            var prompt = new CRYPTPROTECT_PROMPTSTRUCT { cbSize = Marshal.SizeOf<CRYPTPROTECT_PROMPTSTRUCT>(), dwPromptFlags = 0 };
            var outBlob = new DATA_BLOB();
            try
            {
                if (!CryptProtectData(ref plainBlob, "", ref entropyBlob, IntPtr.Zero, ref prompt, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    throw new CryptographicException(Marshal.GetLastWin32Error().ToString());
                var outBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return outBytes;
            }
            finally { FreeBlob(ref plainBlob); FreeBlob(ref entropyBlob); if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData); }
        }

        public static byte[] Unprotect(byte[] data, byte[] entropy)
        {
            var encBlob = ToBlob(data);
            var entropyBlob = ToBlob(entropy);
            var prompt = new CRYPTPROTECT_PROMPTSTRUCT { cbSize = Marshal.SizeOf<CRYPTPROTECT_PROMPTSTRUCT>(), dwPromptFlags = 0 };
            var outBlob = new DATA_BLOB();
            try
            {
                if (!CryptUnprotectData(ref encBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, ref prompt, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    throw new CryptographicException(Marshal.GetLastWin32Error().ToString());
                var outBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return outBytes;
            }
            finally { FreeBlob(ref encBlob); FreeBlob(ref entropyBlob); if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData); }
        }

        static DATA_BLOB ToBlob(byte[] data)
        {
            if (data == null || data.Length == 0) return new DATA_BLOB { cbData = 0, pbData = IntPtr.Zero };
            var ptr = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, ptr, data.Length);
            return new DATA_BLOB { cbData = data.Length, pbData = ptr };
        }
        static void FreeBlob(ref DATA_BLOB b) { if (b.pbData != IntPtr.Zero) { Marshal.FreeHGlobal(b.pbData); b.pbData = IntPtr.Zero; } }
    }
}
