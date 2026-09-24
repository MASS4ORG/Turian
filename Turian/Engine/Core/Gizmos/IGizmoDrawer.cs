namespace Turian.Engine.Core;

/// <summary>
/// Draws gizmos for a specific component type. Implementations are discovered by the editor through
/// <c>GizmoDrawerCatalog</c> and invoked for every gizmo-capable component of the selected node (or in
/// the scene, when the editor is in "all gizmos" mode).
///
/// <para>Implementations may live in user assemblies: apply <c>[CustomGizmo]</c> to register a drawer
/// for a component type.</para>
/// </summary>
public interface IGizmoDrawer
{
    /// <summary>Invoked every frame for each matching component. Draw into <paramref name="gizmos"/>.</summary>
    void DrawGizmos(Gizmos gizmos, Component component);
}
