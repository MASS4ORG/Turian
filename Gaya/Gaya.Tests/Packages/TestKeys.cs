using System.Collections.Concurrent;

namespace Gaya.Tests;

/// <summary>Disposable OpenSSH keys for signing tests.</summary>
static class TestKeys
{
    static readonly Lazy<bool> Available = new(ProbeKeygen);

    // Each named key is generated once per test run; every test gets its own copy of the files, so no test shares a
    // folder, only the key material, which holds no state.
    static readonly ConcurrentDictionary<string, Lazy<string>> Generated = new(StringComparer.Ordinal);
    static readonly Lazy<string> SeedFolder = new(CreateSeedFolder);

    /// <summary>Whether <c>ssh-keygen</c> can run here; probed once per test run.</summary>
    /// <returns>True when it is installed.</returns>
    public static bool KeygenAvailable() => Available.Value;

    /// <summary>Creates an unencrypted Ed25519 key in <paramref name="folder"/>.</summary>
    /// <param name="folder">Where the key files go.</param>
    /// <param name="name">The key's name and comment; the same name in another folder is the same key.</param>
    /// <returns>A signer using the key.</returns>
    public static SshKeygenSigner Create(string folder, string name)
    {
        var seed = Generated.GetOrAdd(name, static key => new Lazy<string>(() => Generate(key))).Value;
        var path = Path.Combine(folder, $"{name}_key");
        File.Copy(seed, path);
        File.Copy($"{seed}.pub", $"{path}.pub");
        return new SshKeygenSigner(path);
    }

    static bool ProbeKeygen()
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

    static string CreateSeedFolder()
    {
        var folder = Directory.CreateTempSubdirectory("gaya-test-keys-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temp folder left behind is swept with the rest.
            }
        };
        return folder;
    }

    static string Generate(string name)
    {
        var path = Path.Combine(SeedFolder.Value, $"{name}_key");
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-q", "-t", "ed25519", "-N", "", "-C", name, "-f", path })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }
}
