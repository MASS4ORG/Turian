namespace Turian.Engine.Core;

/// <summary>
/// Per-asset encryption for OAP blobs (spec §5.2). Encryption is applied to the
/// already-compressed bytes and is its own inverse for every supported codec, so a
/// single <see cref="Apply"/> call performs both encryption and decryption.
/// </summary>
/// <remarks>
/// The key is supplied by the application and is never stored in the package, so the
/// asset bytes cannot be recovered from the file alone. This deters casual extraction;
/// it is not DRM and does not defend against an attacker who controls the running
/// process.
/// </remarks>
public static class OapCrypto
{
    /// <summary>Length of an OAP encryption key, in bytes (256 bits).</summary>
    public const int KeyLength = 32;

    /// <summary>Length of the per-asset nonce, in bytes.</summary>
    public const int NonceLength = 12;

    /// <summary>
    /// Derives a 256-bit key from an arbitrary passphrase with SHA-256. Not salted —
    /// suitable for the lightweight-protection use case, not for password storage.
    /// </summary>
    /// <param name="passphrase">The passphrase.</param>
    /// <returns>A 32-byte key.</returns>
    public static byte[] DeriveKey(string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        return SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
    }

    /// <summary>
    /// Derives the deterministic per-asset nonce: the first 12 bytes of
    /// <c>SHA-256(asset_id ‖ content_crc32)</c>, where <c>content_crc32</c> is the
    /// little-endian 4-byte index field. Both inputs are known to a reader before
    /// decryption, and the pair is unique per asset within a package.
    /// </summary>
    /// <param name="assetId">The 16 raw asset-id bytes (big-endian, as stored).</param>
    /// <param name="contentCrc32">The plaintext CRC-32 from the index entry.</param>
    /// <returns>A 12-byte nonce.</returns>
    public static byte[] NonceFor(ReadOnlySpan<byte> assetId, uint contentCrc32)
    {
        Span<byte> message = stackalloc byte[OapFormat.AssetIdSize + sizeof(uint)];
        assetId[..OapFormat.AssetIdSize].CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message[OapFormat.AssetIdSize..], contentCrc32);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(message, digest);

        return [.. digest[..NonceLength]];
    }

    /// <summary>
    /// Encrypts or decrypts <paramref name="buffer"/> in place with <paramref name="codec"/>.
    /// A no-op for <see cref="OapEncryption.None"/>.
    /// </summary>
    /// <param name="codec">The cipher to apply.</param>
    /// <param name="key">The 32-byte key.</param>
    /// <param name="assetId">The 16 raw asset-id bytes.</param>
    /// <param name="contentCrc32">The plaintext CRC-32 from the index entry.</param>
    /// <param name="buffer">The bytes to transform in place.</param>
    public static void Apply(
        OapEncryption codec,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> assetId,
        uint contentCrc32,
        Span<byte> buffer)
    {
        switch (codec)
        {
            case OapEncryption.None:
                return;

            case OapEncryption.Xor:
                {
                    var nonce = NonceFor(assetId, contentCrc32);
                    for (var i = 0; i < buffer.Length; i++)
                    {
                        buffer[i] ^= (byte)(key[i % key.Length] ^ nonce[i % nonce.Length]);
                    }

                    return;
                }

            case OapEncryption.ChaCha20:
                {
                    var nonce = NonceFor(assetId, contentCrc32);
                    ChaCha20Xor(key, nonce, buffer);
                    return;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unknown OAP encryption codec.");
        }
    }

    // ── ChaCha20 (IETF, RFC 8439) ─────────────────────────────────────────────
    // Raw stream cipher: .NET only exposes ChaCha20Poly1305 (AEAD), so the block
    // function is implemented here. The block counter starts at 0, matching the
    // reference OAP writer.

    static readonly uint[] chaChaConstants =
        [0x61707865, 0x3320646e, 0x79622d32, 0x6b206574];

    /// <summary>
    /// XORs <paramref name="buffer"/> in place with the RFC&#160;8439 ChaCha20 keystream for
    /// <paramref name="key"/> and <paramref name="nonce"/>, block counter starting at 0.
    /// Exposed for testing against the RFC vectors.
    /// </summary>
    /// <param name="key">The 32-byte key.</param>
    /// <param name="nonce">The 12-byte nonce.</param>
    /// <param name="buffer">The bytes to transform in place.</param>
    internal static void ChaCha20Xor(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> buffer)
    {
        if (key.Length != KeyLength)
        {
            throw new ArgumentException($"ChaCha20 key must be {KeyLength} bytes.", nameof(key));
        }

        if (nonce.Length != NonceLength)
        {
            throw new ArgumentException($"ChaCha20 nonce must be {NonceLength} bytes.", nameof(nonce));
        }

        Span<uint> initial = stackalloc uint[16];
        initial[0] = chaChaConstants[0];
        initial[1] = chaChaConstants[1];
        initial[2] = chaChaConstants[2];
        initial[3] = chaChaConstants[3];
        for (var i = 0; i < 8; i++)
        {
            initial[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        }

        initial[12] = 0; // block counter
        initial[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
        initial[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..]);
        initial[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[8..]);

        Span<uint> working = stackalloc uint[16];
        Span<byte> keystream = stackalloc byte[64];

        var offset = 0;
        uint counter = 0;
        while (offset < buffer.Length)
        {
            initial[12] = counter++;
            initial.CopyTo(working);

            for (var round = 0; round < 10; round++)
            {
                QuarterRound(working, 0, 4, 8, 12);
                QuarterRound(working, 1, 5, 9, 13);
                QuarterRound(working, 2, 6, 10, 14);
                QuarterRound(working, 3, 7, 11, 15);
                QuarterRound(working, 0, 5, 10, 15);
                QuarterRound(working, 1, 6, 11, 12);
                QuarterRound(working, 2, 7, 8, 13);
                QuarterRound(working, 3, 4, 9, 14);
            }

            for (var i = 0; i < 16; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[(i * 4)..], working[i] + initial[i]);
            }

            var block = Math.Min(64, buffer.Length - offset);
            for (var i = 0; i < block; i++)
            {
                buffer[offset + i] ^= keystream[i];
            }

            offset += block;
        }
    }

    static void QuarterRound(Span<uint> s, int a, int b, int c, int d)
    {
        s[a] += s[b]; s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 16);
        s[c] += s[d]; s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 12);
        s[a] += s[b]; s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 8);
        s[c] += s[d]; s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 7);
    }
}
