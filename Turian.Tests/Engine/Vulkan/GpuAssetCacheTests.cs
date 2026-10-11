namespace Turian.Tests;

/// <summary>Checks the backend's per-device asset caches and its release paths. Skipped without a Vulkan device.</summary>
[Collection(SerialTests.Name)]
public sealed class GpuAssetCacheTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>, IDisposable
{
    readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("turian-gpu-cache-");

    /// <inheritdoc/>
    public void Dispose() => directory.Delete(recursive: true);

    /// <summary>A material resource is shared until its entry is invalidated or the cache is cleared.</summary>
    [Fact]
    public void MaterialResourceRebuildsAfterInvalidationAndClear()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var database = new AssetDatabase();
        var material = new MaterialAsset { Id = Guid.NewGuid(), BaseColorFactor = new Vector4(1, 0, 0, 1) };
        material.RelativePath = Path.Combine(directory.CreateSubdirectory("Assets").FullName, "Red.material");
        var path = Path.Combine(directory.FullName, "Red.material.json");
        File.WriteAllText(path, Serializer.Serialize(material));
        Assert.True(database.RegisterAsset(material, path));
        using var context = new MaterialDescriptorContext(fixture.Vulkan, database);
        try
        {
            var first = material.GetContent(context);
            Assert.NotNull(first);
            Assert.Same(first, material.GetContent(context));

            MaterialAsset.InvalidateCacheEntry(Guid.NewGuid());
            Assert.Same(first, material.GetContent(context));

            MaterialAsset.InvalidateCacheEntry(material.Id);
            var rebuilt = material.GetContent(context);
            Assert.NotSame(first, rebuilt);

            MaterialAsset.ClearCache();
            Assert.NotSame(rebuilt, material.GetContent(context));
        }
        finally
        {
            MaterialAsset.InvalidateCacheEntry(material.Id);
        }
    }

    /// <summary>Clearing the texture cache releases every upload and resets the device-memory totals.</summary>
    [Fact]
    public void TextureCacheClearReleasesEveryUpload()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var database = new AssetDatabase();
        var path = Path.Combine(directory.CreateSubdirectory("Assets").FullName, "texture.amtex");
        TextureBlobWriter.Save(path, new TextureBlobContent(Format.R8G8B8A8Unorm, 2, 2, false, [new byte[16]]));
        var texture = new TextureAsset { Id = Guid.NewGuid(), RelativePath = path };
        Assert.True(database.RegisterAsset(texture, path));
        MaterialAsset.ClearCache();
        var before = TextureAsset.UploadedCount;

        Assert.NotNull(texture.GetContent(fixture.Vulkan, database));
        Assert.Equal(before + 1, TextureAsset.UploadedCount);
        Assert.True(TextureAsset.UploadedBytes >= 16);

        TextureAsset.ClearCache();
        Assert.Equal(0, TextureAsset.UploadedCount);
        Assert.Equal(0ul, TextureAsset.UploadedBytes);
    }

    /// <summary>
    /// On a device with mesh shading, a mesh component is drawn without being awakened against the device, and a
    /// camera that excludes its rendering layer leaves the frame empty.
    /// </summary>
    [Fact]
    public void MeshComponentDrawsThroughTheMeshRenderSystem()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        Assert.SkipUnless(fixture.Vulkan.Device.SupportsMeshShaders, "no mesh shading on this device");

        var empty = CenterColor(new Node());
        var drawn = CenterColor(MeshScene(renderLayer: 0));
        var excluded = CenterColor(MeshScene(renderLayer: 31));

        Assert.NotEqual(empty, drawn);
        Assert.Equal(empty, excluded);
    }

    /// <summary>Mesh shading is enabled exactly when the device lists its extension.</summary>
    [Fact]
    public void MeshShadingFollowsTheEnabledExtension()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        Assert.Equal(fixture.Vulkan.Device.SupportsMeshShaders,
            fixture.Vulkan.Device.DeviceExtensions.Contains("VK_EXT_mesh_shader"));
    }

    // The default editor camera sits at the origin looking down +Z; the scaled triangle covers the view's center.
    static Node MeshScene(int renderLayer)
    {
        var root = new Node();
        var node = new Node { Position = new Vector3(0, 0, 4), Scale = new Vector3(8), RenderLayer = renderLayer };
        node.AddComponent(new MeshComponent());
        node.Parent = root;
        root.Children.Add(node);
        return root;
    }

    (byte R, byte G, byte B) CenterColor(Node root)
    {
        const int size = 32;
        const int center = (((size / 2) * size) + (size / 2)) * 4;
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        viewer.Camera.CullingMask = LayerMask.FromLayer(0);
        viewer.Render(root, 0.016);
        var pixels = new byte[size * size * 4];
        viewer.CopyPixels(pixels);
        return (pixels[center], pixels[center + 1], pixels[center + 2]);
    }
}
