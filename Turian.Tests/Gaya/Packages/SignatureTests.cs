namespace Turian.Tests;

/// <summary>Ed25519 verification and OpenSSH signatures, against independently produced vectors.</summary>
public sealed class SignatureTests
{
    const string fixtureKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIETjciFivC4Q7z6o7KhiydqNS1Z5D6+ZZFbGUxQgL7ur test@example.com";
    const string fixtureNamespace = "brick-test@example";
    const string fixtureSignature = """
        -----BEGIN SSH SIGNATURE-----
        U1NIU0lHAAAAAQAAADMAAAALc3NoLWVkMjU1MTkAAAAgRONyIWK8LhDvPqjsqGLJ2o1LVn
        kPr5lkVsZTFCAvu6sAAAASYnJpY2stdGVzdEBleGFtcGxlAAAAAAAAAAZzaGE1MTIAAABT
        AAAAC3NzaC1lZDI1NTE5AAAAQIviu+tb4qfol/SXziAHBS8m/R/anH9m/HNqfmHm3ve34D
        PgtfgrEgSy7cmuoscQzOA8kMluMFM2rAuABZyPZAk=
        -----END SSH SIGNATURE-----
        """;

    /// <summary>RFC 8032 test vectors 1 and 2, and three vectors made by another implementation.</summary>
    [Theory]
    [InlineData("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    [InlineData("bbdf3fdc684f3f9b30e9f4ebeb6d4fbe88c6daa5f5b7dbecd8f4fd3249b8317d", "",
        "0c1089c3e415d23eed50d9d9fbed980bd7b5d8f176357ad294a099c0989fa682ce6a5a0465ea5ce1f0fbb4e8464873a8da8ae0f04413c6c394cc0d94c9fc9f0e")]
    [InlineData("39e4aafc15196e5bc6c23f80ec41626a5a8c23e8df66a5d6f9c7d0dbd186f03b", "627269636b",
        "140a6caac7c0cdc3e5fc7a98485b7dba5c63e322d1fbeab629fd9270ab304e3bd81e2481e64157dda3b60243f4ac12a1e18141baf43401b976066b106292e602")]
    public void ValidSignaturesVerify(string publicKey, string message, string signature)
    {
        Assert.True(Ed25519.Verify(Convert.FromHexString(publicKey), Convert.FromHexString(message), Convert.FromHexString(signature)));
    }

    /// <summary>A changed message, signature, key or an unreduced scalar is rejected.</summary>
    [Fact]
    public void TamperedSignaturesFail()
    {
        var key = Convert.FromHexString("39e4aafc15196e5bc6c23f80ec41626a5a8c23e8df66a5d6f9c7d0dbd186f03b");
        var message = Convert.FromHexString("627269636b");
        var signature = Convert.FromHexString("140a6caac7c0cdc3e5fc7a98485b7dba5c63e322d1fbeab629fd9270ab304e3bd81e2481e64157dda3b60243f4ac12a1e18141baf43401b976066b106292e602");

        Assert.False(Ed25519.Verify(key, [.. message, 0], signature));
        var flipped = signature.ToArray();
        flipped[10] ^= 1;
        Assert.False(Ed25519.Verify(key, message, flipped));
        Assert.False(Ed25519.Verify(Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"), message, signature));
        Assert.False(Ed25519.Verify(key, message, signature[..63]));

        // S + L is the same scalar mod L, but a signature must carry the reduced one.
        var s = new BigInteger(signature.AsSpan(32), isUnsigned: true);
        var l = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", CultureInfo.InvariantCulture);
        var malleable = signature.ToArray();
        (s + l).ToByteArray(isUnsigned: true).CopyTo(malleable, 32);
        Assert.False(Ed25519.Verify(key, message, malleable));
    }

    /// <summary>A signature made by <c>ssh-keygen -Y sign</c> verifies, for its key, namespace and message only.</summary>
    [Fact]
    public void SshSignaturesVerify()
    {
        var message = "hello bricks\n"u8.ToArray();

        Assert.True(SshSignature.Verify(fixtureKey, fixtureNamespace, message, fixtureSignature));
        Assert.False(SshSignature.Verify(fixtureKey, "other-namespace", message, fixtureSignature));
        Assert.False(SshSignature.Verify(fixtureKey, fixtureNamespace, "hello bricks"u8.ToArray(), fixtureSignature));
        Assert.False(SshSignature.Verify(fixtureKey, fixtureNamespace, message, "not a signature"));
        Assert.False(SshSignature.Verify("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIC5yk49hAQe0NmpuXPwbWVE1J7LUf+v2w6beGcvpUNpK other", fixtureNamespace, message, fixtureSignature));
    }

    /// <summary>Keys parse from public key lines and fingerprint the way <c>ssh-keygen -l</c> does.</summary>
    [Fact]
    public void KeysParseAndFingerprint()
    {
        Assert.Equal(32, SshSignature.ParsePublicKey(fixtureKey).Length);
        Assert.Equal("SHA256:jM0P76DVYigT/G0MFh1c0t38h967d7tNnGXkF+pKOkc", SshSignature.Fingerprint(fixtureKey));
        Assert.Throws<PackageException>(() => SshSignature.ParsePublicKey("ssh-rsa AAAAB3NzaC1yc2E user"));
        Assert.Throws<PackageException>(() => SshSignature.ParsePublicKey("nonsense"));
    }

    /// <summary>A signature made now with the installed <c>ssh-keygen</c> verifies too; skipped where it is not installed.</summary>
    [Fact]
    public async Task FreshSshKeygenSignaturesVerify()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"gaya-sshsig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            if (!await Run(folder, "-q", "-t", "ed25519", "-N", "", "-f", "key")) return;

            await File.WriteAllTextAsync(Path.Combine(folder, "message"), "a brick", TestContext.Current.CancellationToken);
            Assert.True(await Run(folder, "-Y", "sign", "-f", "key", "-n", "bricks@mass4.org", "message"));

            var key = await File.ReadAllTextAsync(Path.Combine(folder, "key.pub"), TestContext.Current.CancellationToken);
            var signature = await File.ReadAllTextAsync(Path.Combine(folder, "message.sig"), TestContext.Current.CancellationToken);
            Assert.True(SshSignature.Verify(key, "bricks@mass4.org", "a brick"u8.ToArray(), signature));

            var fingerprint = await Output(folder, "-l", "-f", "key.pub");
            Assert.StartsWith(SshSignature.Fingerprint(key), fingerprint.Split(' ')[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    static async Task<bool> Run(string folder, params string[] arguments)
    {
        try
        {
            _ = await Output(folder, arguments);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    static async Task<string> Output(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("ssh-keygen") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        return output;
    }
}
