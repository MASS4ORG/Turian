namespace Turian.Engine.UI;

/// <summary>
/// Projects a screen-space pointer onto a world-space UI panel: builds a ray from the camera
/// through the pointer, intersects the panel's unit quad and returns the hit in panel pixels.
/// The pixel mapping is the exact inverse of <c>worldui.vert</c>'s UV (<c>u = localX + 0.5</c>,
/// <c>v = localY + 0.5</c>), so a hovered pixel here is the pixel the shader drew there.
/// </summary>
/// <remarks>
/// Matrices follow the engine's row-vector convention: a point is transformed as <c>v · M</c>, and
/// <c>view * projection</c> is "apply view, then projection".
/// </remarks>
public static class WorldPanelPointer
{
    /// <summary>Tries to hit the panel described by <paramref name="model"/>.</summary>
    /// <param name="model">The panel's world transform (unit quad on local XY, <c>[-0.5, 0.5]</c>).</param>
    /// <param name="viewProjection">The camera's <c>view * projection</c> matrix.</param>
    /// <param name="pointerPixels">Pointer position in viewport pixels, origin top-left.</param>
    /// <param name="viewportPixels">Viewport size in pixels.</param>
    /// <param name="panelPixels">Panel size in pixels the UI was rasterised at.</param>
    /// <param name="hit">The hit position in panel pixels (origin top-left), when this returns <c>true</c>.</param>
    /// <returns><c>true</c> when the ray hits inside the panel quad.</returns>
    public static bool TryHit(
        Matrix4x4 model,
        Matrix4x4 viewProjection,
        Vector2 pointerPixels,
        Vector2 viewportPixels,
        Vector2 panelPixels,
        out Vector2 hit)
    {
        hit = default;
        if (viewportPixels.X <= 0f || viewportPixels.Y <= 0f) return false;

        if (!Matrix4x4.Invert(viewProjection, out var invViewProj)) return false;
        if (!Matrix4x4.Invert(model, out var invModel)) return false;

        // Pointer → NDC (Vulkan: x right in [-1,1], y down in [-1,1]).
        var ndcX = (2f * pointerPixels.X / viewportPixels.X) - 1f;
        var ndcY = (2f * pointerPixels.Y / viewportPixels.Y) - 1f;

        var near = Unproject(ndcX, ndcY, 0f, invViewProj);
        var far = Unproject(ndcX, ndcY, 1f, invViewProj);

        // Ray into panel-local space.
        var localNear = Vector4.Transform(new Vector4(near, 1f), invModel);
        var localFar = Vector4.Transform(new Vector4(far, 1f), invModel);
        var lo = new Vector3(localNear.X, localNear.Y, localNear.Z) / SafeW(localNear.W);
        var lf = new Vector3(localFar.X, localFar.Y, localFar.Z) / SafeW(localFar.W);
        var dir = lf - lo;
        if (MathF.Abs(dir.Z) < 1e-6f) return false;

        var t = -lo.Z / dir.Z;
        if (t < 0f) return false;

        var lx = lo.X + (t * dir.X);
        var ly = lo.Y + (t * dir.Y);
        if (lx is < -0.5f or > 0.5f || ly is < -0.5f or > 0.5f) return false;

        hit = new Vector2((lx + 0.5f) * panelPixels.X, (ly + 0.5f) * panelPixels.Y);
        return true;
    }

    static Vector3 Unproject(float ndcX, float ndcY, float ndcZ, Matrix4x4 invViewProj)
    {
        var p = Vector4.Transform(new Vector4(ndcX, ndcY, ndcZ, 1f), invViewProj);
        var w = SafeW(p.W);
        return new Vector3(p.X / w, p.Y / w, p.Z / w);
    }

    static float SafeW(float w) => MathF.Abs(w) < 1e-9f ? 1f : w;
}
