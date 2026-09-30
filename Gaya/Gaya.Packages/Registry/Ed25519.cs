using System.Numerics;

namespace Gaya.Packages;

/// <summary>
/// Ed25519 signature verification (RFC 8032), so a host can check a brick's signature with nothing but the base class
/// library. Only verification is here: signing happens with the publisher's own tooling, which keeps private keys out
/// of this code.
/// </summary>
/// <remarks>
/// Big-integer arithmetic is not constant time, which does not matter for verifying a public signature with a public
/// key. A signature is accepted only when its scalar is reduced, as RFC 8032 requires.
/// </remarks>
public static class Ed25519
{
    /// <summary>The length of a public key, in bytes.</summary>
    public const int PublicKeyLength = 32;

    /// <summary>The length of a signature, in bytes.</summary>
    public const int SignatureLength = 64;

    static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", CultureInfo.InvariantCulture);
    static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
    static readonly Point Base = BasePoint();

    readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    /// <summary>Checks a signature.</summary>
    /// <param name="publicKey">The signer's 32-byte public key.</param>
    /// <param name="message">The signed bytes.</param>
    /// <param name="signature">The 64-byte signature.</param>
    /// <returns>True when the signature is valid for the message under the key.</returns>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyLength || signature.Length != SignatureLength) return false;

        var a = Decode(publicKey);
        var r = Decode(signature[..32]);
        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
        if (a is null || r is null || s >= L) return false;

        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        hash.AppendData(signature[..32]);
        hash.AppendData(publicKey);
        hash.AppendData(message);
        var k = new BigInteger(hash.GetHashAndReset(), isUnsigned: true, isBigEndian: false) % L;

        var left = Multiply(Base, s);
        var right = Add(r.Value, Multiply(a.Value, k));
        return Mod(left.X * right.Z - right.X * left.Z) == 0 && Mod(left.Y * right.Z - right.Y * left.Z) == 0;
    }

    static Point BasePoint()
    {
        var y = Mod(4 * Inverse(5));
        var x = RecoverX(y, 0)!.Value;
        return new Point(x, y, 1, Mod(x * y));
    }

    static BigInteger Mod(BigInteger value)
    {
        var result = value % P;
        return result.Sign < 0 ? result + P : result;
    }

    static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    static BigInteger? RecoverX(BigInteger y, int sign)
    {
        var x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
        if (x2.IsZero) return sign == 1 ? null : BigInteger.Zero;

        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero) x = Mod(x * SqrtMinusOne);
        if (!Mod(x * x - x2).IsZero) return null;

        return (x.IsEven ? 0 : 1) != sign ? P - x : x;
    }

    static Point? Decode(ReadOnlySpan<byte> bytes)
    {
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        var sign = (int)(value >> 255);
        var y = value & ((BigInteger.One << 255) - 1);
        if (y >= P || RecoverX(y, sign) is not { } x) return null;

        return new Point(x, y, 1, Mod(x * y));
    }

    static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(p.T * 2 * D * q.T);
        var d = Mod(p.Z * 2 * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    static Point Multiply(Point point, BigInteger scalar)
    {
        var result = new Point(0, 1, 1, 0);
        var addend = point;
        for (; !scalar.IsZero; scalar >>= 1)
        {
            if (!scalar.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
        }

        return result;
    }
}
