namespace Turian.Tests;

/// <summary>Tests <see cref="InstancedModelComponent"/>'s change tracking, serialization, bounds and picking.</summary>
public sealed class InstancedModelComponentTests
{
    sealed class CpuModel(ModelBuilder builder) : Model(builder)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }

    sealed class FixedDevice(Model model) : IRenderDevice
    {
        public Model? GetModel(Guid modelAssetId, AssetDatabase assets) => model;
    }

    static MeshInstance At(float x, float z = 10f) => new(new Transform { Position = new Vector3(x, 0f, z) });

    /// <summary>A unit cube centered on the origin.</summary>
    static CpuModel Cube() => new(new ModelBuilder
    {
        Vertices = [new(new Vector3(-0.5f), Vector3.One), new(new Vector3(0.5f), Vector3.One)],
        Indices = [0, 1],
    });

    static (Node Root, Node Node, InstancedModelComponent Component, ServiceProvider Services) Scene(
        params MeshInstance[] instances)
    {
        var services = new ServiceCollection()
            .AddSingleton(new AssetDatabase())
            .AddSingleton<IRenderDevice>(new FixedDevice(Cube()))
            .BuildServiceProvider();
        var root = new Node { Name = "root" };
        var node = new Node { Name = "instanced", Parent = root };
        root.Children.Add(node);
        var component = new InstancedModelComponent
        {
            Model = new AssetReference<ModelAsset>(Guid.NewGuid()),
            Instances = instances,
        };
        node.AddComponent(component);
        root.Awake(null, services);
        return (root, node, component, services);
    }

    /// <summary>Edits widen one changed range until a consumer acknowledges it; untouched versions report nothing.</summary>
    [Fact]
    public void ChangesTrackTheEditedRangeUntilAcknowledged()
    {
        var component = new InstancedModelComponent { Instances = [At(0), At(1), At(2), At(3), At(4)] };

        Assert.True(component.TryGetChanges(-1, out var start, out var end));
        Assert.Equal((0, 5), (start, end));
        component.AcknowledgeChanges();
        var synced = component.Version;
        Assert.False(component.TryGetChanges(synced, out _, out _));

        component.SetInstance(3, At(30));
        component.SetInstance(1, At(10));
        Assert.True(component.TryGetChanges(synced, out start, out end));
        Assert.Equal((1, 4), (start, end));
        Assert.Equal(new Vector3(10f, 0f, 10f), component[1].Transform.Position);
        component.AcknowledgeChanges();
        synced = component.Version;

        Assert.Equal(5, component.AddInstance(At(5)));
        Assert.True(component.TryGetChanges(synced, out start, out end));
        Assert.Equal((5, 6), (start, end));
        component.AcknowledgeChanges();
        synced = component.Version;

        component.RemoveAt(2);
        Assert.Equal(5, component.Count);
        Assert.True(component.TryGetChanges(synced, out start, out end));
        Assert.Equal((2, 5), (start, end));
    }

    /// <summary>A consumer behind the last acknowledgement refreshes everything, and clearing leaves an empty range.</summary>
    [Fact]
    public void StaleConsumersRefreshEverything()
    {
        var component = new InstancedModelComponent { Instances = [At(0), At(1), At(2)] };
        var stale = component.Version;
        component.AcknowledgeChanges();
        component.SetInstance(1, At(5));
        component.AcknowledgeChanges();

        Assert.True(component.TryGetChanges(stale, out var start, out var end));
        Assert.Equal((0, 3), (start, end));

        component.Clear();
        Assert.True(component.TryGetChanges(component.Version - 1, out start, out end));
        Assert.Equal((0, 0), (start, end));
        Assert.Equal(0, component.Count);
    }

    /// <summary>References and every instance field survive a scene save and load.</summary>
    [Fact]
    public void SerializationRoundTripsInstances()
    {
        var node = new Node { Name = "batch" };
        var modelId = Guid.NewGuid();
        var materialId = Guid.NewGuid();
        node.AddComponent(new InstancedModelComponent
        {
            Model = new AssetReference<ModelAsset>(modelId),
            Material = new AssetReference<MaterialAsset>(materialId),
            Instances =
            [
                new MeshInstance(new Transform { Position = new Vector3(1, 2, 3), Scale = new Vector3(2) },
                    new Color(1f, 0f, 0f, 0.5f), new Vector4(1, 2, 3, 4)),
                At(7),
            ],
        });

        var loaded = Serializer.LoadData<Node>(Serializer.Serialize(node))!;
        var component = loaded.GetComponent<InstancedModelComponent>()!;

        Assert.Equal(modelId, component.Model!.AssetId);
        Assert.Equal(materialId, component.Material!.AssetId);
        Assert.Equal(2, component.Count);
        Assert.Equal(new Vector3(1, 2, 3), component[0].Transform.Position);
        Assert.Equal(new Vector3(2), component[0].Transform.Scale);
        Assert.Equal(new Color(1f, 0f, 0f, 0.5f), component[0].Color);
        Assert.Equal(new Vector4(1, 2, 3, 4), component[0].CustomData);
        Assert.Equal(Color.White, component[1].Color);
    }

    /// <summary>Aggregate bounds cover every instance under the node transform and follow instance edits.</summary>
    [Fact]
    public void BoundsCoverEveryInstanceAndFollowEdits()
    {
        var (_, node, component, services) = Scene(At(-3), At(3));
        using (services)
        {
            node.Position = new Vector3(0f, 1f, 0f);

            Assert.Equal(new Bounds(new Vector3(-3.5f, -0.5f, 9.5f), new Vector3(3.5f, 0.5f, 10.5f)),
                component.LocalBounds);
            Assert.Equal(new Bounds(new Vector3(-3.5f, 0.5f, 9.5f), new Vector3(3.5f, 1.5f, 10.5f)),
                component.WorldBounds);
            Assert.Equal(new Bounds(new Vector3(2.5f, 0.5f, 9.5f), new Vector3(3.5f, 1.5f, 10.5f)),
                component.GetInstanceWorldBounds(1));

            component.SetInstance(1, At(6));
            Assert.Equal(6.5f, component.LocalBounds.Max.X);

            var model = component.ModelInstance;
            component.Dispose();
            Assert.Same(model, component.ModelInstance);
        }
    }

    /// <summary>Without a render device nothing loads, so every bound is empty.</summary>
    [Fact]
    public void BoundsAreEmptyWithoutAModel()
    {
        var component = new InstancedModelComponent { Instances = [At(0)] };

        Assert.Null(component.ModelInstance);
        Assert.Equal(Guid.Empty, component.ModelAssetId);
        Assert.True(component.LocalBounds.IsEmpty);
        Assert.True(component.WorldBounds.IsEmpty);
        Assert.True(component.GetInstanceWorldBounds(0).IsEmpty);
    }

    /// <summary>A pick on any instance selects the owning node; a pick in the gap inside the aggregate box does not.</summary>
    [Fact]
    public void PickingHitsInstancesAndNotTheGapsBetweenThem()
    {
        var (root, node, _, services) = Scene(At(-3), At(3));
        using (services)
        {
            var candidates = ScenePicker.EnumerateWorldBounds(root).ToList();

            Assert.Equal(2, candidates.Count);
            Assert.Same(node, ScenePicker.PickClosest(candidates, new Ray(new Vector3(3f, 0f, 0f), Vector3.UnitZ)));
            Assert.Same(node, ScenePicker.PickClosest(candidates, new Ray(new Vector3(-3f, 0f, 0f), Vector3.UnitZ)));
            Assert.Null(ScenePicker.PickClosest(candidates, new Ray(Vector3.Zero, Vector3.UnitZ)));
            Assert.Null(ScenePicker.Pick(root, new EditorCamera(), new Vector2(480f, 270f), new Vector2(960f, 540f)));
        }
    }

    /// <summary>A marquee over several instances selects the owning node once, and one over the gap selects nothing.</summary>
    [Fact]
    public void MarqueeSelectsTheOwningNodeOnce()
    {
        var (root, node, _, services) = Scene(At(-3), At(3));
        using (services)
        {
            var camera = new EditorCamera();
            camera.Resize(960, 540);
            var viewport = new Vector2(960f, 540f);

            Assert.Equal([node], SceneMarquee.Pick(root, camera, Vector2.Zero, viewport, viewport));
            Assert.Empty(SceneMarquee.Pick(root, camera, new Vector2(470f, 260f), new Vector2(490f, 280f), viewport));
        }
    }
}
