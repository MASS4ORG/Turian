namespace Turian.Engine.Core;

/// <summary>
/// Screen-space/world-space conversions shared by anything that needs to reason about what a
/// camera sees: the transform gizmo's hit-testing and viewport object picking both need a world
/// ray from a screen point, and both need it to work for orthographic cameras, whose rays are
/// parallel rather than emanating from the camera position.
/// </summary>
public static class CameraMath
{
    /// <summary>
    /// Unprojects a normalized-device-coordinate point at a given depth into world space.
    /// </summary>
    /// <param name="camera">The camera whose view/projection matrices define the mapping.</param>
    /// <param name="ndcX">X in [-1, 1].</param>
    /// <param name="ndcY">Y in [-1, 1].</param>
    /// <param name="ndcZ">Depth in [0, 1]: 0 is the near plane, 1 is the far plane.</param>
    public static Vector3 Unproject(ICamera camera, float ndcX, float ndcY, float ndcZ)
    {
        ArgumentNullException.ThrowIfNull(camera);

        var v = new Vector4(ndcX, ndcY, ndcZ, 1f);
        _ = Matrix4x4.Invert(camera.GetProjectionMatrix(), out var projInv);
        _ = Matrix4x4.Invert(camera.GetViewMatrix(), out var viewInv);
        v = Vector4.Transform(v, projInv);
        v = Vector4.Transform(v, viewInv);
        if (MathF.Abs(v.W) > float.Epsilon) v /= v.W;
        return new Vector3(v.X, v.Y, v.Z);
    }

    /// <summary>
    /// Builds the world-space ray a screen point casts from <paramref name="camera"/>. Unprojects
    /// the point at two depths and derives origin and direction from those, which stays correct
    /// for both perspective (rays emanate from the camera) and orthographic cameras (rays are
    /// parallel to <see cref="ICamera.Front"/>) — unprojecting the camera position alone cannot
    /// express the orthographic case.
    /// </summary>
    /// <param name="camera">The camera the screen point is viewed through.</param>
    /// <param name="screenPos">Pointer position in viewport pixels.</param>
    /// <param name="viewportSize">Viewport size in pixels.</param>
    /// <returns><c>null</c> when the viewport has no area or the near/far points coincide.</returns>
    public static Ray? ScreenPointToRay(ICamera camera, Vector2 screenPos, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f) return null;

        var ndcX = (screenPos.X / viewportSize.X * 2f) - 1f;
        var ndcY = (screenPos.Y / viewportSize.Y * 2f) - 1f;

        var near = Unproject(camera, ndcX, ndcY, 0f);
        var far = Unproject(camera, ndcX, ndcY, 1f);
        var direction = far - near;
        return direction.LengthSquared() < 1e-9f ? null : new Ray(near, Vector3.Normalize(direction));
    }
}
