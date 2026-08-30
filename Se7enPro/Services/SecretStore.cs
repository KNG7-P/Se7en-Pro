using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Se7enPro.Services;

internal static class SecretStore
{
    private static readonly byte[] Alpha = new byte[]
    {
        0x16, 0xF7, 0xE3, 0x02, 0xBD, 0x9F, 0x80, 0xFB, 0x15, 0x35, 0x4E, 0x05, 0x7D, 0x29, 0x33, 0x26,
        0xB0, 0x2C, 0x4A, 0xCF, 0xE6, 0xB9, 0x2E, 0xE7, 0x62, 0x20, 0x08, 0xB1, 0xC2, 0x39, 0x74, 0x10
    };

    private static readonly byte[] Beta = new byte[]
    {
        0xB3, 0xA3, 0x1F, 0xED, 0x26, 0x5F, 0x2F, 0x64, 0xC5, 0x09, 0x56, 0x7A, 0xB2, 0xFA, 0x45, 0x6D,
        0x8F, 0x98, 0xD2, 0x40, 0xD6, 0xBD, 0x41, 0x8E, 0xF4, 0x9C, 0x9F, 0x73, 0x0A, 0xCC, 0x9D, 0x84
    };

    private static readonly byte[] Gamma = new byte[]
    {
        0x6A, 0x63, 0x7D, 0x72, 0x2D, 0xAD, 0xDB, 0x21, 0x8B, 0x07, 0xCA, 0x6B, 0x00, 0xA8, 0x8F, 0xB9,
        0x5B, 0xE8, 0x57, 0x70, 0x08, 0x99, 0xE2, 0x9A, 0x29, 0x26, 0x82, 0x0E, 0xB8, 0x8B, 0x7E, 0x4E
    };

    private static readonly ConcurrentDictionary<int, string> StringCache = new();
    private static readonly ConcurrentDictionary<int, byte[]> BytesCache = new();

    private static byte[] DeriveKey()
    {
        var salt = new byte[Alpha.Length];
        for (var i = 0; i < Alpha.Length; i++)
        {
            salt[i] = (byte)(Alpha[i] ^ Beta[i]);
        }
        using var mac = new HMACSHA256(salt);
        return mac.ComputeHash(Gamma);
    }

    public static string DecryptString(byte[] blob)
    {
        var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(blob);
        if (StringCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var plain = Decrypt(blob);
        var s = Encoding.UTF8.GetString(plain);
        StringCache[key] = s;
        return s;
    }

    public static byte[] DecryptBytes(byte[] blob)
    {
        var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(blob);
        if (BytesCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var plain = Decrypt(blob);
        BytesCache[key] = plain;
        return plain;
    }

    public static byte[] DecryptResource(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        var buf = new byte[stream.Length];
        var read = 0;
        while (read < buf.Length)
        {
            var n = stream.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return Decrypt(buf);
    }

    private static byte[] Decrypt(byte[] blob)
    {
        if (blob.Length < 12 + 16)
        {
            throw new CryptographicException("Encrypted payload too short");
        }
        var nonce = new byte[12];
        Buffer.BlockCopy(blob, 0, nonce, 0, 12);
        var tagOffset = blob.Length - 16;
        var tag = new byte[16];
        Buffer.BlockCopy(blob, tagOffset, tag, 0, 16);
        var ctLen = tagOffset - 12;
        var ct = new byte[ctLen];
        Buffer.BlockCopy(blob, 12, ct, 0, ctLen);
        var plain = new byte[ctLen];
        var aesKey = DeriveKey();
        try
        {
            using var aes = new AesGcm(aesKey, 16);
            aes.Decrypt(nonce, ct, tag, plain);
        }
        finally
        {
            Array.Clear(aesKey, 0, aesKey.Length);
        }
        return plain;
    }
}
