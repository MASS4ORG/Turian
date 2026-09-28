namespace Gaya.Plugin.Turian;

/// <summary>
/// Existing type editors registered with <see cref="CustomEditorAttribute"/>. They also implement
/// the property-drawer contract so their registrations remain valid in the drawer pipeline.
/// </summary>
interface IValueEditor : IPropertyDrawer { }
