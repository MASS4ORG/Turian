namespace Turian.Engine.UI;

/// <summary>
/// Makes the sibling <see cref="UiDocumentComponent"/>'s <see cref="UiRenderMode.WorldSpace"/> panel
/// interactive: each frame the active camera's pointer ray is intersected with the panel quad and,
/// on a hit, drives that panel's input at the corresponding pixel. Without this component a
/// world-space panel is display-only.
/// </summary>
[RequireComponent(typeof(UiDocumentComponent))]
[DisallowMultipleComponent]
[ComponentContextMenu("UI/UI Raycaster")]
[TypeId("a3000001-0000-4000-8000-000000000024")]
[PublicAPI]
public class UiRaycasterComponent : Component
{
    /// <summary>
    /// Maximum ray length in world units; a panel farther than this is not interactive. Zero or
    /// negative means unlimited.
    /// </summary>
    public float MaxDistance { get; set; }
}
