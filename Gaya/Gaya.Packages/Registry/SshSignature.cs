using System.Buffers.Binary;

namespace Gaya.Packages;

/// <summary>
/// Checks OpenSSH signatures (<c>ssh-keygen -Y sign</c>) made with ed25519 keys. A publisher signs with the tooling and
/// keys they already have, and a host needs only the signer's public key line to verify.
/// </summary>
public static class SshSignature
{
    const string Algorithm = "ssh-ed25519";
    const string Magic = "SSHSIG";
    const string ArmorBegin = "-----BEGIN SSH SIGNATURE-----";
    const string ArmorEnd = "-----END SSH SIGNATURE-----";

    /// <summary>The 32-byte key in an OpenSSH public key line (<c>ssh-ed25519 AAAA… comment</c>).</summary>
    /// <param name="line">The public key line.</param>
    /// <returns>The raw key.</returns>
    /// <exception cref="PackageException">The line is not an ed25519 public key.</exception>
    public static byte[] ParsePublicKey(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            if (parts.Length >= 2 && parts[0] == Algorithm)
            {
                var reader = new Reader(Convert.FromBase64String(parts[1]));
                if (reader.ReadString() is var algorithm && Encoding.ASCII.GetString(algorithm) == Algorithm
                    && reader.ReadString() is { Length: Ed25519.PublicKeyLength } key)
                    return key;
            }
        }
        catch (FormatException)
        {
            // Falls through to the error below.
        }

        throw new PackageException($"'{line}' is not an OpenSSH ed25519 public key (ssh-ed25519 AAAA…).");
    }

    /// <summary>The fingerprint <c>ssh-keygen -l</c> prints for a key: <c>SHA256:</c> and the unpadded base64 hash.</summary>
    /// <param name="publicKeyLine">The public key line.</param>
    /// <returns>The fingerprint.</returns>
    public static string Fingerprint(string publicKeyLine)
    {
        var key = ParsePublicKey(publicKeyLine);
        var blob = Write(Encoding.ASCII.GetBytes(Algorithm), key);
        return $"SHA256:{Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=')}";
    }

    /// <summary>Checks an armored signature of <paramref name="message"/> made under <paramref name="namespace"/>.</summary>
    /// <param name="publicKeyLine">The signer's public key line.</param>
    /// <param name="namespace">The namespace the signature was made for, so a signature cannot be reused for another purpose.</param>
    /// <param name="message">The signed bytes.</param>
    /// <param name="armoredSignature">The <c>-----BEGIN SSH SIGNATURE-----</c> text, or just its base64.</param>
    /// <returns>True when the signature is valid, by that key, for that namespace and message.</returns>
    public static bool Verify(string publicKeyLine, string @namespace, ReadOnlySpan<byte> message, string armoredSignature)
    {
        var key = ParsePublicKey(publicKeyLine);
        try
        {
            var reader = new Reader(Decode(armoredSignature));
            if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != Magic || reader.ReadUInt32() != 1) return false;

            var signedBy = new Reader(reader.ReadString());
            if (Encoding.ASCII.GetString(signedBy.ReadString()) != Algorithm || !signedBy.ReadString().AsSpan().SequenceEqual(key)) return false;
            if (Encoding.UTF8.GetString(reader.ReadString()) != @namespace) return false;

            var reserved = reader.ReadString();
            var hashName = Encoding.ASCII.GetString(reader.ReadString());
            var hashed = hashName switch
            {
                "sha256" => SHA256.HashData(message),
                "sha512" => SHA512.HashData(message),
                _ => null,
            };

            var signature = new Reader(reader.ReadString());
            if (hashed is null || Encoding.ASCII.GetString(signature.ReadString()) != Algorithm) return false;

            var raw = signature.ReadString();
            var signedData = Concatenate(Encoding.ASCII.GetBytes(Magic), Write(Encoding.UTF8.GetBytes(@namespace)),
                Write(reserved), Write(Encoding.ASCII.GetBytes(hashName)), Write(hashed));
            return Ed25519.Verify(key, signedData, raw);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    static byte[] Decode(string armored)
    {
        var lines = armored.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static l => l is not (ArmorBegin or ArmorEnd));
        return Convert.FromBase64String(string.Concat(lines));
    }

    static byte[] Write(params byte[][] strings)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in strings)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
            stream.Write(length);
            stream.Write(value);
        }

        return stream.ToArray();
    }

    static byte[] Concatenate(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

    sealed class Reader(byte[] data)
    {
        int position;

        public uint ReadUInt32()
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
            position += 4;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            var bytes = data.AsSpan(position, count).ToArray();
            position += count;
            return bytes;
        }

        public byte[] ReadString() => ReadBytes(checked((int)ReadUInt32()));
    }
}
