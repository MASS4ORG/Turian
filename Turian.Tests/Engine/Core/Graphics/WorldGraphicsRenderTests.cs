namespace Turian.Tests;

/// <summary>Exercises the shipped line/trail/text path through real offscreen Vulkan frames.</summary>
[Collection(SerialTests.Name)]
public sealed class WorldGraphicsRenderTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    const int size = 192;

    static byte[] Pixels(SceneViewerService viewer, Node root)
    {
        viewer.Render(root, 1f / 60);
        var pixels = new byte[viewer.Width * viewer.Height * 4];
        viewer.CopyPixels(pixels);
        return pixels;
    }

    static int Colored(byte[] pixels, int channel) => Enumerable.Range(0, pixels.Length / 4)
        .Count(index => pixels[index * 4 + channel] > 100 &&
                        pixels[index * 4 + channel] > pixels[index * 4 + (channel + 1) % 3] * 1.5f &&
                        pixels[index * 4 + channel] > pixels[index * 4 + (channel + 2) % 3] * 1.5f);

    static Node Child(Node root)
    {
        var child = new Node { Parent = root };
        root.Children.Add(child);
        return child;
    }

    /// <summary>Gradient ribbons draw without lighting, blend alpha and disappear under component and parent controls.</summary>
    [Fact]
    public void LineGradientAlphaAndVisibilityReachPixels()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        viewer.ClearColor = Vector4.Zero;
        var root = new Node();
        var baseline = Pixels(viewer, root);
        var node = Child(Child(root));
        var line = new LineRenderer
        {
            Positions = [new(-1, 0, 4), new(1, 0, 4)],
            WidthCurve = new() { Keys = [new(0, 0.4f)] },
            ColorGradient = new() { Keys = [new(0, new Color(1, 0, 0)), new(1, new Color(0, 0, 1))] },
        };
        node.AddComponent(line);
        root.Awake(null);
        var image = Pixels(viewer, root);
        Assert.True(Colored(image, 2) > 50);
        Assert.True(Colored(image, 0) > 50);
        Assert.Equal(image, Pixels(viewer, root));
        line.ColorGradient = new() { Keys = [new(0, new Color(1, 0, 0, 0.5f))] };
        var halfAlpha = Pixels(viewer, root);
        var brightest = Enumerable.Range(0, halfAlpha.Length / 4).Max(index => halfAlpha[index * 4 + 2]);
        var expected = new Color(0.5f, 0, 0).ToSrgb().R;
        Assert.InRange(brightest, expected - 2, expected + 2);
        line.ColorGradient = new() { Keys = [new(0, new Color(1, 0, 0, 0))] };
        Assert.Equal(baseline, Pixels(viewer, root));
        line.IsActive = false;
        Assert.Equal(baseline, Pixels(viewer, root));
        line.IsActive = true;
        line.ColorGradient = new();
        node.IsActive = false;
        Assert.Equal(baseline, Pixels(viewer, root));
        node.IsActive = true;
        node.Parent!.IsActive = false;
        Assert.Equal(baseline, Pixels(viewer, root));
        node.Parent.IsActive = true;
        viewer.Camera.CullingMask = LayerMask.Nothing;
        Assert.Equal(baseline, Pixels(viewer, root));
        viewer.Camera.CullingMask = LayerMask.Everything;
        line.Positions = [];
        Assert.Equal(baseline, Pixels(viewer, root));
        line.DestroyImmediate();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(baseline, Pixels(viewer, root));
        }
    }

    /// <summary>Changing text and fonts repeatedly does not exhaust descriptors, and billboards draw from both views.</summary>
    [Fact]
    public void TextUpdatesBillboardsAndResizeRemainVisible()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        viewer.ClearColor = Vector4.Zero;
        var root = new Node();
        var node = Child(root);
        node.Position = new(0, 0, 4);
        var text = new WorldTextComponent { Text = "LABEL\n+12", FontSize = 0.7f, Color = new Color(0, 1, 0) };
        node.AddComponent(text);
        root.Awake(null);
        var first = Pixels(viewer, root);
        Assert.True(Colored(first, 1) > 100);
        Assert.Equal(first, Pixels(viewer, root));
        for (var i = 0; i < 40; i++)
        {
            text.Text = $"+{i}";
            text.Font = i % 2 == 0 ? "serif" : "sans-serif";
            Assert.True(Colored(Pixels(viewer, root), 1) > 20);
        }
        viewer.Camera.Position = new(4, 0, 4);
        viewer.Camera.LookIn(-Vector3.UnitX, Vector3.UnitY);
        Assert.True(Colored(Pixels(viewer, root), 1) > 20);
        text.Billboard = false;
        Assert.True(Colored(Pixels(viewer, root), 1) < 10);
        text.Billboard = true;
        viewer.Resize(128, 128);
        Assert.True(Colored(Pixels(viewer, root), 1) > 10);
        text.Text = string.Empty;
        var empty = Pixels(viewer, new Node());
        Assert.Equal(empty, Pixels(viewer, root));
        node.DestroyImmediate();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(empty, Pixels(viewer, root));
        }
    }

    /// <summary>Trail geometry renders through the same path and disappears once its simulated lifetime elapses.</summary>
    [Fact]
    public void TrailFadesAndExpiresInTheRenderer()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        viewer.ClearColor = Vector4.Zero;
        var root = new Node();
        var node = Child(root);
        var trail = new TrailRenderer
        {
            Time = 1,
            WidthCurve = new() { Keys = [new(0, 0.4f)] },
            ColorGradient = new() { Keys = [new(0, new Color(1, 0, 0)), new(1, new Color(1, 0, 0, 0))] },
        };
        node.AddComponent(trail);
        root.Awake(null);
        node.Position = new(-1, 0, 4);
        trail.OnLateUpdate(0);
        node.Position = new(1, 0, 4);
        trail.OnLateUpdate(0.2f);
        Assert.True(Colored(Pixels(viewer, root), 2) > 100);
        trail.Emitting = false;
        trail.OnLateUpdate(1);
        Assert.Equal(Pixels(viewer, new Node()), Pixels(viewer, root));
    }

    /// <summary>Solid scene geometry hides both ribbons and text until it moves behind them.</summary>
    [Fact]
    public void SolidsOccludeWorldGraphics()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), size, size);
        viewer.ClearColor = Vector4.Zero;
        var root = new Node();
        Child(root).AddComponent(new LineRenderer { Positions = [new(-1, 0, 4), new(1, 0, 4)] });
        var label = Child(root);
        label.Position = new(0, 1, 4);
        label.AddComponent(new WorldTextComponent { Text = "Occlusion", Color = new Color(0, 1, 0) });
        root.Awake(null);
        Assert.True(Colored(Pixels(viewer, root), 1) > 20);
        var wall = Child(root);
        wall.Position = new(0, 0, 3);
        wall.Scale = new(20);
        wall.AddComponent(new ModelComponent { ModelOverride = PreviewQuadMesh.Get(fixture.Vulkan) });
        var hidden = Pixels(viewer, root);
        Assert.Equal(0, Colored(hidden, 1));
        wall.Position = new(0, 0, 5);
        Assert.True(Colored(Pixels(viewer, root), 1) > 20);
    }

    /// <summary>Rasterization produces real interior and exterior distances and loads project font bytes by id and path.</summary>
    [Fact]
    public void SkiaFontMasksAndProjectFontsProduceDistanceFields()
    {
        var assets = new AssetDatabase();
        var layout = SdfTextRasterizer.Rasterize("Hg\n+12", "sans-serif", assets);
        Assert.True(layout.Width > 20 && layout.Height > 80);
        var alpha = layout.Rgba.Where((_, index) => index % 4 == 3).ToArray();
        Assert.Contains(alpha, static value => value > 128);
        Assert.Contains(alpha, static value => value == 0);
        Assert.Contains(alpha, static value => value is > 0 and < 128);
        var directory = Directory.CreateTempSubdirectory("turian-sdf-font-");
        directory.CreateSubdirectory("Assets");
        try
        {
            using var face = SKTypeface.FromFamilyName("sans-serif");
            using var stream = face.OpenStream();
            var bytes = new byte[stream.Length];
            Assert.Equal(bytes.Length, stream.Read(bytes, bytes.Length));
            var path = Path.Combine(directory.FullName, "font.ttf");
            File.WriteAllBytes(path, bytes);
            var asset = new Asset { Id = Guid.NewGuid(), RelativePath = Path.Combine(directory.FullName, "Assets/font.ttf") };
            Assert.True(assets.RegisterAsset(asset, path));
            var loaded = SdfTextRasterizer.Rasterize("Hg\n+12", $"asset://{asset.Id}", assets);
            Assert.Equal(layout.Rgba, loaded.Rgba);
            Assert.Equal(layout.Rgba, SdfTextRasterizer.Rasterize("Hg\n+12", "Assets/font.ttf", assets).Rgba);
            Assert.Throws<FileNotFoundException>(() => SdfTextRasterizer.Rasterize("test", $"asset://{Guid.NewGuid()}", assets));
            Assert.Throws<ArgumentException>(() => SdfTextRasterizer.Rasterize(new string('M', 1000), "sans-serif", assets));
        }
        finally { directory.Delete(true); }
    }

    /// <summary>A project texture modulates a solid ribbon and switching the reference refreshes its binding.</summary>
    [Fact]
    public void LineTexturesUseAssetContent()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var directory = Directory.CreateTempSubdirectory("turian-line-texture-");
        directory.CreateSubdirectory("Assets");
        var assets = new AssetDatabase();
        var texture = new TextureAsset { Id = Guid.NewGuid(), RelativePath = Path.Combine(directory.FullName, "Assets/green.png") };
        try
        {
            var path = Path.Combine(directory.FullName, "green.png");
            using (var bitmap = new SKBitmap(16, 8))
            {
                bitmap.Erase(SKColors.Lime);
                using (var canvas = new SKCanvas(bitmap))
                using (var paint = new SKPaint { Color = SKColors.Blue })
                {
                    canvas.DrawRect(new SKRect(8, 0, 16, 8), paint);
                }
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(path);
                data.SaveTo(file);
            }
            Assert.True(assets.RegisterAsset(texture, path));
            using var viewer = new SceneViewerService(fixture.Vulkan, assets, size, size);
            viewer.ClearColor = Vector4.Zero;
            var root = new Node();
            var line = new LineRenderer { Positions = [new(-1, 0, 4), new(1, 0, 4)], Texture = new(texture.Id) };
            Child(root).AddComponent(line);
            root.Awake(null);
            var stretched = Pixels(viewer, root);
            Assert.True(Colored(stretched, 1) > 100);
            Assert.True(Colored(stretched, 0) > 100);
            line.TextureMode = LineTextureMode.Tile;
            var tiled = Pixels(viewer, root);
            Assert.NotEqual(stretched, tiled);
            Assert.True(Colored(tiled, 1) > 100);
            line.Texture = new(Guid.NewGuid());
            Assert.Equal(0, Colored(Pixels(viewer, root), 1));
        }
        finally
        {
            TextureAsset.InvalidateCacheEntry(texture.Id);
            directory.Delete(true);
        }
    }
}
