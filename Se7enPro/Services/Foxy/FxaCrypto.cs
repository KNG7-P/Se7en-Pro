using System;
using System.Security.Cryptography;
using System.Text;

namespace Se7enPro.Services.Foxy;

public static class FxaCrypto
{
    private const string ProtocolVersion = "identity.mozilla.com/picl/v1/";
    private const int Pbkdf2Rounds = 1000;
    private const int StretchedPwLen = 32;
    private const int HkdfLen = 32;

    public static byte[] DeriveQuickStretch(string email, string password)
    {
        var salt = Encoding.UTF8.GetBytes(ProtocolVersion + "quickStretch:" + email);
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Pbkdf2Rounds, HashAlgorithmName.SHA256, StretchedPwLen);
    }

    public static string DeriveAuthPw(string email, string password) =>
        Convert.ToHexString(HkdfExpand(DeriveQuickStretch(email, password),
            ProtocolVersion + "authPW", HkdfLen)).ToLowerInvariant();

    public static (string TokenId, byte[] HmacKey) DeriveHawkCredentials(string sessionTokenHex)
    {
        var expanded = HkdfExpand(Convert.FromHexString(sessionTokenHex),
            ProtocolVersion + "sessionToken", 64);
        return (Convert.ToHexString(expanded[..32]).ToLowerInvariant(), expanded[32..64]);
    }

    public static byte[] HkdfExpand(byte[] ikm, string info, int length, byte[]? salt = null)
    {
        salt ??= new byte[32];
        using var prk = new HMACSHA256(salt);
        var key = prk.ComputeHash(ikm);
        var infoBytes = Encoding.UTF8.GetBytes(info);
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var written = 0;
        byte counter = 1;
        using var hmac = new HMACSHA256(key);
        while (written < length)
        {
            var block = new byte[previous.Length + infoBytes.Length + 1];
            Buffer.BlockCopy(previous, 0, block, 0, previous.Length);
            Buffer.BlockCopy(infoBytes, 0, block, previous.Length, infoBytes.Length);
            block[^1] = counter;
            previous = hmac.ComputeHash(block);
            var take = Math.Min(previous.Length, length - written);
            Buffer.BlockCopy(previous, 0, output, written, take);
            written += take;
            counter++;
        }
        return output;
    }

    public static string BuildHawkHeader(string method, Uri url, string tokenId, byte[] hmacKey, byte[]? body = null)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonceBytes = new byte[6];
        RandomNumberGenerator.Fill(nonceBytes);
        var nonce = Convert.ToBase64String(nonceBytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var pathAndQuery = url.AbsolutePath + (string.IsNullOrEmpty(url.Query) ? "" : url.Query);
        var host = url.Host;
        var port = url.Port > 0 ? url.Port : (url.Scheme == "https" ? 443 : 80);

        var hash = "";
        if (body is { Length: > 0 })
        {
            var prefix = Encoding.UTF8.GetBytes("hawk.1.payload\napplication/json\n");
            var suffix = Encoding.UTF8.GetBytes("\n");
            var buffer = new byte[prefix.Length + body.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, buffer, 0, prefix.Length);
            Buffer.BlockCopy(body, 0, buffer, prefix.Length, body.Length);
            Buffer.BlockCopy(suffix, 0, buffer, prefix.Length + body.Length, suffix.Length);
            hash = Convert.ToBase64String(SHA256.HashData(buffer));
        }

        var macInput = $"hawk.1.header\n{ts}\n{nonce}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{host}\n{port}\n{hash}\n\n";
        using var hmac = new HMACSHA256(hmacKey);
        var mac = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(macInput)));

        var header = $"Hawk id=\"{tokenId}\", ts=\"{ts}\", nonce=\"{nonce}\", mac=\"{mac}\"";
        if (!string.IsNullOrEmpty(hash)) header += $", hash=\"{hash}\"";
        return header;
    }
}
