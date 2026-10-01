using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Se7enPro.Services;

public static class Curve25519Helper
{
    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private const int A24 = 121665;

    public static (string PrivateKeyBase64, string PublicKeyBase64) GenerateKeyPair()
    {
        var priv = new byte[32];
        RandomNumberGenerator.Fill(priv);
        
        priv[0] &= 248;
        priv[31] &= 127;
        priv[31] |= 64;

        var k = new BigInteger(priv, isUnsigned: true, isBigEndian: false);
        var pubInt = ScalarMult(k, 9);
        var pubBytes = pubInt.ToByteArray(isUnsigned: true, isBigEndian: false);

        var finalPub = new byte[32];
        Array.Copy(pubBytes, finalPub, Math.Min(pubBytes.Length, 32));

        return (Convert.ToBase64String(priv), Convert.ToBase64String(finalPub));
    }

    private static BigInteger Mod(BigInteger x)
    {
        var r = x % P;
        return r < 0 ? r + P : r;
    }

    private static BigInteger ScalarMult(BigInteger k, BigInteger u)
    {
        BigInteger x1 = u;
        BigInteger x2 = 1, z2 = 0;
        BigInteger x3 = u, z3 = 1;
        BigInteger swap = 0;

        for (var t = 254; t >= 0; t--)
        {
            var kt = (k >> t) & 1;
            swap ^= kt;
            if (swap != 0)
            {
                (x2, x3) = (x3, x2);
                (z2, z3) = (z3, z2);
            }
            swap = kt;

            var a = Mod(x2 + z2);
            var aa = Mod(a * a);
            var b = Mod(x2 - z2);
            var bb = Mod(b * b);
            var e = Mod(aa - bb);
            var c = Mod(x3 + z3);
            var d = Mod(x3 - z3);
            var da = Mod(d * a);
            var cb = Mod(c * b);

            var daPlusCb = Mod(da + cb);
            var daMinusCb = Mod(da - cb);

            x3 = Mod(daPlusCb * daPlusCb);
            z3 = Mod(x1 * Mod(daMinusCb * daMinusCb));
            x2 = Mod(aa * bb);
            z2 = Mod(e * Mod(aa + Mod(A24 * e)));
        }

        if (swap != 0)
        {
            (x2, x3) = (x3, x2);
            (z2, z3) = (z3, z2);
        }

        return Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
    }
}
