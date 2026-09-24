namespace Turian.Tests;

/// <summary>Tests for <see cref="CameraMath.ScreenPointToRay"/>.</summary>
public class CameraMathTests
{
    static readonly Vector2 viewport = new(960f, 540f);

    /// <summary>The viewport centre casts a ray straight down the camera's forward axis.</summary>
    [Fact]
    public void ScreenPointToRay_ViewportCenter_PointsAlongFront()
    {
        var camera = new EditorCamera { Position = new Vector3(1f, 2f, 3f) };
        var center = new Vector2(viewport.X / 2f, viewport.Y / 2f);

        var ray = CameraMath.ScreenPointToRay(camera, center, viewport);

        Assert.NotNull(ray);
        Assert.True(Vector3.Distance(Vector3.Normalize(ray.Value.Direction), camera.Front) < 1e-3f);
    }

    /// <summary>A perspective camera's rays diverge outward from its position toward the corners.</summary>
    [Fact]
    public void ScreenPointToRay_Perspective_RaysDivergeFromTheCameraPosition()
    {
        // FieldOfView's default (10 rad) is degenerate for CreatePerspectiveFieldOfView — see the
        // open note on EditorCamera.FieldOfView — so this test sets a normal 60° explicitly.
        var camera = new EditorCamera
        {
            Position = new Vector3(0f, 0f, 0f),
            IsOrthographic = false,
            FieldOfView = 60f * MathF.PI / 180f,
        };
        var topLeft = new Vector2(0f, 0f);
        var bottomRight = new Vector2(viewport.X, viewport.Y);

        var rayA = CameraMath.ScreenPointToRay(camera, topLeft, viewport)!.Value;
        var rayB = CameraMath.ScreenPointToRay(camera, bottomRight, viewport)!.Value;

        // For a perspective camera every ray originates near the camera position — "near" because
        // the ray's origin is the unprojected near plane, offset from the camera by NearPlane.
        Assert.True(Vector3.Distance(rayA.Origin, camera.Position) < camera.NearPlane * 5f);
        Assert.True(Vector3.Distance(rayB.Origin, camera.Position) < camera.NearPlane * 5f);
        Assert.True(Vector3.Distance(rayA.Direction, rayB.Direction) > 0.1f);
    }

    /// <summary>
    /// An orthographic camera's rays are parallel: unprojecting the camera position alone cannot
    /// express this, which is why the two-depth technique exists.
    /// </summary>
    [Fact]
    public void ScreenPointToRay_Orthographic_RaysAreParallel()
    {
        var camera = new EditorCamera { Position = new Vector3(0f, 0f, -10f), IsOrthographic = true };
        var left = new Vector2(100f, 270f);
        var right = new Vector2(860f, 270f);

        var rayA = CameraMath.ScreenPointToRay(camera, left, viewport)!.Value;
        var rayB = CameraMath.ScreenPointToRay(camera, right, viewport)!.Value;

        Assert.True(Vector3.Distance(rayA.Direction, rayB.Direction) < 1e-3f);
        Assert.True(Vector3.Distance(rayA.Origin, rayB.Origin) > 1f);
    }

    /// <summary>A zero-area viewport has no rays to cast.</summary>
    [Fact]
    public void ScreenPointToRay_EmptyViewport_ReturnsNull()
    {
        var camera = new EditorCamera();

        Assert.Null(CameraMath.ScreenPointToRay(camera, Vector2.Zero, Vector2.Zero));
    }
}
