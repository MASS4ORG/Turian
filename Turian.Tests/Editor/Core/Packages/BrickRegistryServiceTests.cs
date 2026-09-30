using Gaya.Packages;

namespace Turian.Tests;

/// <summary>Registries and licensing terms as a Turian project uses them.</summary>
public sealed class BrickRegistryServiceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-registry-service-{Guid.NewGuid():N}");

    /// <summary>Creates the scratch folder.</summary>
    public BrickRegistryServiceTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>Registries are declared in the manifest with their trusted keys, listed after the public one is added, and removed.</summary>
    [Fact]
    public void RegistriesAreDeclaredAndListed()
    {
        var project = Path.Combine(root, "game");
        Directory.CreateDirectory(project);
        const string key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIETjciFivC4Q7z6o7KhiydqNS1Z5D6+ZZFbGUxQgL7ur test";

        BrickService.AddRegistry(project, new ScopedRegistry { Name = "studio", Url = "https://bricks.studio.example/v1", Scopes = ["com.studio"], Keys = [key] });

        Assert.Equal(["studio", "bricks.mass4.org"], BrickService.Registries(project).Select(r => r.Name));
        var saved = File.ReadAllText(Path.Combine(project, "Packages", "manifest.json"));
        Assert.DoesNotContain("allowUnsigned", saved, StringComparison.Ordinal);
        Assert.Throws<PackageException>(() => BrickService.AddRegistry(project, new ScopedRegistry { Name = "x", Url = "u", Scopes = ["a"] }));
        Assert.Throws<PackageException>(() => BrickService.AddRegistry(project, new ScopedRegistry { Name = "x", Url = "u", Scopes = ["a"], Keys = ["nonsense"] }));
        Assert.Throws<PackageException>(() => BrickService.AddRegistry(project, new ScopedRegistry { Name = "", Url = "u", Scopes = ["a"], AllowUnsigned = true }));

        Assert.True(BrickService.RemoveRegistry(project, "studio"));
        Assert.False(BrickService.RemoveRegistry(project, "studio"));
        Assert.Single(BrickService.Registries(project));
    }

    /// <summary>The public registry is trusted with the MASS4 key that ships with the editor.</summary>
    [Fact]
    public void PublicRegistryCarriesTheMass4Key()
    {
        var registry = ProjectPackages.PublicRegistry;

        Assert.Equal("https://bricks.mass4.org/v1", registry.Url);
        Assert.Contains("user", registry.Scopes);
        Assert.StartsWith("SHA256:", SshSignature.Fingerprint(Assert.Single(registry.Keys)), StringComparison.Ordinal);
        Assert.True(registry.Serves("org.mass4.turian.ui"));
        Assert.True(registry.Serves("user.mateo.rules"));
        Assert.False(registry.Serves("com.acme.rules"));
        Assert.False(registry.Serves("org.mass4x.other"));
    }

    /// <summary>A brick licensed against forks cannot be embedded, and a fork of a brick licensed against republishing cannot be packed.</summary>
    [Fact]
    public void LicenseTermsAreEnforced()
    {
        var brick = BrickService.New(root, "user.mateo.paid");
        var manifest = PackageManifest.Load(brick, ["turian"]);
        manifest.Store = new PackageStoreInfo { Embeddable = false };
        manifest.Save(brick);
        var packed = BrickService.Pack(brick, Path.Combine(root, "out"));
        var project = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        BrickService.Add(project, "user.mateo.paid", $"file:{packed.Path}");

        Assert.Contains("cannot be embedded", Assert.Throws<PackageException>(() => BrickService.Embed(project, "user.mateo.paid")).Message, StringComparison.Ordinal);

        var fork = BrickService.New(root, "user.mateo.fork");
        var forked = PackageManifest.Load(fork, ["turian"]);
        forked.Upstream = "user.mateo.fork@0.1.0";
        forked.Store = new PackageStoreInfo { Redistribute = false };
        forked.Save(fork);
        Assert.Contains("cannot be republished", Assert.Throws<PackageException>(() => BrickService.Pack(fork, Path.Combine(root, "out2"))).Message, StringComparison.Ordinal);
    }
}
