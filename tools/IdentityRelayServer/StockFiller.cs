using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Se7enRelay;

/// <summary>
/// Generates and registers Cloudflare WARP identities for relay stock inventory.
/// </summary>
internal static class StockFiller
{
    /// <summary>
    /// Generates a keypair and registers it with Cloudflare, returning the provisioned identity.
    /// </summary>
    public static async Task<StockedIdentity?> CreateAsync(CancellationToken ct)
    {
        var (privB64, pubB64) = GenerateKeyPair();

        var body = JsonSerializer.Serialize(new
        {
            key = pubB64,
            install_id = "",
            fcm_token = "",
            tos = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            model = "PC",
            serial_number = RandomBytesHex(16),
            os_version = "",
            key_type = "curve25519",
            tunnel_type = "wireguard",
            locale = "en_US",
        });

        var (status, text) = await Relay.ForwardAsync(
            HttpMethod.Post, "reg", body, bearer: null, ct);

        if (status is < 200 or >= 300)
        {
            Console.Error.WriteLine($"[relay] pre-warm registration returned HTTP {status}: {Trim(text)}");
            return null;
        }

        var parsed = Relay.ParseRegistration(text, privB64);
        if (parsed is null)
        {
            Console.Error.WriteLine($"[relay] pre-warm reply had an unexpected shape: {Trim(text)}");
        }

        return parsed;
    }

    /// <summary>
    /// Generates an X25519 keypair.
    /// </summary>
    private static (string PrivateKeyB64, string PublicKeyB64) GenerateKeyPair()
    {
        using var rng = RandomNumberGenerator.Create();
        var priv = new byte[32];
        rng.GetBytes(priv);

        // Clamp the scalar as X25519 requires, so the key is well formed.
        priv[0] &= 248;
        priv[31] &= 127;
        priv[31] |= 64;

        var privKey = new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(priv);
        var pub = privKey.GeneratePublicKey();
        var pubBytes = pub.GetEncoded();

        return (Convert.ToBase64String(priv), Convert.ToBase64String(pubBytes));
    }

    private static string RandomBytesHex(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Trim(string s) => s.Length <= 180 ? s : s[..180] + "...";
}

/// <summary>Reads and writes the stock file.</summary>
internal static class StockFile
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static List<StockedIdentity> Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<StockedIdentity>>(json, JsonOpts) ?? new List<StockedIdentity>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[relay] could not read {path}: {ex.Message}");
            return new List<StockedIdentity>();
        }
    }

    public static void Save(string path, Stock stock)
    {
        try
        {
            // Only identities that were never reserved are written back, so a restart cannot
            // resurrect one that has already gone out to a client.
            var pending = new List<StockedIdentity>();
            while (stock.TryReserve(out var item))
            {
                if (item is not null) pending.Add(item);
                stock.Unreserve();
            }

            File.WriteAllText(path, JsonSerializer.Serialize(pending, JsonOpts));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[relay] could not write {path}: {ex.Message}");
        }
    }
}