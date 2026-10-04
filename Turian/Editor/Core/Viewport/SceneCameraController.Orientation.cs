namespace Turian.Editor.Core;

public sealed partial class SceneCameraController
{
    /// <summary>Views the pivot from a signed world axis, keeping the camera's distance and entering orthographic mode.</summary>
    public void AlignToAxis(Vector3 direction, Vector3? pivot = null)
    {
        if (direction.LengthSquared() < 1e-8f) return;
        direction = Vector3.Normalize(direction);
        orbitPivot = pivot ?? Camera.Position + Camera.Front * orbitDistance;
        orbitDistance = MathF.Max(Vector3.Distance(Camera.Position, orbitPivot), 0.01f);
        var up = MathF.Abs(direction.Y) > 0.99f ? -Vector3.UnitZ : Vector3.UnitY;
        Camera.LookIn(-direction, up);
        Camera.Position = orbitPivot + direction * orbitDistance;
        SetProjection(true);
    }

    /// <summary>Toggles perspective and orthographic projection while preserving the pivot's apparent size.</summary>
    public void ToggleProjection(Vector3? pivot = null)
    {
        var target = pivot ?? Camera.Position + Camera.Front * orbitDistance;
        orbitDistance = MathF.Max(Vector3.Dot(target - Camera.Position, Camera.Front), 0.01f);
        orbitPivot = Camera.Position + Camera.Front * orbitDistance;
        SetProjection(!Camera.IsOrthographic);
    }

    void SetProjection(bool orthographic)
    {
        if (Camera.IsOrthographic == orthographic) return;
        if (orthographic) Camera.Frustum = orbitDistance * MathF.Tan(Camera.FieldOfView * 0.5f);
        else
        {
            orbitDistance = Camera.Frustum / MathF.Tan(Camera.FieldOfView * 0.5f);
            Camera.Position = orbitPivot - Camera.Front * orbitDistance;
        }
        Camera.IsOrthographic = orthographic;
    }
}
