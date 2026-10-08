using Turian.Editor.Obj;

namespace Turian.Tests;

/// <summary>Checks the optional OBJ importer, metadata continuity and cooked runtime/export contracts.</summary>
public sealed class ObjModelImporterTests : IDisposable
{
    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-obj-{Guid.NewGuid():N}");

    const string Triangle = """
        v 0 1 0
        v 1 1 0
        v 0 2 0
        vt 0 0
        vt 1 0
        vt 0 1
        vn 0 1 0
        f 1/1/1 2/2/1 3/3/1
        """;

    /// <summary>Creates a disposable asset folder.</summary>
    public ObjModelImporterTests() => Directory.CreateDirectory(Path.Combine(project, "Assets"));

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);

    /// <summary>OBJ extension matching excludes other model formats and empty paths.</summary>
    [Theory]
    [InlineData("mesh.obj", true)]
    [InlineData("mesh.OBJ", true)]
    [InlineData("mesh.glb", false)]
    [InlineData("mesh.fbx", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    public void ClaimsOnlyObj(string path, bool expected) =>
        Assert.Equal(expected, new ObjModelImporter().IsValid(path));

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
        Assert.Contains("builtin:org.mass4.turian.obj", error.Message);
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

    /// <summary>The brick outranks core fallbacks, retains metadata ids and exports only cooked geometry.</summary>
    [Fact]
    public void BrickImportsAndExportsWithTheExistingAssetId()
    {
        var obj = new ObjModelImporter();
        var path = Path.Combine(project, "Assets", "mesh.obj");
        File.WriteAllText(path, Triangle);
        var asset = Assert.IsType<ModelImportAsset>(obj.CreateAsset(path));
        Serializer.Save(path + ".meta", asset);
        Assert.Same(asset.ImportSettings, obj.ImportSettingsFor(asset));
        Assert.Null(obj.ImportSettingsFor(new Asset()));

        new ProjectManifest { Dependencies = { ["org.mass4.turian.obj"] = "builtin:org.mass4.turian.obj" } }
            .Save(project);
        var settings = new SettingsService();
        var database = new AssetDatabase();
        using var importer = new AssetImporter(NullLogger.Instance, database, settings);
        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));
        Assert.IsType<ObjModelImporter>(importer.ImporterFor(path));
        Assert.Equal(asset.Id, Asset.Load(path + ".meta")!.Id);
        Assert.True(database.TryGetAsset(asset.Id, out var record));
        Assert.EndsWith("primary.glb", record!.ImportedRelativePath);
        var blob = GltfModelReader.Load(record.ResolveContentPath());
        Assert.Equal(3, blob.Vertices.Length);
        Assert.Equal(new uint[] { 0, 1, 2 }, blob.Indices);
        Assert.All(blob.Vertices, vertex => Assert.Equal(Vector3.One, vertex.Color));
        Assert.Equal(new Vector2(0, 0), blob.Vertices[2].Uv);

        var output = new OapArchiveBuilder(NullLogger.Instance)
            .BuildPackage(project, Path.Combine(project, "game.oap"));
        var reader = OapReader.OpenFile(output.OapFilePath);
        var entry = reader.FindById(asset.Id)!.Value;
        Assert.Equal(OapAssetType.Mesh, (OapAssetType)entry.AssetType);
        Assert.Equal(blob.Indices, GltfModelReader.Read(reader.ReadAsset(entry, verify: true)).Indices);

        var brick = Assert.Single(ProjectPackages.Resolve(project).Packages);
        Assert.True(brick.Manifest.EditorOnly);
        Assert.Empty(BrickAssemblies.RuntimeAssemblies(brick));
        Assert.DoesNotContain(BrickAssemblies.EditorAssemblies(brick),
            p => Path.GetFileName(p) == "JeremyAnsel.Media.WavefrontObj.dll");
        var buildSettings = new BuildAppSettings { Title = "Game", ProjectAbsoluteDir = project };
        var executable = CsProjectGenerator.GenerateExecutable(buildSettings, NullLogger.Instance);
        Assert.DoesNotContain("WavefrontObj", executable.RawXml);
        Assert.DoesNotContain("Turian.Editor.Obj", executable.RawXml);
    }

    /// <summary>Colored vertices and repeated face vertices keep their values and share indices.</summary>
    [Fact]
    public void BakePreservesColorsAndDeduplicatesVertices()
    {
        var path = Path.Combine(project, "Assets", "colored.obj");
        File.WriteAllText(path, Triangle.Replace("v 0 1 0", "v 0 1 0 1 0 0", StringComparison.Ordinal)
            + "\nf 1/1/1 2/2/1 3/3/1\n");
        var importer = new ObjModelImporter();
        var artifact = Assert.Single(importer.ImportToCache(importer.CreateAsset(path), path, project));
        var blob = GltfModelReader.Load(Path.Combine(project, artifact));
        Assert.Equal(Vector3.UnitX, blob.Vertices[0].Color);
        Assert.Equal(3, blob.Vertices.Length);
        Assert.Equal(new uint[] { 0, 1, 2, 0, 1, 2 }, blob.Indices);
        Assert.Equal(new Bounds(new Vector3(0, 1, 0), new Vector3(1, 2, 0)), blob.SubMeshes[0].Bounds);
    }
}
