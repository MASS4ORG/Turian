namespace Turian.Tests;

/// <summary>The process-wide key that runtime OAP mounts decrypt with.</summary>
public sealed class OapRuntimeKeyTests
{
    /// <summary>A host override replaces the environment key, rejects a wrong length, and null disables decryption.</summary>
    [Fact]
    public void OverrideReplacesKeyUntilCleared()
    {
        try
        {
            OapRuntimeKey.SetPassphrase("runtime secret");
            Assert.Equal(OapCrypto.DeriveKey("runtime secret"), OapRuntimeKey.Current);
            Assert.Throws<ArgumentException>(() => OapRuntimeKey.Set(new byte[OapCrypto.KeyLength - 1]));
            Assert.Equal(OapCrypto.DeriveKey("runtime secret"), OapRuntimeKey.Current);
        }
        finally
        {
            OapRuntimeKey.Set(null);
        }

        Assert.Null(OapRuntimeKey.Current);
    }
}
