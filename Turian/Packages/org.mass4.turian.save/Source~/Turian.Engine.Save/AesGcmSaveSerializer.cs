using System.Security.Cryptography;

namespace Turian.Engine.Save;

/// <summary>Encrypts and authenticates a serializer's payload with a fresh nonce for each snapshot.</summary>
public sealed class AesGcmSaveSerializer : ISaveSerializer, IDisposable
{
    readonly ISaveSerializer inner;
    readonly byte[] key;
    bool disposed;

    /// <summary>Copies a game-managed AES key; provision and rotate keys outside save files.</summary>
    public AesGcmSaveSerializer(ISaveSerializer inner, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException("AES keys contain 16, 24 or 32 bytes.", nameof(key));
        }
        this.inner = inner;
        this.key = key.ToArray();
    }

    /// <inheritdoc />
    public string Id => "aes-gcm-v1:" + inner.Id;

    /// <inheritdoc />
    public byte[] Serialize<T>(T state)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var plaintext = inner.Serialize(state);
        try
        {
            var result = new byte[28 + plaintext.Length];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (data.Length < 28)
        {
            throw new InvalidDataException("Encrypted save payload is truncated.");
        }
        var plaintext = new byte[data.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data[..12], data[28..], data.Slice(12, 16), plaintext);
            return inner.Deserialize<T>(plaintext);
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("Save authentication failed.", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Clears the copied encryption key after outstanding save operations have completed.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(key);
        disposed = true;
    }
}
