namespace Turian.Editor.Core;

/// <summary>
/// Builds the single directional light every scene-based asset preview lights its subject with — a
/// fixed 3/4 angle.
/// </summary>
static class PreviewLighting
{
    /// <summary>Creates a node carrying one directional light aimed at a fixed 3/4 angle.</summary>
    public static Node CreateLightNode()
    {
        var node = new Node { Name = "Light" };
        node.Transform.Rotation = new Vector3(35f, -45f, 0f);
        node.AddComponent(LightComponent.CreateDirectionalLight(1f, new Vector4(1f)));
        return node;
    }
}
