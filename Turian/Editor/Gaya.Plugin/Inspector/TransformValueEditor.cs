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
        if (field.GetValue() is not Transform transform) return;

        FormControls.VectorRow(gui, $"{id}/pos", "Position", transform.Position,
            v => field.SetValue(transform with { Position = v }), field, context);
        FormControls.VectorRow(gui, $"{id}/rot", "Rotation", transform.Rotation,
            v => field.SetValue(transform with { Rotation = v }), field, context);
        FormControls.VectorRow(gui, $"{id}/scale", "Scale", transform.Scale,
            v => field.SetValue(transform with { Scale = v }), field, context);
    }

    /// <summary>A transform has no value-only form: it is three independent rows by design.</summary>
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context) => false;
}
