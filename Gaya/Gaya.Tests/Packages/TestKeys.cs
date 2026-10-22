namespace Gaya.Tests;

/// <summary>Disposable OpenSSH keys for signing tests.</summary>
static class TestKeys
{
    /// <summary>Whether <c>ssh-keygen</c> can run here.</summary>
    /// <returns>True when it is installed.</returns>
    public static bool KeygenAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ssh-keygen", "-?") { RedirectStandardError = true, RedirectStandardOutput = true });
            process!.WaitForExit();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Creates an unencrypted Ed25519 key in <paramref name="folder"/>.</summary>
    /// <param name="folder">Where the key files go.</param>
    /// <param name="name">The key's name and comment.</param>
    /// <returns>A signer using the key.</returns>
    public static SshKeygenSigner Create(string folder, string name)
    {
        var path = Path.Combine(folder, $"{name}_key");
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-q", "-t", "ed25519", "-N", "", "-C", name, "-f", path })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return new SshKeygenSigner(path);
    }
}
