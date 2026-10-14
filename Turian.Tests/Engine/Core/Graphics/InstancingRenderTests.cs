namespace Turian.Tests;

/// <summary>
/// Integration coverage for automatic batching and <see cref="InstancedModelComponent"/> through
/// <see cref="SceneViewerService"/>. Skipped without a Vulkan device.
/// </summary>
/// <param name="fixture">The headless Vulkan device shared by the class.</param>
[Collection(SerialTests.Name)]
public sealed class InstancingRenderTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    const int size = 32;
    const int center = (((size / 2) * size) + (size / 2)) * 4;

    sealed class FixedDevice(Model model) : IRenderDevice
    {
        public Model GetModel(Guid modelAssetId, AssetDatabase assets) => model;
    }

    VulkanModel NewQuad() => new(fixture.Vulkan, new ModelBuilder
    {
        Vertices = [new(new(-0.5f, -0.5f, 0f), Vector3.One) { Normal = -Vector3.UnitZ },
            new(new(0.5f, -0.5f, 0f), Vector3.One) { Normal = -Vector3.UnitZ },
            new(new(0.5f, 0.5f, 0f), Vector3.One) { Normal = -Vector3.UnitZ },
            new(new(-0.5f, 0.5f, 0f), Vector3.One) { Normal = -Vector3.UnitZ }],
        Indices = [0, 1, 2, 0, 2, 3],
    });

    static void AddLight(Node root)
    {
        var light = new Node { Position = new Vector3(0, 0, 2), Parent = root };
        light.AddComponent(LightComponent.CreatePointLight(20f, Vector4.One));
        root.Children.Add(light);
    }

    static byte[] Render(SceneViewerService viewer, Node root)
    {
        viewer.Render(root, 0.016);
        var pixels = new byte[viewer.Width * viewer.Height * 4];
        viewer.CopyPixels(pixels);
        return pixels;
    }

    /// <summary>Ten thousand identical props draw as a single instanced draw.</summary>
    [Fact]
    public void TenThousandIdenticalPropsDrawInOneCall()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var quad = PreviewQuadMesh.Get(fixture.Vulkan);
        var root = new Node();
        for (var i = 0; i < 10_000; i++)
        {
            var node = new Node { Position = new Vector3((i % 100) - 50, (i / 100) - 50, 60), Parent = root };
            node.AddComponent(new ModelComponent { ModelOverride = quad });
            root.Children.Add(node);
        }

        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size)
        {
            CollectStatistics = true,
            UseFrustumCulling = false,
        };
        viewer.Render(root, 0.016);

        Assert.Equal(1, viewer.FrameStats.DrawCalls);
        Assert.Equal(10_000, viewer.FrameStats.Instances);
        Assert.Equal(20_000, viewer.FrameStats.Triangles);
        Assert.Equal(new RenderCullingStats(10_000, 0), viewer.FrameStats.Submeshes);
    }

    /// <summary>Batched props render the same pixels as the same props drawn one by one, per-instance color included.</summary>
    [Fact]
    public void BatchedImageMatchesUnbatchedImage()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        Color[] colors = [new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f), Color.White];
        using var shared = NewQuad();
        var separate = colors.Select(_ => NewQuad()).ToArray();
        try
        {
            var batched = Scene(_ => shared);
            var unbatched = Scene(i => separate[i]);
            using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size)
            {
                CollectStatistics = true,
            };

            var expected = Render(viewer, unbatched);
            Assert.Equal(colors.Length, viewer.FrameStats.DrawCalls);
            var actual = Render(viewer, batched);
            Assert.Equal(1, viewer.FrameStats.DrawCalls);
            Assert.Equal(colors.Length, viewer.FrameStats.Instances);

            Assert.Equal(expected, actual);
        }
        finally
        {
            foreach (var model in separate)
            {
                model.Dispose();
            }
        }

        Node Scene(Func<int, Model> model)
        {
            var root = new Node();
            for (var i = 0; i < colors.Length; i++)
            {
                var node = new Node
                {
                    Position = new Vector3((i % 2) - 0.5f, (i / 2) - 0.5f, 4),
                    Parent = root,
                };
                node.AddComponent(new ModelComponent { ModelOverride = model(i), Color = colors[i] });
                root.Children.Add(node);
            }

            AddLight(root);
            return root;
        }
    }

    /// <summary>
    /// An instanced component draws once per submesh, shows each instance's color, and a single edited instance is
    /// visible on the very next frame.
    /// </summary>
    [Fact]
    public void InstancedComponentDrawsColorsAndUploadsEdits()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var quad = NewQuad();
        using var services = new ServiceCollection()
            .AddSingleton(new AssetDatabase())
            .AddSingleton<IRenderDevice>(new FixedDevice(quad))
            .BuildServiceProvider();
        var root = new Node();
        var node = new Node { Position = new Vector3(0, 0, 4), Parent = root };
        root.Children.Add(node);
        var component = new InstancedModelComponent
        {
            Model = new AssetReference<ModelAsset>(Guid.NewGuid()),
            Instances =
            [
                new MeshInstance(new Transform { Scale = new Vector3(8) }, new Color(1f, 0f, 0f), Vector4.Zero),
                new MeshInstance(new Transform { Position = new Vector3(100, 0, 0) }),
                new MeshInstance(new Transform { Position = new Vector3(-100, 0, 0) }),
            ],
        };
        node.AddComponent(component);
        AddLight(root);
        root.Awake(null, services);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size)
        {
            CollectStatistics = true,
        };

        var red = Render(viewer, root);
        Assert.Equal(1, viewer.FrameStats.DrawCalls);
        Assert.Equal(3, viewer.FrameStats.Instances);
        Assert.True(red[center + 2] > red[center + 1] + 40, $"expected red, got BGR {Bgr(red)}");

        component.SetInstance(0, component[0] with { Color = new Color(0f, 1f, 0f) });
        var green = Render(viewer, root);
        Assert.True(green[center + 1] > green[center + 2] + 40, $"expected green, got BGR {Bgr(green)}");

        component.SetInstance(0, component[0] with { Color = new Color(1f, 0f, 0f) });
        Assert.Equal(red, Render(viewer, root));

        node.Position = new Vector3(1000, 0, 4);
        Render(viewer, root);
        Assert.Equal(0, viewer.FrameStats.DrawCalls);
        Assert.Equal(new RenderCullingStats(0, 3), viewer.CullingStats);
    }

    /// <summary>Growing an instanced component past its buffer and removing instances keep drawing every instance.</summary>
    [Fact]
    public void InstancedComponentGrowsAndShrinks()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var quad = NewQuad();
        using var services = new ServiceCollection()
            .AddSingleton(new AssetDatabase())
            .AddSingleton<IRenderDevice>(new FixedDevice(quad))
            .BuildServiceProvider();
        var root = new Node();
        var node = new Node { Position = new Vector3(0, 0, 50), Parent = root };
        root.Children.Add(node);
        var component = new InstancedModelComponent { Model = new AssetReference<ModelAsset>(Guid.NewGuid()) };
        node.AddComponent(component);
        root.Awake(null, services);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size)
        {
            CollectStatistics = true,
        };

        component.AddInstance(new MeshInstance(Transform.Identity));
        viewer.Render(root, 0.016);
        Assert.Equal(1, viewer.FrameStats.Instances);
        for (var i = 0; i < 200; i++)
        {
            component.AddInstance(new MeshInstance(new Transform { Position = new(i, 0, 0) }));
        }

        viewer.Render(root, 0.016);
        Assert.Equal(201, viewer.FrameStats.Instances);
        component.RemoveAt(0);
        component.RemoveAt(0);
        viewer.Render(root, 0.016);
        Assert.Equal(199, viewer.FrameStats.Instances);
        component.Clear();
        viewer.Render(root, 0.016);
        Assert.Equal(0, viewer.FrameStats.DrawCalls);
    }

    /// <summary>A batch released after leaving the scene uploads again in full when its component returns.</summary>
    [Fact]
    public void InstancedComponentReturningAfterReleaseDrawsAgain()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var quad = NewQuad();
        using var services = new ServiceCollection()
            .AddSingleton(new AssetDatabase())
            .AddSingleton<IRenderDevice>(new FixedDevice(quad))
            .BuildServiceProvider();
        var root = new Node();
        var node = new Node { Position = new Vector3(0, 0, 4), Parent = root };
        root.Children.Add(node);
        var component = new InstancedModelComponent
        {
            Model = new AssetReference<ModelAsset>(Guid.NewGuid()),
            Instances = [new MeshInstance(new Transform { Scale = new Vector3(8) }, new Color(1f, 0f, 0f), default)],
        };
        node.AddComponent(component);
        AddLight(root);
        root.Awake(null, services);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size)
        {
            CollectStatistics = true,
        };
        var first = Render(viewer, root);

        node.IsActive = false;
        for (var i = 0; i < 4; i++)
        {
            viewer.Render(root, 0.016);
        }

        Assert.Equal(0, viewer.FrameStats.Instances);
        node.IsActive = true;

        Assert.Equal(first, Render(viewer, root));
        Assert.Equal(1, viewer.FrameStats.Instances);
    }

    static string Bgr(byte[] pixels) => $"{pixels[center]},{pixels[center + 1]},{pixels[center + 2]}";
}
