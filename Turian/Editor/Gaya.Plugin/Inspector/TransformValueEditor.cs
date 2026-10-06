namespace Gaya.Plugin.Turian;

/// <summary>
/// A transform as three labelled vector rows — Position, Rotation, Scale.
/// Each edit writes a changed copy back through the field, since a transform is an immutable value.
/// </summary>
[CustomEditor(typeof(Transform))]
sealed class TransformValueEditor : IPropertyDrawer
{
    /// <inheritdoc />
    public void Draw(Gui gui, FormField field, string id, FormRenderContext context)
    {
        gui.FormField(field.Project<Transform, Vector3>("Position", value => value.Position,
            (value, part) => value with { Position = part }), $"{id}/pos", context);
        gui.FormField(field.Project<Transform, Vector3>("Rotation", value => value.Rotation,
            (value, part) => value with { Rotation = part }), $"{id}/rot", context);
        gui.FormField(field.Project<Transform, Vector3>("Scale", value => value.Scale,
            (value, part) => value with { Scale = part }), $"{id}/scale", context);
    }

    /// <summary>A transform has no value-only form: it is three independent rows by design.</summary>
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context) => false;
}
