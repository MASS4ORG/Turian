namespace Turian.Tests;

/// <summary>Checks the core model importer's fallbacks for sources that need an editor brick or Assimp.</summary>
public sealed class ModelAssetImporterTests : IDisposable
{
    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-model-{Guid.NewGuid():N}");

    /// <summary>Creates a disposable asset folder.</summary>
    public ModelAssetImporterTests() => Directory.CreateDirectory(Path.Combine(project, "Assets"));

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);

    /// <summary>The core fallback preserves model settings and explains how to enable source import.</summary>
    [Fact]
    public void CoreFallbackRequiresTheEditorBrick()
    {
        var importer = new ModelAssetImporter();
        Assert.False(importer.IsValid(""));
        Assert.True(importer.IsValid("mesh.OBJ"));
        var asset = Assert.IsType<ModelImportAsset>(importer.CreateAsset("mesh.obj"));
        Assert.Same(asset.ImportSettings, importer.ImportSettingsFor(asset));
        Assert.Null(importer.ImportSettingsFor(new Asset()));
        var error = Assert.Throws<NotSupportedException>(() => importer.ImportToCache(asset, "mesh.obj", project));
        Assert.Contains("Install org.mass4.turian.obj", error.Message);
        Assert.Contains(".meta asset ID", error.Message);
        Assert.Empty(Directory.GetFiles(project, "*", SearchOption.AllDirectories));
    }

    /// <summary>Other interchange formats export into runtime glTF geometry.</summary>
    [Fact]
    public void CoreFallbackCopiesOtherSources()
    {
        var path = Path.Combine(project, "Assets", "mesh.stl");
        File.WriteAllText(path, """
            solid triangle
            facet normal 0 0 1
            outer loop
            vertex 0 0 0
            vertex 1 0 0
            vertex 0 1 0
            endloop
            endfacet
            endsolid triangle
            """);
        var importer = new ModelAssetImporter();
        Assert.True(importer.IsValid(path));
        var artifact = Assert.Single(importer.ImportToCache(importer.CreateAsset(path), path, project));
        Assert.Equal("primary.glb", artifact);
        Assert.Equal(3, GltfModelReader.Load(Path.Combine(project, artifact)).Vertices.Length);
    }
}
