namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws any of the vector types the inspector knows as one labelled row of per-axis fields. The
/// row layout and the axis drag/type behaviour came straight from the dispatcher's hard-coded case;
/// registering it as a <see cref="CustomEditorAttribute"/> lets the same two primitives serve the
/// transform editor too.
/// </summary>
[CustomEditor(typeof(Vector2))]
[CustomEditor(typeof(Vector3))]
[CustomEditor(typeof(Vector4))]
sealed class VectorValueEditor : IValueEditor
{
    static readonly string[] axes = ["X", "Y", "Z", "W"];

    static StudioTheme Theme => StudioTheme.Current;

    static float RowHeight => Theme.Scale(Theme.RowHeight);
    static float AxisLabelWidth => Theme.Scale(12f);
    static GuiColor InkDim => Theme.InkDim;

    public void Draw(Gui gui, FormField field, string id) =>
        FieldDrawers.Row(gui, field.Label, id, () => DrawValue(gui, field, id));

    public bool DrawValue(Gui gui, FormField field, string id)
    {
        EditAxes(gui, field, id, TryRead(field.GetValue()), Write);
        return true;
    }

    /// <summary>
    /// A labelled vector row with three fields, how the transform editor shows Position, Rotation
    /// and Scale. Edits write through <paramref name="set"/> and report the in-place change.
    /// </summary>
    internal static void DrawRow3(Gui gui, string id, string label, Vector3 value,
        Action<Vector3> set, FormField field)
    {
        FieldDrawers.Row(gui, label, id, () =>
        {
            Span<double> parts = [value.X, value.Y, value.Z];
            var changed = false;

            for (var i = 0; i < 3; i++)
            {
                if (!AxisField(gui, i, parts[i], $"{id}/a{i}", out var next)) continue;

                parts[i] = next;
                changed = true;
            }

            if (!changed) return;

            set(new Vector3((float)parts[0], (float)parts[1], (float)parts[2]));
            field.Touch();
        });
    }

    static void EditAxes(Gui gui, FormField field, string id, double[] values,
        Action<FormField, double[]> write)
    {
        var changed = false;

        for (var i = 0; i < values.Length; i++)
        {
            if (!AxisField(gui, i, values[i], $"{id}/a{i}", out var next)) continue;

            values[i] = next;
            changed = true;
        }

        if (changed) write(field, values);
    }

    static void Write(FormField field, double[] values)
    {
        var type = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;
        var f = Array.ConvertAll(values, a => (float)a);

        object? written = null;
        if (type == typeof(Vector2)) written = new Vector2(f[0], f[1]);
        else if (type == typeof(Vector3)) written = new Vector3(f[0], f[1], f[2]);
        else if (type == typeof(Vector4)) written = new Vector4(f[0], f[1], f[2], f[3]);

        if (written is not null) field.SetValue(written);
    }

    /// <summary>One axis: a dim X/Y/Z prefix and a field that shares the width evenly.</summary>
    static bool AxisField(Gui gui, int index, double value, string id, out double result)
    {
        using (gui.Node(AxisLabelWidth, RowHeight, $"{id}/axis").Enter())
        {
            var hot = gui.Pass == Pass.Pass2Render && gui.GetInteractable().OnHover();
            if (gui.Pass == Pass.Pass2Render && gui.GetInteractable().OnDrag(out var drag))
            {
                var delta = (drag.FrameDelta.X - drag.FrameDelta.Y) * 0.01;
                value += delta;
            }
            gui.DrawText(axes[Math.Min(index, axes.Length - 1)], Theme.Text(11),
                hot ? Theme.Ink : InkDim, centerInRect: false);
        }

        return FieldDrawers.TryEdit(gui, value, id, integral: false, float.MinValue, float.MaxValue,
            out result);
    }

    static double[] TryRead(object? value) => value switch
    {
        Vector2 v => [v.X, v.Y],
        Vector3 v => [v.X, v.Y, v.Z],
        Vector4 v => [v.X, v.Y, v.Z, v.W],
        _ => [],
    };
}
