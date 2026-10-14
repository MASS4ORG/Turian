namespace Turian.Tests;

/// <summary>Tests for <see cref="WorldPanelPointer"/> — camera-ray → panel-pixel projection.</summary>
public sealed class WorldPanelPointerTests
{
    const int vpW = 800;
    const int vpH = 600;
    const int panelW = 1024;
    const int panelH = 640;

    // Camera at the origin looking down -Z, up +Y.
    static Matrix4x4 ViewProjection()
    {
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, (float)vpW / vpH, 0.05f, 100f);
        return view * proj;
    }

    // A unit quad 3 m ahead, aspect-corrected like UiManager does.
    static Matrix4x4 PanelModel()
    {
        var aspect = (float)panelH / panelW;
        return Matrix4x4.CreateScale(1f, aspect, 1f) * Matrix4x4.CreateTranslation(0f, 0f, -3f);
    }

    /// <summary>The screen center hits the center of the panel quad.</summary>
    [Fact]
    public void CenterOfScreen_HitsCenterOfPanel()
    {
        var ok = WorldPanelPointer.TryHit(
            PanelModel(), ViewProjection(),
            new Vector2(vpW / 2f, vpH / 2f), new Vector2(vpW, vpH), new Vector2(panelW, panelH),
            out var hit);

        Assert.True(ok);
        Assert.Equal(panelW / 2f, hit.X, 1f);
        Assert.Equal(panelH / 2f, hit.Y, 1f);
    }

    /// <summary>A pointer left of center maps to a lower panel X.</summary>
    [Fact]
    public void PointerLeftOfCenter_MapsToLowerPanelX()
    {
        WorldPanelPointer.TryHit(
            PanelModel(), ViewProjection(),
            new Vector2((vpW / 2f) - 100f, vpH / 2f), new Vector2(vpW, vpH), new Vector2(panelW, panelH),
            out var hit);

        Assert.True(hit.X < panelW / 2f);
    }

    /// <summary>A pointer above center maps to a lower panel Y.</summary>
    [Fact]
    public void PointerAboveCenter_MapsToLowerPanelY()
    {
        // Screen "above center" is a smaller pixel-y; it must land on a smaller panel-y (top).
        WorldPanelPointer.TryHit(
            PanelModel(), ViewProjection(),
            new Vector2(vpW / 2f, (vpH / 2f) - 100f), new Vector2(vpW, vpH), new Vector2(panelW, panelH),
            out var hit);

        Assert.True(hit.Y < panelH / 2f);
    }

    /// <summary>A ray that misses the quad returns false.</summary>
    [Fact]
    public void RayMissingTheQuad_ReturnsFalse()
    {
        var ok = WorldPanelPointer.TryHit(
            PanelModel(), ViewProjection(),
            new Vector2(2f, 2f), new Vector2(vpW, vpH), new Vector2(panelW, panelH),
            out _);

        Assert.False(ok);
    }

    /// <summary>A panel behind the camera is never hit.</summary>
    [Fact]
    public void PanelBehindCamera_ReturnsFalse()
    {
        var behind = Matrix4x4.CreateTranslation(0f, 0f, 3f); // +Z is behind a -Z-looking camera
        var ok = WorldPanelPointer.TryHit(
            behind, ViewProjection(),
            new Vector2(vpW / 2f, vpH / 2f), new Vector2(vpW, vpH), new Vector2(panelW, panelH),
            out _);

        Assert.False(ok);
    }

    /// <summary>Viewport dimensions must be positive before a ray can be projected.</summary>
    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(-1, 600)]
    [InlineData(800, -1)]
    public void InvalidViewport_ReturnsFalse(float width, float height)
    {
        Assert.False(WorldPanelPointer.TryHit(PanelModel(), ViewProjection(), new Vector2(400, 300),
            new Vector2(width, height), new Vector2(panelW, panelH), out var hit));
        Assert.Equal(Vector2.Zero, hit);
    }

    /// <summary>Singular panel or camera matrices cannot produce a hit.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SingularMatrix_ReturnsFalse(bool singularModel)
    {
        var model = singularModel ? default : PanelModel();
        var camera = singularModel ? ViewProjection() : default;
        Assert.False(WorldPanelPointer.TryHit(model, camera, new Vector2(400, 300),
            new Vector2(vpW, vpH), new Vector2(panelW, panelH), out var hit));
        Assert.Equal(Vector2.Zero, hit);
    }

    /// <summary>Each quad edge maps to the corresponding panel edge, and points beyond it miss.</summary>
    [Theory]
    [InlineData(0.25f, 0.5f, true, 0f, 0.5f)]
    [InlineData(0.75f, 0.5f, true, 1f, 0.5f)]
    [InlineData(0.5f, 0.25f, true, 0.5f, 0f)]
    [InlineData(0.5f, 0.75f, true, 0.5f, 1f)]
    [InlineData(0.24f, 0.5f, false, 0f, 0f)]
    [InlineData(0.76f, 0.5f, false, 0f, 0f)]
    [InlineData(0.5f, 0.24f, false, 0f, 0f)]
    [InlineData(0.5f, 0.76f, false, 0f, 0f)]
    public void QuadEdges(float x, float y, bool expected, float hitX, float hitY)
    {
        var panel = new Vector2(panelW, panelH);
        Assert.Equal(expected, WorldPanelPointer.TryHit(Matrix4x4.Identity, Matrix4x4.Identity,
            new Vector2(x * vpW, y * vpH), new Vector2(vpW, vpH), panel, out var hit));
        Assert.Equal(hitX * panelW, hit.X, 0.01f);
        Assert.Equal(hitY * panelH, hit.Y, 0.01f);
    }

    /// <summary>A ray parallel to the panel plane has no intersection.</summary>
    [Fact]
    public void ParallelRay_ReturnsFalse()
    {
        Assert.False(WorldPanelPointer.TryHit(Matrix4x4.CreateRotationY(MathF.PI / 2f), Matrix4x4.Identity,
            new Vector2(400, 300), new Vector2(vpW, vpH), new Vector2(panelW, panelH), out var hit));
        Assert.Equal(Vector2.Zero, hit);
    }
}
