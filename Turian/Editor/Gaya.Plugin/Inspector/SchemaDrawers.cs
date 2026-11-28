namespace Gaya.Plugin.Turian;

/// <summary>
/// Reads the presentation hints an inspector source attached to a property. Described properties have no member to
/// reflect over, so the drawers of reflected fields cannot see their attributes.
/// </summary>
static class SchemaHints
{
    /// <summary>The first hint of a kind on the described property behind a field, or null.</summary>
    public static T? Find<T>(FormField field) where T : Attribute =>
        (field.Target as InspectorPropertyBinding)?.Property.Attributes.OfType<T>().FirstOrDefault();
}

/// <summary>Edits a described number carrying a <see cref="RangeAttribute"/> with a slider over that range.</summary>
sealed class SchemaRangeDrawer : IPropertyDrawer
{
    /// <summary>The one drawer; it keeps no state.</summary>
    public static SchemaRangeDrawer Instance { get; } = new();

    /// <summary>Whether a field is a writable described number with a range.</summary>
    public static bool Takes(FormField field)
    {
        var type = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;
        return !field.IsReadOnly && !type.IsEnum
            && Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal
            && SchemaHints.Find<RangeAttribute>(field) is not null;
    }

    /// <inheritdoc />
    public void Draw(Gui gui, FormField field, string id, FormRenderContext context) =>
        FormControls.Row(gui, field.Label, id, () => DrawValue(gui, field, id, context),
            context.IsModified?.Invoke(field) == true);

    /// <inheritdoc />
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context)
    {
        var range = SchemaHints.Find<RangeAttribute>(field)!;
        var type = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;
        var integral = Type.GetTypeCode(type) <= TypeCode.UInt64;

        // Differing selections have no value to show, so the thumb starts at the low end.
        var current = field.HasMixedValue ? null : field.GetValue();
        var value = current is null ? range.Min : Convert.ToSingle(current, CultureInfo.InvariantCulture);
        var theme = ThemeTokens.Current;
        gui.CurrentNodeScope.Set(ControlStyles.Value<ControlFontSize, float>(theme.Text(12)));
        var slider = gui.Slider(ref value, range.Min, range.Max, integral ? 1f : 0f,
            showValue: !field.HasMixedValue, id: $"{id}/slider");
        slider.Width(Size.Expand()).Height(theme.Scale(theme.RowHeight));

        if (slider.Changed)
        {
            Write(field, value, type);
        }

        return true;
    }

    // A range wider than the property's type has positions the property cannot hold; those write nothing.
    static void Write(FormField field, float value, Type type)
    {
        object converted;
        try
        {
            converted = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return;
        }

        field.SetValue(converted);
    }
}

/// <summary>Edits a described string that carries a <see cref="TextAreaAttribute"/> in a multi-line box.</summary>
sealed class SchemaTextAreaDrawer : IPropertyDrawer
{
    /// <summary>The one drawer; it keeps no state.</summary>
    public static SchemaTextAreaDrawer Instance { get; } = new();

    /// <summary>Whether a field is a writable described string asking for several lines.</summary>
    public static bool Takes(FormField field) => !field.IsReadOnly && field.ValueType == typeof(string)
        && SchemaHints.Find<TextAreaAttribute>(field) is not null;

    /// <inheritdoc />
    public void Draw(Gui gui, FormField field, string id, FormRenderContext context) =>
        FormControls.Row(gui, field.Label, id, () => DrawValue(gui, field, id, context),
            context.IsModified?.Invoke(field) == true, height: Height(field));

    /// <inheritdoc />
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context)
    {
        var current = field.HasMixedValue ? string.Empty : field.GetValue() as string ?? string.Empty;
        var next = current;
        gui.CurrentNodeScope.Set(ControlStyles.Value<ControlFontSize, float>(ThemeTokens.Current.Text(12)));
        gui.TextArea(ref next, field.HasMixedValue ? "—" : string.Empty, id: $"{id}/text")
            .Width(Size.Expand()).Height(Height(field)).Padding(4);
        if (!string.Equals(next, current, StringComparison.Ordinal))
        {
            field.SetValue(next);
        }

        return true;
    }

    // As many rows as the text has lines, within the attribute's bounds; the box scrolls past them.
    static float Height(FormField field)
    {
        var area = SchemaHints.Find<TextAreaAttribute>(field)!;
        var text = field.HasMixedValue ? string.Empty : field.GetValue() as string ?? string.Empty;
        var lines = Math.Clamp(text.Count(static c => c == '\n') + 1, area.MinLines, area.MaxLines);
        var theme = ThemeTokens.Current;
        return Math.Max(theme.Scale(theme.RowHeight), lines * theme.Text(12) * 1.4f + 8f);
    }
}
