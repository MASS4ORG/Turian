namespace Gaya.Plugin.Turian;

/// <summary>
/// A transform as three labelled vector rows — Position, Rotation, Scale.
/// Rotation is edited in degrees through the transform's own setters, which is why this editor
/// touches the value in place rather than assigning the field.
/// </summary>
[CustomEditor(typeof(Transform))]
sealed class TransformValueEditor : IPropertyDrawer
{
    /// <inheritdoc />
    public void Draw(Gui gui, FormField field, string id, FormRenderContext context)
    {
        if (field.GetValue() is not Transform transform) return;

        FormControls.VectorRow(gui, $"{id}/pos", "Position", transform.Position,
            v => transform.Position = v, field, context);
        FormControls.VectorRow(gui, $"{id}/rot", "Rotation", transform.Rotation,
            v => transform.Rotation = v, field, context);
        FormControls.VectorRow(gui, $"{id}/scale", "Scale", transform.Scale,
            v => transform.Scale = v, field, context);
    }

    /// <summary>A transform has no value-only form: it is three independent rows by design.</summary>
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context) => false;
}
