namespace Turian.Tests;

/// <summary>
/// Integration coverage for <see cref="StandardRenderSystem"/> through <see cref="SceneViewerService"/>: a
/// lit quad rendered offscreen. Skipped without a Vulkan device.
/// </summary>
/// <param name="fixture">The headless Vulkan device shared by the class.</param>
[Collection(SerialTests.Name)]
public sealed class StandardRenderSystemTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    const int size = 32;
    const int center = (((size / 2) * size) + (size / 2)) * 4;

    // The default editor camera sits at the origin looking down +Z; the -Z-facing quad fills the view ahead of it.
    static Node QuadScene(Model quad, bool withLight)
    {
        var root = new Node();
        var node = new Node { Position = new Vector3(0, 0, 4), Scale = new Vector3(8) };
        node.AddComponent(new ModelComponent { ModelOverride = quad });
        node.Parent = root;
        root.Children.Add(node);
        if (!withLight) return root;

        var light = new Node { Position = new Vector3(0, 0, 2) };
        light.AddComponent(LightComponent.CreatePointLight(20f, Vector4.One));
        light.Parent = root;
        root.Children.Add(light);
        return root;
    }

    int CenterBrightness(Node root)
    {
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        viewer.Render(root, 0.016);
        var pixels = new byte[size * size * 4];
        viewer.CopyPixels(pixels);
        return pixels[center] + pixels[center + 1] + pixels[center + 2];
    }

    /// <summary>A light lights the very first frame: it is gathered before the global UBO is uploaded.</summary>
    [Fact]
    public void FirstFrameIsLit()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var quad = PreviewQuadMesh.Get(fixture.Vulkan);

        var unlit = CenterBrightness(QuadScene(quad, withLight: false));
        var lit = CenterBrightness(QuadScene(quad, withLight: true));

        Assert.True(lit > unlit + 30, $"expected the first frame lit: unlit={unlit}, lit={lit}");
    }

    /// <summary>The camera mask excludes draws on the selected rendering layer and includes them when enabled.</summary>
    [Fact]
    public void CameraMaskExcludesRenderLayers()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var root = QuadScene(PreviewQuadMesh.Get(fixture.Vulkan), withLight: false);
        root.Children[0].RenderLayer = 31;
        root.Children[0].PhysicsLayer = 0;
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        viewer.Camera.CullingMask = LayerMask.FromLayer(0);
        viewer.Render(root, 0.016);
        Assert.Equal(0, viewer.CullingStats.Submitted);
        var camera = new CameraComponent { CullingMask = LayerMask.FromLayer(31) };
        new Node().AddComponent(camera);
        camera.Resize(size, size);
        viewer.Render(root, 0.016, camera);
        Assert.Equal(1, viewer.CullingStats.Submitted);
    }

    /// <summary>Point and directional lights illuminate only the rendering layers their masks include.</summary>
    [Theory]
    [InlineData(LightType.Point)]
    [InlineData(LightType.Directional)]
    public void LightMasksControlRenderedIllumination(LightType type)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var root = QuadScene(PreviewQuadMesh.Get(fixture.Vulkan), withLight: true);
        root.Children[0].RenderLayer = 31;
        var light = root.Children[1].GetComponent<LightComponent>()!;
        light.Type = type;
        light.CullingMask = LayerMask.FromLayer(0);
        var excluded = CenterBrightness(root);
        light.CullingMask = LayerMask.FromLayer(31);
        var included = CenterBrightness(root);
        Assert.True(included > excluded + 30, $"excluded={excluded}, included={included}");
    }

    /// <summary>
    /// A model with an asset id and material slots draws with the default material when neither the override nor
    /// the imported material exists, on the first frame and from the cached handles on the next.
    /// </summary>
    [Fact]
    public void UnresolvedMaterialsFallBackToDefault()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var quad = PreviewQuadMesh.Get(fixture.Vulkan);
        var slotted = new Model(fixture.Vulkan, new ModelBuilder
        {
            Vertices = [new(new(-0.5f, -0.5f, 0f), Vector3.One) { Normal = -Vector3.UnitZ },
                    new(new(0.5f, -0.5f, 0f), Vector3.One) { Normal = -Vector3.UnitZ },
                    new(new(0.5f, 0.5f, 0f), Vector3.One) { Normal = -Vector3.UnitZ },
                    new(new(-0.5f, 0.5f, 0f), Vector3.One) { Normal = -Vector3.UnitZ }],
            Indices = [0, 1, 2, 0, 2, 3],
            SubMeshes = [new SubMesh(0, 6, MaterialIndex: 0)],
        });
        using (slotted)
        {
            var root = QuadScene(quad, withLight: true);
            var component = root.Children[0].GetComponent<ModelComponent>()!;
            component.ModelOverride = slotted;
            component.Model = new AssetReference<ModelAsset>(Guid.NewGuid());
            component.Materials = [new AssetReference<MaterialAsset>(Guid.NewGuid())];
            var expected = CenterBrightness(QuadScene(quad, withLight: true));

            using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
            var first = new byte[size * size * 4];
            var second = new byte[size * size * 4];
            viewer.Render(root, 0.016);
            viewer.CopyPixels(first);
            viewer.Render(root, 0.016);
            viewer.CopyPixels(second);

            Assert.Equal(expected, first[center] + first[center + 1] + first[center + 2]);
            Assert.Equal(first, second);
        }
    }

    /// <summary>Consecutive frames through the sorted draw list render the same image.</summary>
    [Fact]
    public void RepeatedFramesRenderTheSameImage()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var quad = PreviewQuadMesh.Get(fixture.Vulkan);
        var root = QuadScene(quad, withLight: true);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        var first = new byte[size * size * 4];
        var second = new byte[size * size * 4];

        viewer.Render(root, 0.016);
        viewer.CopyPixels(first);
        viewer.Render(root, 0.016);
        viewer.CopyPixels(second);

        Assert.Equal(first, second);
        Assert.True(first[center] + first[center + 1] + first[center + 2] > 0, "expected the quad to be drawn");
    }

    /// <summary>Offscreen draws are removed without changing pixels, and moving a parent refreshes cached bounds.</summary>
    [Fact]
    public void FrustumCullingPreservesPixelsAndTracksParentChanges()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var quad = PreviewQuadMesh.Get(fixture.Vulkan);
        var root = QuadScene(quad, withLight: true);
        var parent = new Node { Position = new Vector3(1000f, 0f, 0f), Parent = root };
        root.Children.Add(parent);
        var child = new Node { Position = new Vector3(0f, 0f, 4f), Parent = parent };
        child.AddComponent(new ModelComponent { ModelOverride = quad });
        parent.Children.Add(child);

        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        var unculled = new byte[size * size * 4];
        var culled = new byte[size * size * 4];
        viewer.UseFrustumCulling = false;
        viewer.Render(root, 0.016);
        viewer.CopyPixels(unculled);
        Assert.Equal(new RenderCullingStats(2, 0), viewer.CullingStats);

        viewer.UseFrustumCulling = true;
        viewer.Render(root, 0.016);
        viewer.CopyPixels(culled);
        Assert.Equal(new RenderCullingStats(1, 1), viewer.CullingStats);
        Assert.Equal(unculled, culled);

        parent.Position = Vector3.Zero;
        viewer.Render(root, 0.016);
        Assert.Equal(new RenderCullingStats(2, 0), viewer.CullingStats);
        viewer.Camera.LookIn(-Vector3.UnitZ, Vector3.UnitY);
        viewer.Render(root, 0.016);
        Assert.Equal(new RenderCullingStats(0, 2), viewer.CullingStats);
    }

    /// <summary>Separate submeshes cull independently, and replacing a model rebuilds its cached bounds.</summary>
    [Fact]
    public void FrustumCullingTracksSubmeshesAndModelReplacement()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var model = new Model(fixture.Vulkan, new ModelBuilder
        {
            Vertices = [new(new(-1f, -1f, 0f), Vector3.One), new(new(1f, -1f, 0f), Vector3.One),
                new(new(0f, 1f, 0f), Vector3.One), new(new(999f, -1f, 0f), Vector3.One),
                new(new(1001f, -1f, 0f), Vector3.One), new(new(1000f, 1f, 0f), Vector3.One)],
            Indices = [0, 1, 2, 3, 4, 5],
            SubMeshes = [new SubMesh(0, 3), new SubMesh(3, 3)],
        });
        Assert.Equal(new Bounds(new Vector3(-1f, -1f, 0f), new Vector3(1001f, 1f, 0f)), model.Bounds);
        var root = QuadScene(model, withLight: false);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        viewer.Render(root, 0.016);
        Assert.Equal(new RenderCullingStats(1, 1), viewer.CullingStats);
        root.Children[0].GetComponent<ModelComponent>()!.ModelOverride = PreviewQuadMesh.Get(fixture.Vulkan);
        viewer.Render(root, 0.016);
        Assert.Equal(new RenderCullingStats(1, 0), viewer.CullingStats);
        viewer.Render(root, 0.016);
        Assert.Equal(new RenderCullingStats(1, 0), viewer.CullingStats);
    }

    /// <summary>A resized viewer retains its culling setting and treats negative scale conservatively.</summary>
    [Fact]
    public void ResizePreservesCullingSetting()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var root = QuadScene(PreviewQuadMesh.Get(fixture.Vulkan), withLight: false);
        root.Children[0].Scale = new Vector3(-8f, 3f, 2f);
        root.Children[0].Rotation = new Vector3(0f, 40f, 20f);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        viewer.Render(root, 0.016);
        Assert.Equal(new RenderCullingStats(1, 0), viewer.CullingStats);
        viewer.UseFrustumCulling = false;
        viewer.Resize(size * 2, size);
        Assert.False(viewer.UseFrustumCulling);
        viewer.Camera.Position = new Vector3(1000f);
        viewer.Render(root, 0.016);
        Assert.Equal(new RenderCullingStats(1, 0), viewer.CullingStats);
        viewer.UseFrustumCulling = true;
        viewer.Render(root, 0.016);
        Assert.Equal(new RenderCullingStats(0, 1), viewer.CullingStats);
    }

    /// <summary>A range cache holds only selected submeshes, reuses storage and refreshes on range or transform edits.</summary>
    [Fact]
    public void WorldBoundsCacheTracksSelectedRange()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var model = new Model(fixture.Vulkan, new ModelBuilder
        {
            Vertices = [new(new Vector3(-1f), Vector3.One), new(new Vector3(2f), Vector3.One)],
            Indices = [0, 1],
            SubMeshes = [new SubMesh(0, 1), new SubMesh(1, 1)],
        });
        var cached = new WorldBoundsCache();
        var transform = new Transform { Position = new Vector3(10f) };
        cached.Update(model, transform, 0, 1);
        var storage = cached.Bounds;
        Assert.Equal(new Bounds(new Vector3(9f), new Vector3(9f)), Assert.Single(storage));
        Assert.Equal(transform.Matrix4X4(), cached.ModelMatrix);
        Assert.Equal(transform.NormalMatrix(), cached.NormalMatrix);
        cached.Update(model, transform, 0, 1);
        Assert.Same(storage, cached.Bounds);

        cached.Update(model, transform, 1, 1);
        Assert.Same(storage, cached.Bounds);
        Assert.Equal(new Bounds(new Vector3(12f), new Vector3(12f)), Assert.Single(storage));
        cached.Update(model, Transform.Identity, 1, 1);
        Assert.Equal(new Bounds(new Vector3(2f), new Vector3(2f)), Assert.Single(storage));
        cached.Update(model, Transform.Identity, 0, 2);
        Assert.Equal(2, cached.Bounds.Length);
        Assert.Equal(new Bounds(new Vector3(-1f), new Vector3(-1f)), cached.Bounds[0]);
        cached.Update(model, Transform.Identity, 0, 0);
        Assert.Empty(cached.Bounds);
    }

    /// <summary>A mesh asset's selected submesh range controls culling independently of the rest of its model.</summary>
    [Fact]
    public void MeshReferenceCullsOnlyItsSelectedRange()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var model = new Model(fixture.Vulkan, new ModelBuilder
        {
            Vertices = [new(new(-1f, -1f, 0f), Vector3.One), new(new(1f, -1f, 0f), Vector3.One),
                new(new(0f, 1f, 0f), Vector3.One), new(new(999f, -1f, 0f), Vector3.One),
                new(new(1001f, -1f, 0f), Vector3.One), new(new(1000f, 1f, 0f), Vector3.One)],
            Indices = [0, 1, 2, 3, 4, 5],
            SubMeshes = [new SubMesh(0, 3), new SubMesh(3, 3)],
        });
        var directory = Directory.CreateTempSubdirectory("turian-culling-range-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "Assets"));
            var mesh = new MeshAsset
            {
                Id = Guid.NewGuid(),
                RelativePath = Path.Combine(directory.FullName, "Assets", "mesh.mesh"),
                Model = new AssetReference<ModelAsset>(Guid.NewGuid()),
                SubMeshStart = 1,
                SubMeshCount = 1,
            };
            var payload = Path.Combine(directory.FullName, "mesh.json");
            File.WriteAllText(payload, Serializer.Serialize(mesh));
            var database = new AssetDatabase();
            Assert.True(database.RegisterAsset(mesh, payload));
            using var services = new ServiceCollection().AddSingleton(database).BuildServiceProvider();
            var root = QuadScene(model, withLight: false);
            root.Children[0].GetComponent<ModelComponent>()!.Mesh = new AssetReference<MeshAsset>(mesh.Id);
            root.Awake(null, services);
            using var viewer = new SceneViewerService(fixture.Vulkan, database, size, size);
            viewer.Render(root, 0.016);
            Assert.Equal(new RenderCullingStats(0, 1), viewer.CullingStats);
            viewer.UseFrustumCulling = false;
            viewer.Render(root, 0.016);
            Assert.Equal(new RenderCullingStats(1, 0), viewer.CullingStats);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
