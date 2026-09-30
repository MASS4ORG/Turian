namespace Turian.Tests;

/// <summary>
/// Package assets: indexed beside the project's own, owned by the installing project, and never allowed to reuse
/// an asset id.
/// </summary>
public sealed class PackageAssetTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-package-assets-{Guid.NewGuid():N}");
    readonly string project;
    readonly string assets;

    /// <summary>Creates a project with an empty Assets folder.</summary>
    public PackageAssetTests()
    {
        project = Path.Combine(root, "game");
        assets = Path.Combine(project, "Assets");
        Directory.CreateDirectory(assets);
        TestAssetDatabase.Reset();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        TestAssetDatabase.Reset();
        Directory.Delete(root, recursive: true);
    }

    /// <summary>A package's metas are indexed under the installing project, and its <c>~</c> folders are not.</summary>
    [Fact]
    public void PackageAssetsJoinTheProjectDatabase()
    {
        var package = Path.Combine(root, "inventory");
        var icon = Guid.NewGuid();
        Meta(Path.Combine(package, "Runtime", "Icon.png"), icon);
        Meta(Path.Combine(package, "Samples~", "Demo.png"), Guid.NewGuid());
        Meta(Path.Combine(assets, "Player.png"), Guid.NewGuid());

        var database = new AssetDatabase();
        database.SetPackageRoots(project, [package]);
        database.BuildDatabase(assets);

        Assert.Equal(2, database.Assets.Count);
        Assert.True(database.TryGetAsset(icon, out var record));
        Assert.Equal(project, record!.ProjectRootPath);
    }

    /// <summary>An asset id claimed by two packages fails, naming both.</summary>
    [Fact]
    public void DuplicateIdsAcrossPackagesFail()
    {
        var shared = Guid.NewGuid();
        Meta(Path.Combine(root, "a", "Icon.png"), shared);
        Meta(Path.Combine(root, "b", "Icon.png"), shared);

        var error = Assert.Throws<PackageException>(() =>
            PackageAssetIds.EnsureUnique(assets, [Package("com.acme.a", "a"), Package("com.acme.b", "b")]));
        Assert.Contains("com.acme.a", error.Message, StringComparison.Ordinal);
        Assert.Contains("com.acme.b", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A package reusing one of the project's own ids fails too; distinct ids pass.</summary>
    [Fact]
    public void PackagesCannotReuseProjectIds()
    {
        var shared = Guid.NewGuid();
        Meta(Path.Combine(assets, "Icon.png"), shared);
        Meta(Path.Combine(root, "a", "Other.png"), Guid.NewGuid());
        PackageAssetIds.EnsureUnique(assets, [Package("com.acme.a", "a")]);

        Meta(Path.Combine(root, "a", "Icon.png"), shared);
        Assert.Throws<PackageException>(() => PackageAssetIds.EnsureUnique(assets, [Package("com.acme.a", "a")]));
    }

    ResolvedPackage Package(string id, string folder) =>
        new(id, new PackageManifest { Name = id, Version = SemanticVersion.Parse("1.0.0") }, Path.Combine(root, folder),
            PackageOrigin.File, $"file:../{folder}", null, null, 1, false);

    static void Meta(string assetPath, Guid id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        File.WriteAllBytes(assetPath, []);
        File.WriteAllText($"{assetPath}.meta",
            $$"""{ "__TypeId": "a3000000-0000-4000-8000-000000000003", "RelativePath": "{{Path.GetFileName(assetPath)}}", "Id": "{{id}}" }""");
    }
}
