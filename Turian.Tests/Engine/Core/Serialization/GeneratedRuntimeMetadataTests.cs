namespace Turian.Tests;

/// <summary>Verifies closed constructor, lifecycle and private service-injection registrations.</summary>
[Collection(SerialTests.Name)]
public sealed class GeneratedRuntimeMetadataTests
{
    /// <summary>Missing optional value-type services receive their default instead of unboxing null.</summary>
    [Fact]
    public void OptionalValueServiceUsesDefault()
    {
        using var empty = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(0, GeneratedComponentMetadata.ResolveService<int>(empty, true, false));
        Assert.Equal(0, GeneratedComponentMetadata.ResolveService<int>(empty, false, true));
        Assert.Throws<InvalidOperationException>(() => GeneratedComponentMetadata.ResolveService<int>(empty, false, false));
        using var populated = new ServiceCollection().AddSingleton(typeof(int), 42).BuildServiceProvider();
        Assert.Equal(42, GeneratedComponentMetadata.ResolveService<int>(populated, false, false));
    }

    /// <summary>Derived node metadata injects base private setters and retains inherited frame callbacks.</summary>
    [Fact]
    public void NodeMetadataPreservesBaseInjectionAndCallbacks()
    {
        Assert.True(GeneratedNodeMetadata.TryGet(typeof(GeneratedInjectionChildNode), out var metadata));
        var node = new GeneratedInjectionChildNode();
        var service = new GeneratedInjectionService();
        using var services = new ServiceCollection().AddSingleton(service).BuildServiceProvider();
        metadata.Inject(node, services, false);
        Assert.True(metadata.HasFrameCallbacks);
        Assert.Same(service, node.Required);
    }

    /// <summary>Contexts may freeze their own options without freezing the options being composed.</summary>
    [Fact]
    public void ExportContextDoesNotFreezeSerializerOptionsDuringComposition()
    {
        try
        {
            GeneratedJson.Configure(static options => new GeneratedRuntimeTestJsonContext(options));
            var value = new GeneratedRuntimeValue(42);
            Assert.Equal(value, Serializer.LoadData<GeneratedRuntimeValue>(Serializer.Serialize(value)));
        }
        finally
        {
            GeneratedJson.Configure(null);
        }
    }

    /// <summary>Generated injection assigns private setters and preserves required/optional service behavior.</summary>
    [Fact]
    public void PrivateInjectionAndCallbacksUseGeneratedMetadata()
    {
        Assert.True(GeneratedComponentMetadata.TryGet(typeof(GeneratedInjectionProbe), out var metadata));
        var component = new GeneratedInjectionProbe();
        var service = new GeneratedInjectionService();
        using var services = new ServiceCollection().AddSingleton(service).BuildServiceProvider();
        metadata.Inject(component, services, false);
        Assert.Same(service, component.Required);
        Assert.Null(component.Optional);
        Assert.True(metadata.FixedUpdate);
        Assert.False(metadata.Update);
        Assert.False(metadata.LateUpdate);
        Assert.False(metadata.AllowsMultiple);
        Assert.Equal([typeof(CameraComponent)], metadata.RequiredComponents);

        using var empty = new ServiceCollection().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => metadata.Inject(component, empty, false));
        metadata.Inject(component, empty, true);
        Assert.Null(component.Required);
    }

    /// <summary>Closed constructors create the requested runtime metadata type.</summary>
    [Fact]
    public void FactoryCreatesRegisteredObject()
    {
        GeneratedObjectFactory.Register<DataAssetAsset>();
        Assert.IsType<DataAssetAsset>(GeneratedObjectFactory.Create(typeof(DataAssetAsset)));
    }

    /// <summary>Generated catalog contracts preserve every field used to resolve shipped assets.</summary>
    [Fact]
    public void CatalogContractPreservesRuntimeResolutionMetadata()
    {
        var record = new AssetRecord
        {
            AssetId = Guid.NewGuid(),
            DataAssetPayloadTypeId = Guid.NewGuid(),
            AssetTypeName = typeof(DataAssetAsset).FullName!,
            SourceRelativePath = "Assets/Score.dataasset",
            ImportedRelativePath = "Content/assets.oap",
            StorageKind = AssetStorageKind.Oap,
            PrimaryContentKey = "score:primary",
            Labels = ["gameplay"],
            Artifacts = ["primary"],
            TargetArtifacts = new() { ["pc"] = "primary.pc.bin" },
            ComponentTypes = [typeof(GeneratedInjectionProbe).FullName!]
        };
        var catalog = new AssetCatalog { Records = [record] };
        var json = JsonSerializer.Serialize(catalog, AssetCatalogJsonContext.Default.AssetCatalog);
        var loaded = JsonSerializer.Deserialize(json, AssetCatalogJsonContext.Default.AssetCatalog)!;
        Assert.Equal(Serializer.Serialize(catalog), Serializer.Serialize(loaded));
        Assert.Equal(record.DataAssetPayloadTypeId, Assert.Single(loaded.Records).DataAssetPayloadTypeId);
    }
}

/// <summary>A service resolved by generated private-property injection.</summary>
public sealed class GeneratedInjectionService;

/// <summary>A node with private injection and a frame callback.</summary>
public partial class GeneratedInjectionNode : Node
{
    /// <summary>The service injected by the generated base registration.</summary>
    [InjectService, JsonIgnore]
    public GeneratedInjectionService? Required { get; private set; }

    /// <inheritdoc />
    public override void OnUpdate(float deltaTime) { }
}

/// <summary>A derived node that inherits injection and frame callbacks.</summary>
public sealed class GeneratedInjectionChildNode : GeneratedInjectionNode;

/// <summary>A value whose contract is provided by the exported context.</summary>
public sealed record GeneratedRuntimeValue(int Count);

/// <summary>A generated value context for export-option composition.</summary>
[JsonSerializable(typeof(GeneratedRuntimeValue))]
sealed partial class GeneratedRuntimeTestJsonContext : JsonSerializerContext;

/// <summary>A component whose metadata and private setters are emitted inside its partial declaration.</summary>
[DisallowMultipleComponent, RequireComponent(typeof(CameraComponent))]
public sealed partial class GeneratedInjectionProbe : Component
{
    /// <summary>The required test service.</summary>
    [InjectService, JsonIgnore]
    public GeneratedInjectionService? Required { get; private set; }

    /// <summary>An optional service omitted from the test container.</summary>
    [InjectService(Optional = true), JsonIgnore]
    public IInputSource? Optional { get; private set; }

    /// <inheritdoc />
    public override void OnFixedUpdate(float deltaTime) { }
}
