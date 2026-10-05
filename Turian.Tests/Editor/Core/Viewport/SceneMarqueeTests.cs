namespace Turian.Tests;

/// <summary>Checks rectangle selection against projected geometry, clipping, activity and Scene layer filters.</summary>
public sealed class SceneMarqueeTests
{
    static readonly Vector2 Viewport = new(960, 540);

    static EditorCamera Camera()
    {
        var camera = new EditorCamera { Position = new Vector3(0, 0, -5), IsOrthographic = true, Frustum = 10 };
        camera.Resize(960, 540);
        return camera;
    }

    /// <summary>Rectangle selection works in either drag direction and avoids objects outside the visible rectangle.</summary>
    [Fact]
    public void RectangleMatchesProjectedBounds()
    {
        var camera = Camera();
        var inside = new Node();
        var outside = new Node();
        var bounds = new Bounds(new Vector3(-0.5f), new Vector3(0.5f));
        (Node, Bounds)[] candidates = [(inside, bounds), (outside, new Bounds(new Vector3(10), new Vector3(11)))];
        var start = new Vector2(460, 250);
        var end = new Vector2(500, 290);
        Assert.Equal([inside], SceneMarquee.SelectBounds(candidates, camera, start, end, Viewport));
        Assert.Equal([inside], SceneMarquee.SelectBounds(candidates, camera, end, start, Viewport));
        Assert.Empty(SceneMarquee.SelectBounds(candidates, camera, Vector2.Zero, new Vector2(10), Viewport));
        Assert.Empty(SceneMarquee.Pick(new Node(), camera, start, end, Viewport));
    }

    /// <summary>Invisible, locked, inactive and clipped objects cannot enter the selection.</summary>
    [Fact]
    public void SelectionRespectsVisibilityLocksAndClipping()
    {
        var camera = Camera();
        var settings = new SceneViewSettings();
        settings.SetVisible(1, false);
        settings.SetLocked(2, true);
        var bounds = new Bounds(new Vector3(-0.5f), new Vector3(0.5f));
        var active = new Node();
        (Node, Bounds)[] candidates =
        [
            (active, bounds), (active, bounds),
            (new Node { RenderLayer = 1 }, bounds),
            (new Node { RenderLayer = 2 }, bounds),
            (new Node { IsActive = false }, bounds),
            (new Node(), new Bounds(new Vector3(-1, -1, -20), new Vector3(1, 1, -19))),
            (new Node(), new Bounds(new Vector3(-1, -1, 10000), new Vector3(1, 1, 10001))),
            (new Node(), Bounds.Empty),
        ];
        Assert.Equal([active], SceneMarquee.SelectBounds(candidates, camera, Vector2.Zero, Viewport, Viewport, settings));
        Assert.Empty(SceneMarquee.SelectBounds(candidates, camera, Vector2.Zero, Viewport, Vector2.Zero));
        Assert.Empty(SceneMarquee.SelectBounds(candidates, camera, new Vector2(-100), new Vector2(-10), Viewport));
    }

    /// <summary>A bound crossing the near clipping plane is selected where its visible part projects.</summary>
    [Fact]
    public void PerspectiveNearPlaneIsClipped()
    {
        var camera = Camera();
        camera.IsOrthographic = false;
        camera.NearPlane = 0.1f;
        var node = new Node();
        var bounds = new Bounds(new Vector3(-1, -1, -6), new Vector3(1, 1, -4));
        Assert.Equal([node], SceneMarquee.SelectBounds([(node, bounds)], camera, Viewport * 0.4f,
            Viewport * 0.6f, Viewport));
    }
}
