namespace Turian.Tests;

/// <summary>Turning twins on and swapping one brick between its source and its twin, against a signed registry.</summary>
[Collection(SerialTests.Name)]
public sealed class BrickSwapTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-swap-{Guid.NewGuid():N}");
    readonly string? previousStore = Environment.GetEnvironmentVariable(PackageStore.StoreVariable);
    string project = "";

    /// <summary>Isolates the brick store.</summary>
    public BrickSwapTests()
    {
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(PackageStore.StoreVariable, Path.Combine(root, "store"));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PackageStore.StoreVariable, previousStore);
        ProjectPackages.Invalidate(project);
        PrecastBrickFixture.DeleteTree(root);
    }

    /// <summary>
    /// With twins on a brick resolves to its twin and swapping keeps it as source through the manifest's sources; with
    /// them off, swapping moves the declaration between the two ids.
    /// </summary>
    [Fact]
    public async Task SwapsBetweenSourceAndTwin()
    {
        if (!KeygenAvailable())
        {
            Assert.Skip("ssh-keygen is not installed");
        }

        var registry = Path.Combine(root, "registry");
        var key = Key("publisher");
        Publish(registry, key, twin: false);
        Publish(registry, key, twin: true);
        project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "game")))!;
        var (manifest, _) = ProjectManifest.Load(project, includeUserOverride: false);
        manifest.Dependencies["user.mateo.rules"] = "^1.0.0";
        manifest.ScopedRegistries.Add(new ScopedRegistry { Name = "test", Url = RegistryPublisher.LocationOf(registry), Keys = [key.PublicKey] });
        _ = BrickService.SaveManifest(project, manifest);

        Assert.Contains("user.mateo.rules.bake", Ids(BrickService.SetTwins(project, on: true)));
        var editable = BrickService.Swap(project, "user.mateo.rules", toSource: true).Packages.Single(p => p.Id == "user.mateo.rules");
        Assert.Equal(PackageOrigin.Embedded, editable.Origin);
        Assert.Equal(FileAttributes.Normal, File.GetAttributes(Path.Combine(editable.RootPath, "Runtime", "Rules.txt")));
        File.WriteAllText(Path.Combine(editable.RootPath, "Runtime", "Rules.txt"), "Edited during the meeting.");
        Assert.Equal(["user.mateo.rules"], ProjectManifest.Load(project, includeUserOverride: false).Manifest.Sources);
        Assert.Contains("user.mateo.rules.bake", Ids(BrickService.Swap(project, "user.mateo.rules.bake", toSource: false)));
        Assert.Empty(ProjectManifest.Load(project, includeUserOverride: false).Manifest.Sources);
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(project, ".Cache", "Trash"), "Rules.txt", SearchOption.AllDirectories),
            file => File.ReadAllText(file) == "Edited during the meeting.");

        _ = BrickService.SetTwins(project, on: false);
        Assert.Contains("user.mateo.rules.bake", Ids(BrickService.Swap(project, "user.mateo.rules", toSource: false)));
        Assert.True(ProjectManifest.Load(project, includeUserOverride: false).Manifest.Dependencies.ContainsKey("user.mateo.rules.bake"));
        Assert.Throws<PackageException>(() => BrickService.Swap(project, "user.mateo.other", toSource: false));
    }

    static IEnumerable<string> Ids(PackageResolution resolution) => resolution.Packages.Select(static p => p.Id);

    void Publish(string registry, SshKeygenSigner key, bool twin)
    {
        var folder = Path.Combine(root, twin ? "twin" : "source");
        Directory.CreateDirectory(Path.Combine(folder, "Runtime"));
        var manifest = new PackageManifest { Name = "user.mateo.rules", Version = SemanticVersion.Parse("1.0.0") };
        if (twin)
        {
            var index = RegistryIndex.Parse(File.ReadAllText(Path.Combine(RegistryPublisher.LocationOf(registry), "index.json")));
            manifest.Name = "user.mateo.rules.bake";
            manifest.BakeOf = new PackageBakeOf
            {
                Brick = "user.mateo.rules",
                Version = manifest.Version,
                Integrity = index.Bricks["user.mateo.rules"].Versions["1.0.0"].Integrity,
            };
        }

        manifest.Save(folder);
        File.WriteAllText(Path.Combine(folder, "Runtime", "Rules.txt"), manifest.Name);
        var packed = BrickArchive.Pack(folder, Path.Combine(root, "packed", twin ? "twin" : "source"), ["turian"]);
        RegistryPublisher.Publish(registry, packed.Path, key, key, reservedCategoryPrefixes: ["turian"]);
    }

    SshKeygenSigner Key(string name)
    {
        var path = Path.Combine(root, $"{name}_key");
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-q", "-t", "ed25519", "-N", "", "-C", name, "-f", path })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        return new SshKeygenSigner(path);
    }

    static bool KeygenAvailable()
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
}
