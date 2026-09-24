namespace Turian;

/// <summary>
/// Registers a gizmo drawer (an <c>IGizmoDrawer</c> implementation) for a component type. The drawer
/// is invoked by the editor every frame while the component is visible in the Scene View.
/// </summary>
/// <remarks>
/// The attribute is available to user code so plugins can define custom gizmos without any editor
/// project dependency (only the engine is referenced).
/// </remarks>
/// <param name="drawerType">The <c>IGizmoDrawer</c> implementation to invoke.</param>
/// <param name="componentType">
/// The component type the drawer applies to. Any assignable component is matched (subclassing is supported).
/// </param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class CustomGizmoAttribute(Type drawerType, Type componentType) : Attribute
{
    /// <summary>The <c>IGizmoDrawer</c> implementation to invoke.</summary>
    public Type DrawerType { get; } = drawerType;

    /// <summary>The component type the drawer applies to.</summary>
    public Type ComponentType { get; } = componentType;
}
