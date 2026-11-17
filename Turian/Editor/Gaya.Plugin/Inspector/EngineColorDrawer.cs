using EngineColor = Turian.Engine.Core.Color;

namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws the engine's color types, linear <see cref="EngineColor"/> and sRGB <see cref="Color32"/>, and vectors marked
/// <see cref="ColorUsageAttribute"/>, with Guinevere's
/// color field: a swatch that opens the picker, and an editable hex value. A field is written only when the user
/// changes it, so an HDR value the picker cannot show is never clamped by merely selecting the node.
/// </summary>
sealed class EngineColorDrawer : IPropertyDrawer
{
    /// <summary>The shared drawer.</summary>
    public static readonly EngineColorDrawer Instance = new();

    /// <summary>Whether the drawer takes a field: an engine color.</summary>
    /// <param name="field">The field.</param>
    /// <returns>True for <see cref="EngineColor"/> and <see cref="Color32"/>.</returns>
    public static bool Takes(FormField field) => field.ValueType == typeof(EngineColor)
        || field.ValueType == typeof(Color32);

    /// <summary>
    /// The picker's color for an engine value: linear colors are shown in sRGB, as a monitor displays them, and a
    /// <see cref="ColorUsageAttribute"/> vector is shown as its channels are, 0..1 to bytes.
    /// </summary>
    /// <param name="value">An <see cref="EngineColor"/>, <see cref="Color32"/>, <see cref="Vector3"/> or <see cref="Vector4"/>.</param>
    /// <returns>The same color for the picker.</returns>
    public static GuiColor ToPicker(object? value)
    {
        switch (value)
        {
            case Vector4 vector:
                return GuiColor.FromArgb(Byte(vector.W), Byte(vector.X), Byte(vector.Y), Byte(vector.Z));
            case Vector3 rgb:
                return GuiColor.FromArgb(255, Byte(rgb.X), Byte(rgb.Y), Byte(rgb.Z));
        }

        var packed = value is EngineColor linear ? linear.ToSrgb() : value is Color32 bytes ? bytes : default;
        return GuiColor.FromArgb(packed.A, packed.R, packed.G, packed.B);
    }

    static byte Byte(float channel) => (byte)MathF.Round(Math.Clamp(channel, 0f, 1f) * 255f);

    /// <summary>The engine value of the type a field holds for a color the picker produced.</summary>
    /// <param name="picked">The picker's color.</param>
    /// <param name="type">The field's type: an engine color, <see cref="Vector3"/> or <see cref="Vector4"/>.</param>
    /// <returns>The boxed engine color.</returns>
    public static object FromPicker(GuiColor picked, Type type)
    {
        if (type == typeof(Vector4))
        {
            return new Vector4(picked.R / 255f, picked.G / 255f, picked.B / 255f, picked.A / 255f);
        }

        if (type == typeof(Vector3))
        {
            return new Vector3(picked.R / 255f, picked.G / 255f, picked.B / 255f);
        }

        var packed = new Color32(picked.R, picked.G, picked.B, picked.A);
        return type == typeof(EngineColor) ? EngineColor.FromSrgb(packed) : packed;
    }

    /// <inheritdoc />
    public void Draw(Gui gui, FormField field, string id, FormRenderContext context) =>
        FormControls.Row(gui, field.Label, id, () => DrawValue(gui, field, id, context),
            context.IsModified?.Invoke(field) == true);

    /// <inheritdoc />
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context)
    {
        var theme = ThemeTokens.Current;
        if (field.HasMixedValue)
        {
            gui.DrawText("—", theme.Text(12), theme.InkDim);
            return true;
        }

        var color = ToPicker(field.GetValue());
        var alpha = field.ValueType != typeof(Vector3);
        if (gui.ColorField(ref color, ColorPickerOptions.For(gui) with { Alpha = alpha }, width: 0, height: theme.Scale(theme.RowHeight), enabled: !field.IsReadOnly,
                id: $"{id}/color"))
        {
            _ = field.SetValue(FromPicker(color, field.ValueType));
        }

        return true;
    }
}
