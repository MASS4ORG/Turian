namespace Turian.Tests;

/// <summary>Tests for OAP key derivation, nonce derivation and the stream ciphers.</summary>
public class OapCryptoTests
{
    /// <summary><see cref="OapCrypto.DeriveKey"/> is the unsalted SHA-256 of the passphrase.</summary>
    [Fact]
    public void DeriveKey_IsSha256OfPassphrase()
    {
        var key = OapCrypto.DeriveKey("open-sesame");
        Assert.Equal(SHA256.HashData("open-sesame"u8.ToArray()), key);
        Assert.Equal(OapCrypto.KeyLength, key.Length);
    }

    /// <summary>The nonce is deterministic in the id and the content CRC.</summary>
    [Fact]
    public void NonceFor_IsDeterministic()
    {
        var id = new byte[OapFormat.AssetIdSize];
        id.AsSpan().Fill(3);

        var a = OapCrypto.NonceFor(id, 0x1234);
        var b = OapCrypto.NonceFor(id, 0x1234);
        var c = OapCrypto.NonceFor(id, 0x1235);

        Assert.Equal(OapCrypto.NonceLength, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    /// <summary>XOR is its own inverse.</summary>
    [Fact]
    public void Xor_IsSymmetric()
    {
        var key = OapCrypto.DeriveKey("hunter2");
        var id = new byte[OapFormat.AssetIdSize];
        id.AsSpan().Fill(7);

        var original = "secret asset bytes, long enough to wrap the key and nonce"u8.ToArray();
        var data = (byte[])original.Clone();

        OapCrypto.Apply(OapEncryption.Xor, key, id, 0x1234, data);
        Assert.NotEqual(original, data);

        OapCrypto.Apply(OapEncryption.Xor, key, id, 0x1234, data);
        Assert.Equal(original, data);
    }

    /// <summary>ChaCha20 is symmetric and sensitive to the key.</summary>
    [Fact]
    public void ChaCha20_IsSymmetricAndKeySensitive()
    {
        var id = new byte[OapFormat.AssetIdSize];
        id.AsSpan().Fill(9);
        var original = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("payload ", 32)));

        var data = (byte[])original.Clone();
        OapCrypto.Apply(OapEncryption.ChaCha20, OapCrypto.DeriveKey("correct"), id, 0xABCD, data);
        Assert.NotEqual(original, data);

        var wrong = (byte[])data.Clone();
        OapCrypto.Apply(OapEncryption.ChaCha20, OapCrypto.DeriveKey("wrong"), id, 0xABCD, wrong);
        Assert.NotEqual(original, wrong);

        OapCrypto.Apply(OapEncryption.ChaCha20, OapCrypto.DeriveKey("correct"), id, 0xABCD, data);
        Assert.Equal(original, data);
    }

    /// <summary>
    /// The ChaCha20 block function matches RFC&#160;8439 Appendix&#160;A.1 test vector&#160;1
    /// (all-zero key and nonce, block counter 0).
    /// </summary>
    [Fact]
    public void ChaCha20_MatchesRfc8439TestVector1()
    {
        Span<byte> key = stackalloc byte[OapCrypto.KeyLength];
        Span<byte> nonce = stackalloc byte[OapCrypto.NonceLength];

        var keystream = new byte[64];
        OapCrypto.ChaCha20Xor(key, nonce, keystream);

        var expected = Convert.FromHexString(
            "76b8e0ada0f13d90405d6ae55386bd28" +
            "bdd219b8a08ded1aa836efcc8b770dc7" +
            "da41597c5157488d7724e03fb8d84a37" +
            "6a43b8f41518a11cc387b669b2ee6586");

        Assert.Equal(expected, keystream);
    }
}
