namespace Turian.Tests;

/// <summary>Tests that Core runs scenes without a render backend and reaches one only through <see cref="IRenderDevice"/>.</summary>
public sealed class RenderDeviceBoundaryTests
{
    sealed class CpuModel(ModelBuilder builder) : Model(builder)
    {
        public int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing) => DisposeCalls++;
    }

    sealed class RecordingDevice(Model? model) : IRenderDevice
    {
        public List<Guid> Requests { get; } = [];

        public Model? GetModel(Guid modelAssetId, AssetDatabase assets)
        {
            Requests.Add(modelAssetId);
            return model;
        }
    }

    static ModelBuilder Triangle() => new()
    {
        Vertices = [new(Vector3.Zero, Vector3.One), new(Vector3.UnitX, Vector3.One), new(Vector3.One, Vector3.One)],
        Indices = [0, 1, 2],
    };

    static ModelComponent Attach(Guid modelId, IServiceProvider services)
    {
        var node = new Node();
        var component = new ModelComponent { Model = new AssetReference<ModelAsset>(modelId) };
        node.AddComponent(component);
        node.Awake(null, services);
        return component;
    }

    /// <summary>Core compiles against no graphics, window or image-decoding assembly.</summary>
    [Fact]
    public void CoreReferencesNoRenderBackendAssembly()
    {
        var references = typeof(Node).Assembly.GetReferencedAssemblies().Select(static a => a.Name!).ToList();

        Assert.DoesNotContain(references, static name => name.StartsWith("Silk.NET.Vulkan", StringComparison.Ordinal)
                                                         || name.StartsWith("Silk.NET.Windowing", StringComparison.Ordinal)
                                                         || name.StartsWith("Silk.NET.GLFW", StringComparison.Ordinal)
                                                         || name == "StbImageSharp"
                                                         || name == "Turian.Engine.Vulkan");
    }

    /// <summary>Games that load Core never pull in the editor, the Gaya workbench or a Vulkan or window backend.</summary>
    [Fact]
    public void CoreReferencesNoEditorOrHost()
    {
        // Silk.NET.Input.Common brings the backend-free windowing abstractions; only concrete backends are banned.
        var offenders = AssemblyClosure.Offenders(typeof(Node).Assembly, static name =>
            name is "Gaya.Host" or "Gaya.Sdk" or "Gaya.Packages.Editor" or "Gaya.Plugin.Turian" or "Turian.Engine.Vulkan"
            || name.StartsWith("Turian.Editor.", StringComparison.Ordinal)
            || name.StartsWith("Silk.NET.Vulkan", StringComparison.Ordinal)
            || (name.StartsWith("Silk.NET.Windowing", StringComparison.Ordinal) && name != "Silk.NET.Windowing.Common")
            || name.StartsWith("Silk.NET.GLFW", StringComparison.Ordinal));

        Assert.True(offenders.Length == 0, $"Engine.Core reaches editor or backend assemblies: {string.Join("; ", offenders)}.");
    }

    /// <summary>The model a component draws comes from the scene's render device, once per referenced asset.</summary>
    [Fact]
    public void ModelInstanceComesFromTheInjectedRenderDevice()
    {
        using var model = new CpuModel(Triangle());
        var device = new RecordingDevice(model);
        var modelId = Guid.NewGuid();
        using var services = new ServiceCollection()
            .AddSingleton(new AssetDatabase())
            .AddSingleton<IRenderDevice>(device)
            .BuildServiceProvider();

        var component = Attach(modelId, services);

        Assert.Same(model, component.ModelInstance);
        Assert.Same(model, component.ModelInstance);
        Assert.Equal([modelId], device.Requests);
    }

    /// <summary>A failed load is not retried until the reference changes.</summary>
    [Fact]
    public void FailedLoadIsNotRetriedForTheSameAsset()
    {
        var device = new RecordingDevice(null);
        using var services = new ServiceCollection()
            .AddSingleton(new AssetDatabase())
            .AddSingleton<IRenderDevice>(device)
            .BuildServiceProvider();

        var component = Attach(Guid.NewGuid(), services);

        Assert.Null(component.ModelInstance);
        Assert.Null(component.ModelInstance);
        Assert.Single(device.Requests);
    }

    /// <summary>A headless scene keeps its references but loads no model.</summary>
    [Fact]
    public void HeadlessSceneLoadsNoModel()
    {
        var modelId = Guid.NewGuid();
        using var services = new ServiceCollection().AddSingleton(new AssetDatabase()).BuildServiceProvider();

        var component = Attach(modelId, services);

        Assert.Null(component.RenderDevice);
        Assert.Equal(modelId, component.ModelAssetId);
        Assert.Null(component.ModelInstance);
    }

    /// <summary>The base model takes its submeshes and bounds from the builder and forwards disposal once.</summary>
    [Fact]
    public void ModelTakesLayoutFromItsBuilder()
    {
        var model = new CpuModel(Triangle());

        Assert.Equal(new Bounds(Vector3.Zero, Vector3.One), model.Bounds);
        Assert.Equal(3u, Assert.Single(model.SubMeshes).IndexCount);
        model.Dispose();
        Assert.Equal(1, model.DisposeCalls);
    }
}
