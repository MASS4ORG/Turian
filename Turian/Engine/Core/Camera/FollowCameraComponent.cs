namespace Turian.Engine.Core;

/// <summary>
/// Keeps the camera at a fixed offset from a target node every frame.
/// </summary>
[RequireComponent(typeof(CameraComponent))]
[DisallowMultipleComponent]
[ComponentContextMenu("Rendering/Camera/Follow")]
[TypeId("a3000001-0000-4000-8000-000000000002")]
[PublicAPI]
public class FollowCameraComponent : Component
{
    /// <summary>The node the camera follows. When null, the component is a no-op.</summary>
    public Node? Target { get; set; }

    /// <summary>World-space offset from the target's position.</summary>
    public Vector3 Offset { get; set; } = new(0f, 2f, -5f);

    /// <inheritdoc/>
    public override void OnLateUpdate(float deltaTime)
    {
        if (Target is null) return;

        var camera = Node?.GetComponent<CameraComponent>();
        if (camera is null) return;

        camera.Position = Target.GlobalTransform.Position + Offset;
    }
}
