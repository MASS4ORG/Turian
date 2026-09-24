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

    /// <summary>The screen centre hits the centre of the panel quad.</summary>
    [Fact]
    public void CentreOfScreen_HitsCentreOfPanel()
    {
        var ok = WorldPanelPointer.TryHit(
            PanelModel(), ViewProjection(),
            new Vector2(vpW / 2f, vpH / 2f), new Vector2(vpW, vpH), new Vector2(panelW, panelH),
            out var hit);

        Assert.True(ok);
        Assert.Equal(panelW / 2f, hit.X, 1f);
        Assert.Equal(panelH / 2f, hit.Y, 1f);
    }

    /// <summary>A pointer left of centre maps to a lower panel X.</summary>
    [Fact]
    public void PointerLeftOfCentre_MapsToLowerPanelX()
    {
        WorldPanelPointer.TryHit(
            PanelModel(), ViewProjection(),
            new Vector2((vpW / 2f) - 100f, vpH / 2f), new Vector2(vpW, vpH), new Vector2(panelW, panelH),
            out var hit);

        Assert.True(hit.X < panelW / 2f);
    }

    /// <summary>A pointer above centre maps to a lower panel Y.</summary>
    [Fact]
    public void PointerAboveCentre_MapsToLowerPanelY()
    {
        // Screen "above centre" is a smaller pixel-y; it must land on a smaller panel-y (top).
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
}
