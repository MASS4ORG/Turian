namespace Turian.Tests;

/// <summary>Tests that a <see cref="ModelComponent"/> reads its mesh through the asset database the scene injects.</summary>
public sealed class ModelComponentAssetsTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-model-assets").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    (AssetDatabase Database, Guid MeshId, Guid ModelId, Bounds Bounds) RegisterMesh()
    {
        var modelId = Guid.NewGuid();
        var bounds = new Bounds(new Vector3(-1, -2, -3), new Vector3(1, 2, 3));
        var mesh = new MeshAsset
        {
            Id = Guid.NewGuid(),
            RelativePath = Path.Combine(root, "Assets", "Ship.mesh"),
            Model = new AssetReference<ModelAsset>(modelId),
            SubMeshCount = 1,
            Bounds = bounds,
        };
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        var imported = Path.Combine(root, "Ship.mesh.json");
        File.WriteAllText(imported, Serializer.Serialize(mesh));
        var database = new AssetDatabase();
        Assert.True(database.RegisterAsset(mesh, imported));
        return (database, mesh.Id, modelId, bounds);
    }

    static ModelComponent Attach(Guid meshId, IServiceProvider? services)
    {
        var node = new Node();
        var component = new ModelComponent { Mesh = new AssetReference<MeshAsset>(meshId) };
        node.AddComponent(component);
        if (services is null) node.Awake(null);
        else node.Awake(null, services);
        return component;
    }

    /// <summary>With the scene's database injected, the mesh names its model and supplies its bounds.</summary>
    [Fact]
    public void MeshResolvesThroughTheInjectedDatabase()
    {
        var (database, meshId, modelId, bounds) = RegisterMesh();
        using var services = new ServiceCollection().AddSingleton(database).BuildServiceProvider();

        var component = Attach(meshId, services);

        Assert.Same(database, component.Assets);
        Assert.Equal(modelId, component.ModelAssetId);
        Assert.Equal(bounds, ModelBoundsUtility.ComputeLocalBounds(component, loadModels: false, out var resolved));
        Assert.True(resolved);
    }

    /// <summary>Outside a project — no database in the scene — the mesh resolves to nothing instead of failing.</summary>
    [Fact]
    public void MeshResolvesToNothingWithoutADatabase()
    {
        var (_, meshId, _, _) = RegisterMesh();

        var component = Attach(meshId, services: null);

        Assert.Null(component.Assets);
        Assert.Equal(Guid.Empty, component.ModelAssetId);
        Assert.Equal(Bounds.Empty, ModelBoundsUtility.ComputeLocalBounds(component, loadModels: false, out var resolved));
        Assert.False(resolved);
        Assert.Null(component.ModelInstance);
    }
}
