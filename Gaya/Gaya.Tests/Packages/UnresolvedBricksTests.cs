namespace Gaya.Tests;

/// <summary>Checks that a resolution that cannot find its bricks says which, why, and what to do, in one message.</summary>
[Collection(SerialTests.Name)]
public sealed class UnresolvedBricksTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-unresolved-{Guid.NewGuid():N}");
    readonly PackageStore store;
    readonly SshKeygenSigner signer;
    readonly string project;
    readonly string registry;

    const string Present = "com.acme.present";

    /// <summary>Creates an isolated signed registry holding one brick.</summary>
    public UnresolvedBricksTests()
    {
        if (!TestKeys.KeygenAvailable())
        {
            Assert.Skip("ssh-keygen is not installed");
        }

        Directory.CreateDirectory(root);
        project = Path.Combine(root, "project");
        registry = Path.Combine(root, "registry");
        store = new PackageStore(Path.Combine(root, "store"));
        signer = TestKeys.Create(root, "registry");
        Publish(Present);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>Every missing brick is listed together, with what each registry holds and how to fix it.</summary>
    [Fact]
    public async Task MissingBricksAreReportedTogether()
    {
        Project(Registry(), Present, "com.acme.absent", "com.acme.gone");

        var error = await Assert.ThrowsAsync<PackageException>(() => Resolve());

        Assert.Equal(2, error.Problems.Count);
        Assert.Contains("2 bricks could not be resolved", error.Message, StringComparison.Ordinal);
        Assert.Contains("com.acme.absent", error.Message, StringComparison.Ordinal);
        Assert.Contains("com.acme.gone", error.Message, StringComparison.Ordinal);
        Assert.Contains("Registries: test lists 1 brick(s).", error.Message, StringComparison.Ordinal);
        Assert.Contains("scopedRegistries", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A registry that cannot be read is named with its reason.</summary>
    [Fact]
    public async Task UnreadableRegistriesAreNamed()
    {
        var unreachable = Registry();
        unreachable.Url = RegistryPublisher.LocationOf(Path.Combine(root, "nowhere"));
        Project(unreachable, Present);

        var error = await Assert.ThrowsAsync<PackageException>(() => Resolve());

        Assert.Single(error.Problems);
        Assert.Contains("test could not be read", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A registry added without its key says the brick's signer and where its keys are listed.</summary>
    [Fact]
    public async Task RegistriesWithoutKeysSayHowToTrustThem()
    {
        var keyless = Registry();
        keyless.Keys = [];
        Project(keyless, Present);

        var error = await Assert.ThrowsAsync<PackageException>(() => Resolve());

        Assert.Contains("no signing key is trusted for test", error.Message, StringComparison.Ordinal);
        Assert.Contains(SshSignature.Fingerprint(signer.PublicKey), error.Message, StringComparison.Ordinal);
        Assert.Contains("/keys lists the keys it signs with", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A brick signed by another key than the trusted ones names both.</summary>
    [Fact]
    public async Task ForeignSignersNameTheTrustedKeys()
    {
        var other = TestKeys.Create(root, "other").PublicKey;
        var mistrusted = Registry();
        mistrusted.Keys = [other];
        Project(mistrusted, Present);

        var error = await Assert.ThrowsAsync<PackageException>(() => Resolve());

        Assert.Contains($"not one of the 1 key(s) trusted for it ({SshSignature.Fingerprint(other)})", error.Message, StringComparison.Ordinal);
        Assert.Contains(SshSignature.Fingerprint(signer.PublicKey), error.Message, StringComparison.Ordinal);
    }

    Task<PackageResolution> Resolve() =>
        new PackageResolver(store, new PackageResolverOptions()).ResolveAsync(project, TestContext.Current.CancellationToken);

    void Project(ScopedRegistry scoped, params string[] ids)
    {
        var manifest = new ProjectManifest { ScopedRegistries = [scoped] };
        foreach (var id in ids)
        {
            manifest.Dependencies[id] = "^1.0.0";
        }

        manifest.Save(project);
    }

    ScopedRegistry Registry() => new()
    {
        Name = "test",
        Url = RegistryPublisher.LocationOf(registry),
        Keys = [signer.PublicKey],
    };

    void Publish(string id)
    {
        var folder = Path.Combine(root, "sources", id);
        Directory.CreateDirectory(folder);
        new PackageManifest { Name = id, Version = SemanticVersion.Parse("1.0.0") }.Save(folder);
        var packed = BrickArchive.Pack(folder, Path.Combine(root, "packed"));
        RegistryPublisher.Publish(registry, packed.Path, signer, signer, claim: "com.acme");
    }
}
