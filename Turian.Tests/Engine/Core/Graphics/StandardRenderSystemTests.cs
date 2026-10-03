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
        using var viewer = new SceneViewerService(fixture.Vulkan, size, size);
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

    /// <summary>
    /// A model with an asset id and material slots draws with the default material when neither the override nor
    /// the imported material exists, on the first frame and from the cached handles on the next.
    /// </summary>
    [Fact]
    public void UnresolvedMaterialsFallBackToDefault()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        TestAssetDatabase.Reset();
        _ = new AssetDatabase();
        try
        {
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

                using var viewer = new SceneViewerService(fixture.Vulkan, size, size);
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
        finally
        {
            TestAssetDatabase.Reset();
        }
    }

    /// <summary>Consecutive frames through the sorted draw list render the same image.</summary>
    [Fact]
    public void RepeatedFramesRenderTheSameImage()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var quad = PreviewQuadMesh.Get(fixture.Vulkan);
        var root = QuadScene(quad, withLight: true);
        using var viewer = new SceneViewerService(fixture.Vulkan, size, size);
        var first = new byte[size * size * 4];
        var second = new byte[size * size * 4];

        viewer.Render(root, 0.016);
        viewer.CopyPixels(first);
        viewer.Render(root, 0.016);
        viewer.CopyPixels(second);

        Assert.Equal(first, second);
        Assert.True(first[center] + first[center + 1] + first[center + 2] > 0, "expected the quad to be drawn");
    }
}
