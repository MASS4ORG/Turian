namespace Turian.Tests;

/// <summary>Checks gizmo visibility and filled primitives through real offscreen Vulkan rendering.</summary>
[Collection(SerialTests.Name)]
public sealed class GizmoRenderingTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Transform handles remain visible when their pivot is behind opaque scene geometry.</summary>
    [Theory]
    [InlineData(TransformGizmoMode.Translate)]
    [InlineData(TransformGizmoMode.Rotate)]
    [InlineData(TransformGizmoMode.Scale)]
    [InlineData(TransformGizmoMode.Combined)]
    public void TransformHandles_AreVisibleBehindOpaqueGeometry(TransformGizmoMode mode)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var scene = new Node();
        var front = new Node { Parent = scene, Position = new Vector3(0, 0, 2), Scale = new Vector3(8) };
        front.AddComponent(new ModelComponent { ModelOverride = PreviewQuadMesh.Get(fixture.Vulkan) });
        scene.Children.Add(front);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), 320, 240);
        viewer.Camera.Position = Vector3.Zero;
        var baseline = Pixels(viewer, scene);
        var gizmo = new TransformGizmo { SelectedNode = new Node { Position = new Vector3(0, 0, 4) }, Mode = mode };
        viewer.OnPopulateGizmos = drawing => gizmo.Draw(drawing, viewer.Camera, new Vector2(320, 240));
        var overlay = Pixels(viewer, scene);
        Assert.NotEqual(baseline, overlay);
        Assert.True(overlay.Where((value, index) => value != baseline[index]).Count() > 100);
    }

    /// <summary>Filled world geometry obeys depth while overlay triangles ignore scene occlusion.</summary>
    [Fact]
    public void TriangleAndLine_DepthSettingsAreRespected()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var scene = new Node();
        var front = new Node { Position = new Vector3(0, 0, 2), Scale = new Vector3(8), Parent = scene };
        front.AddComponent(new ModelComponent { ModelOverride = PreviewQuadMesh.Get(fixture.Vulkan) });
        scene.Children.Add(front);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), 128, 128);
        viewer.Camera.Position = Vector3.Zero;
        var baseline = Pixels(viewer, scene);
        var depth = true;
        viewer.OnPopulateGizmos = drawing =>
        {
            drawing.DepthTest = depth;
            drawing.Color = new Vector4(1, 0, 0, 1);
            drawing.DrawTriangle(new Vector3(-1, -1, 4), new Vector3(1, -1, 4), new Vector3(0, 1, 4));
            drawing.Thickness = 8f;
            drawing.DrawLine(new Vector3(-1, 0, 4), new Vector3(1, 0, 4));
        };
        Assert.Equal(baseline, Pixels(viewer, scene));
        depth = false;
        Assert.NotEqual(baseline, Pixels(viewer, scene));
    }

    /// <summary>All transform tools render solid primitives on an obliquely viewed cube.</summary>
    [Fact]
    public void AllTools_RenderOnCubeAndShowRotationFeedback()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var cube = ModelUtils.CreateCubeModel6(fixture.Vulkan);
        var node = new Node { Name = "Cube" };
        node.AddComponent(new ModelComponent { ModelOverride = cube });
        var scene = new Node();
        node.Parent = scene;
        scene.Children.Add(node);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), 640, 480);
        viewer.Camera.Position = new Vector3(3, 2.3f, -4);
        viewer.Camera.LookIn(-viewer.Camera.Position, Vector3.UnitY);
        var gizmo = new TransformGizmo { SelectedNode = node, SnapRotation = 0f };
        var viewport = new Vector2(640, 480);
        viewer.OnPopulateGizmos = drawing => gizmo.Draw(drawing, viewer.Camera, viewport);
        foreach (var mode in Enum.GetValues<TransformGizmoMode>())
        {
            gizmo.Mode = mode;
            var pixels = Pixels(viewer, scene);
            SaveCapture(mode.ToString(), pixels, 640, 480);
        }
        gizmo.Mode = TransformGizmoMode.Rotate;
        Pixels(viewer, scene);
        var ring = viewer.Gizmos.OverlayLines.First(line => line.Color.Z > line.Color.X && line.Color.Z > line.Color.Y);
        var start = Pixel(viewer.Camera, (ring.A + ring.B) * 0.5f, viewport);
        gizmo.ProcessPointerMove(start, viewer.Camera, viewport);
        Assert.Equal(TransformGizmoMode.Rotate, gizmo.HandleMode);
        SaveCapture("Rotate-hover", Pixels(viewer, scene), 640, 480);
        gizmo.ProcessPointerDown(start, viewer.Camera, viewport);
        Assert.True(gizmo.IsDragging);
        gizmo.ProcessPointerMove(start + new Vector2(25, -25), viewer.Camera, viewport);
        Assert.True(MathF.Abs(gizmo.RotationDegrees) > 0f);
        SaveCapture("Rotate-drag", Pixels(viewer, scene), 640, 480);
    }

    static Vector2 Pixel(EditorCamera camera, Vector3 world, Vector2 size) =>
        (camera.Project(world) + Vector2.One) * 0.5f * size;

    /// <summary>Segments crossing the near plane cannot fold onto the opposite side of the screen.</summary>
    [Fact]
    public void LineCrossingNearPlaneMatchesClippedSegment()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), 256, 256);
        viewer.Camera.Position = Vector3.Zero;
        var scene = new Node();
        var baseline = Pixels(viewer, scene);
        var startZ = -2f;
        viewer.OnPopulateGizmos = drawing =>
        {
            drawing.DepthTest = false;
            drawing.Color = new Vector4(1, 0, 0, 1);
            drawing.Thickness = 5;
            drawing.DrawLine(new Vector3(1, 0, startZ), new Vector3(1, 0, 4));
        };
        var crossing = Pixels(viewer, scene);
        Assert.Equal(baseline.AsSpan((128 * 256 + 128) * 4, 4).ToArray(),
            crossing.AsSpan((128 * 256 + 128) * 4, 4).ToArray());
        Assert.NotEqual(baseline, crossing);
        startZ = viewer.Camera.NearPlane;
        Assert.Equal(crossing, Pixels(viewer, scene));
    }

    static byte[] Pixels(SceneViewerService viewer, Node scene)
    {
        viewer.Render(scene, 0.016);
        var pixels = new byte[viewer.Width * viewer.Height * 4];
        viewer.CopyPixels(pixels);
        return pixels;
    }

    static unsafe void SaveCapture(string name, byte[] pixels, int width, int height)
    {
        if (Environment.GetEnvironmentVariable("TURIAN_GIZMO_CAPTURES") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        fixed (byte* pointer = pixels)
        {
            using var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888),
                (nint)pointer);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(directory, name + ".png"));
            data.SaveTo(file);
        }
    }
}
