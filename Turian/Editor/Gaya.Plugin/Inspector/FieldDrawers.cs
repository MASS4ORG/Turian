namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws a <see cref="FormField"/> as an editor chosen by its value type. A type with a
/// <see cref="CustomEditorAttribute"/>-registered <see cref="IValueEditor"/> draws through that
/// editor; the primitive types retain the switches in <see cref="DrawEditor"/>. The layout
/// primitives editors share — a labelled <see cref="Row"/>, a scrub <see cref="TryEdit"/> — live
/// here, so an editor stays a few calls over the same look the built-in rows have.
/// </summary>
static class FieldDrawers
{
    static StudioTheme Theme => StudioTheme.Current;

    static float LabelWidth => Theme.Scale(96f);
    static float RowHeight => Theme.Scale(Theme.RowHeight);

    static GuiColor Ink => Theme.Ink;
    static GuiColor InkDim => Theme.InkDim;
    static GuiColor Field => Theme.Field;
    static GuiColor Border => Theme.Border;

    /// <summary>Draws one labeled row for a field, or several for a composite like a transform.</summary>
    /// <param name="gui">The GUI instance.</param>
    /// <param name="field">The member to edit.</param>
    /// <param name="id">A unique id for this row's controls.</param>
    /// <param name="references">Draws reference fields, or null to fall back to a read-only summary.</param>
    /// <param name="collapsed">Ids of the collection groups the user has folded shut.</param>
    public static void Draw(Gui gui, FormField field, string id, ReferenceDrawer? references = null,
        ISet<string>? collapsed = null)
    {
        var type = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;

        if (references is not null && ReferenceField.IsReference(field))
        {
            Row(gui, field.Label, id, () => references.TryDraw(gui, field, id));
            return;
        }

        // A type with a registered editor owns its whole appearance — a vector is one labeled row,
        // a transform is three. A read-only value skips the editor and shows a text summary below.
        if (!field.IsReadOnly && ValueEditorRegistry.For(type) is { } customEditor)
        {
            customEditor.Draw(gui, field, id);
            return;
        }

        if (CollectionField.TryCreate(field) is { } collection)
        {
            DrawCollection(gui, collection, id, references, collapsed);
            return;
        }

        if (Nested(field, type) is { } nested)
        {
            DrawNested(gui, field, nested, id, references, collapsed);
            return;
        }

        Row(gui, field.Label, id, () => DrawEditor(gui, field, type, id),
            IsNumeric(type) && !field.IsReadOnly ? () => ScrubLabel(gui, field, type) : null);
    }

    /// <summary>
    /// Draws only the value editor, for a caller that lays out the label itself — the settings panel
    /// puts the title and its description above the control rather than beside it.
    /// </summary>
    /// <param name="gui">The GUI instance.</param>
    /// <param name="field">The member to edit.</param>
    /// <param name="id">A unique id for this editor's controls.</param>
    public static void DrawEditorOnly(Gui gui, FormField field, string id) =>
        DrawEditor(gui, field, Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType, id);

    /// <summary>A label on the left and whatever the caller draws filling the rest.</summary>
    internal static void Row(Gui gui, string label, string id, Action editor, Action? labelInteraction = null)
    {
        using (gui.Node(-1, RowHeight, id).ExpandWidth().Direction(Axis.Horizontal).Gap(6f).Enter())
        {
            using (gui.Node(LabelWidth, RowHeight, $"{id}/label").Enter())
            {
                var labelHot = labelInteraction is not null && gui.Pass == Pass.Pass2Render
                    && gui.GetInteractable().OnHover();
                if (labelInteraction is not null && gui.Pass == Pass.Pass2Render)
                    labelInteraction();
                gui.DrawText(label, Theme.Text(12), labelHot ? Ink : InkDim, centerInRect: false);
            }

            using (gui.Node(-1, RowHeight, $"{id}/editor").Expand().Direction(Axis.Horizontal).Gap(4f).Enter())
                editor();
        }
    }

    static void ScrubLabel(Gui gui, FormField field, Type type)
    {
        if (!gui.GetInteractable().OnDrag(out var drag)) return;

        var current = Convert.ToDouble(field.GetValue() ?? 0, CultureInfo.InvariantCulture);
        var delta = (drag.FrameDelta.X - drag.FrameDelta.Y) * (IsIntegral(type) ? 1 : 0.01);
        var (min, max) = field.Range ?? TypeRange(type);
        var next = Math.Clamp(current + delta, min, max);
        if (Math.Abs(next - current) > double.Epsilon)
            field.SetValue(ToNumber(next, type));
    }

    /// <summary>
    /// The member's own bounds when it has no <see cref="RangeAttribute"/>. Scrubbing a field at its
    /// type's edge saturates here instead of overflowing — an unsigned dragged past MinValue must
    /// clamp, not throw the frame away.
    /// </summary>
    static readonly Dictionary<Type, (double Min, double Max)> typeRanges = new()
    {
        [typeof(byte)] = (byte.MinValue, byte.MaxValue),
        [typeof(sbyte)] = (sbyte.MinValue, sbyte.MaxValue),
        [typeof(short)] = (short.MinValue, short.MaxValue),
        [typeof(ushort)] = (ushort.MinValue, ushort.MaxValue),
        [typeof(int)] = (int.MinValue, int.MaxValue),
        [typeof(uint)] = (uint.MinValue, uint.MaxValue),
        [typeof(long)] = (long.MinValue, long.MaxValue),
        [typeof(ulong)] = (ulong.MinValue, ulong.MaxValue),
        [typeof(float)] = (float.MinValue, float.MaxValue),
        [typeof(double)] = (double.MinValue, double.MaxValue),
        [typeof(decimal)] = ((double)decimal.MinValue, (double)decimal.MaxValue),
    };

    static (double Min, double Max) TypeRange(Type type) =>
        typeRanges.TryGetValue(type, out var range) ? range : (float.MinValue, float.MaxValue);

    static void DrawEditor(Gui gui, FormField field, Type type, string id)
    {
        if (field.IsReadOnly)
        {
            gui.DrawText(Text(field.GetValue()), Theme.Text(12), InkDim, centerInRect: false);
            return;
        }

        // A registered editor's value-only form, for settings rows whose label is drawn elsewhere.
        if (ValueEditorRegistry.For(type) is { } customEditor && customEditor.DrawValue(gui, field, id)) return;

        if (type == typeof(bool)) DrawBool(gui, field);
        else if (type == typeof(string)) DrawString(gui, field, id);
        else if (type.IsEnum) DrawEnum(gui, field, type, id);
        else if (IsNumeric(type)) DrawNumber(gui, field, type, id);
        else gui.DrawText(Text(field.GetValue()), Theme.Text(12), InkDim, centerInRect: false);
    }

    static void DrawBool(Gui gui, FormField field)
    {
        // Checkbox builds its own nodes, so it has to run in both passes, or it never gets a rect.
        var current = field.GetValue() is true;
        var next = gui.Checkbox(current, size: Theme.Scale(14f));

        if (gui.Pass == Pass.Pass2Render && next != current) field.SetValue(next);
    }

    static void DrawString(Gui gui, FormField field, string id)
    {
        var current = field.GetValue() as string ?? string.Empty;
        var next = Input(gui, current, $"{id}/text", width: 0);

        if (!string.Equals(next, current, StringComparison.Ordinal)) field.SetValue(next);
    }

    static void DrawEnum(Gui gui, FormField field, Type type, string id)
    {
        var names = Enum.GetNames(type);
        var labels = names.Select(name => EnumLabel(type, name)).ToArray();
        var current = Array.IndexOf(names, field.GetValue()?.ToString() ?? string.Empty);

        // Dropdown keys its open state by call site, so every enum field would otherwise share one.
        // ReSharper disable once ExplicitCallerInfoArgument
        var next = gui.Dropdown(labels, current, width: 0, height: RowHeight, fontSize: Theme.Text(12),
            backgroundColor: Field, borderColor: Border, textColor: Ink, dropdownColor: Field,
            filePath: $"{id}/enum");

        if (next >= 0 && next != current) field.SetValue(Enum.Parse(type, names[next]));
    }

    /// <summary>
    /// A member's <see cref="EnumLabelAttribute"/> label, for a name that should not be shown as
    /// written — a language's own endonym, say — or its own name otherwise.
    /// </summary>
    static string EnumLabel(Type type, string name) =>
        type.GetField(name)?.GetCustomAttribute<EnumLabelAttribute>()?.Label ?? name;

    static void DrawNumber(Gui gui, FormField field, Type type, string id)
    {
        var current = Convert.ToDouble(field.GetValue() ?? 0, CultureInfo.InvariantCulture);
        var (min, max) = field.Range ?? (float.MinValue, float.MaxValue);

        if (!TryEdit(gui, current, $"{id}/num", IsIntegral(type), min, max, out var parsed)) return;

        field.SetValue(ToNumber(parsed, type));
    }

    /// <summary>An edited number as the member's own type, saturating at the type's limits.</summary>
    static object ToNumber(double value, Type type)
    {
        try
        {
            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return type.GetField(value < 0 ? "MinValue" : "MaxValue")!.GetValue(null)!;
        }
    }

    /// <summary>
    /// Every number is a scrub field: drag it to change the value, click it to type one. The step is
    /// per-pixel, so an integer moves a unit at a time and a float moves in hundredths. Numerals are
    /// right-aligned, and edits round-trip through double so a long, a decimal or an unsigned that a
    /// float cannot hold exactly keeps its digits.
    /// </summary>
    internal static bool TryEdit(Gui gui, double value, string id, bool integral,
        float min, float max, out double result)
    {
        var edited = value;
        gui.NumberField(ref edited, integral ? 1d : 0.01d, min, max, width: 0, height: RowHeight,
            format: integral ? "0" : "0.###", backgroundColor: Field, borderColor: Border, textColor: Ink,
            fontSize: Theme.Text(12), padding: 4, id: id, alignX: 1f);

        result = edited;
        return !edited.Equals(value);
    }

    static string Input(Gui gui, string text, string id, float width, float alignX = 0f) =>
        gui.TextInput(text, width: width, height: RowHeight, fontSize: Theme.Text(12),
            backgroundColor: Field, borderColor: Border, textColor: Ink, padding: 4, id: id,
            alignX: alignX);

    static bool IsIntegral(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
        || type == typeof(sbyte) || type == typeof(uint)
        || type == typeof(ulong) || type == typeof(ushort);

    static bool IsNumeric(Type type) =>
        type == typeof(float) || type == typeof(double) || type == typeof(decimal)
        || type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
        || type == typeof(sbyte) || type == typeof(uint) || type == typeof(ushort) || type == typeof(ulong);

    static string Text(object? value) => value?.ToString() ?? "—";

    /// <summary>
    /// A list, array or dictionary as a foldable group of rows, each entry drawn by the same drawers
    /// a member gets. A resizable collection also offers add and remove.
    /// </summary>
    static void DrawCollection(Gui gui, CollectionField collection, string id,
        ReferenceDrawer? references, ISet<string>? collapsed)
    {
        var entries = collection.Entries();
        var isOpen = collapsed?.Contains(id) != true;

        using (gui.Node(-1, RowHeight, $"{id}/head").ExpandWidth().Direction(Axis.Horizontal).Gap(6f).Enter())
        {
            using (gui.Node(LabelWidth, RowHeight, $"{id}/head/label").Direction(Axis.Horizontal).Gap(2f).Enter())
            {
                if (gui.Pass == Pass.Pass2Render && gui.GetInteractable().OnClick() && collapsed is not null)
                    if (!collapsed.Add(id)) collapsed.Remove(id);

                using (gui.Node(10f, RowHeight, $"{id}/head/arrow").ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
                    DrawArrow(gui, isOpen);

                gui.DrawText(collection.Label, Theme.Text(12), InkDim, centerInRect: false);
            }

            using (gui.Node(-1, RowHeight, $"{id}/head/actions").Expand()
                       .Direction(Axis.Horizontal).Gap(4f).ContentAlignY(0.5f).Enter())
            {
                gui.DrawText($"{entries.Count}", Theme.Text(11), InkDim, centerInRect: false);
                if (collection.CanResize && SmallButton(gui, "+", $"{id}/add")) collection.Add();
            }
        }

        if (!isOpen) return;

        for (var i = 0; i < entries.Count; i++)
        {
            using (gui.Node(-1, -1, $"{id}/entry{i}").ExpandWidth().Direction(Axis.Horizontal).Gap(4f).Enter())
            {
                using (gui.Node(-1, -1, $"{id}/entry{i}/value").Expand().Direction(Axis.Vertical).Enter())
                    Draw(gui, entries[i], $"{id}/entry{i}", references, collapsed);

                // Removing shifts every later entry, so the remaining ones this frame no longer
                // address what their handles were built for.
                if (collection.CanResize && SmallButton(gui, "-", $"{id}/entry{i}/remove"))
                {
                    collection.RemoveAt(i);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// The object a field holds when it is a plain settings-style object worth expanding — an input
    /// binding, an action map — rather than a value with an editor of its own. Engine objects are
    /// excluded: a node or an asset is referenced, never edited in place.
    /// </summary>
    static object? Nested(FormField field, Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return null;
        if (typeof(IdClass).IsAssignableFrom(type) || typeof(Asset).IsAssignableFrom(type)) return null;
        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) return null;
        if (FormBuilder.EditableMembers(type).Count == 0) return null;

        return field.GetValue();
    }

    /// <summary>
    /// A nested object as a foldable group of its own members, drawn by the same drawers a top-level
    /// member gets. What makes an input action's bindings editable without a bespoke panel.
    /// </summary>
    static void DrawNested(Gui gui, FormField field, object target, string id,
        ReferenceDrawer? references, ISet<string>? collapsed)
    {
        var isOpen = collapsed?.Contains(id) != true;

        using (gui.Node(-1, RowHeight, $"{id}/head").ExpandWidth().Direction(Axis.Horizontal).Gap(6f).Enter())
        using (gui.Node(LabelWidth, RowHeight, $"{id}/head/label").Direction(Axis.Horizontal).Gap(2f).Enter())
        {
            if (gui.Pass == Pass.Pass2Render && gui.GetInteractable().OnClick() && collapsed is not null)
                if (!collapsed.Add(id)) collapsed.Remove(id);

            using (gui.Node(10f, RowHeight, $"{id}/head/arrow").ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
                DrawArrow(gui, isOpen);

            gui.DrawText(Summary(field, target), Theme.Text(12), InkDim, centerInRect: false);
        }

        if (!isOpen) return;

        var model = FormBuilder.Build(target, _ => field.Touch());
        var fields = model.Sections.SelectMany(section => section.BodyFields).ToList();

        using (gui.Node(-1, -1, $"{id}/body").ExpandWidth().Direction(Axis.Vertical)
                   .Margin(Theme.Scale(12f), 0f, 0f, 0f).Enter())
            for (var i = 0; i < fields.Count; i++)
                Draw(gui, fields[i], $"{id}/f{i}", references, collapsed);
    }

    /// <summary>
    /// The row's heading: a nested object inside a collection has no label of its own, so its own
    /// <c>Name</c> or <c>Path</c> stands in and the list reads as what it holds.
    /// </summary>
    static string Summary(FormField field, object target) =>
        target.GetType().GetProperty("Name")?.GetValue(target) as string is { Length: > 0 } name ? name
        : target.GetType().GetProperty("Path")?.GetValue(target) as string is { Length: > 0 } path ? path
        : field.Label;

    /// <summary>
    /// A full-width labeled button running a <c>[Button]</c> method, sized like a row so it reads as
    /// Unity's inspector button. Blocks input so a press never falls through to the section behind it.
    /// </summary>
    internal static bool TextButton(Gui gui, string label, string id)
    {
        using (gui.Node(-1, RowHeight, id).ExpandWidth().BlockInput().ContentAlignX(0.5f)
                   .ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render) gui.DrawBackgroundRect(hot ? Border : Field, 3f);

            gui.DrawText(label, Theme.Text(12), Ink);

            return gui.Pass == Pass.Pass2Render && hot && interactable.OnClick();
        }
    }

    /// <summary>A square glyph button sized to a row. Blocks input, so a click never also reaches the clickable
    /// row or section header behind it.
    /// </summary>
    internal static bool SmallButton(Gui gui, string glyph, string id)
    {
        using (gui.Node(RowHeight, RowHeight, id).BlockInput().Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(Border, 3f);

            gui.DrawText(glyph, Theme.Text(13), hot ? Ink : InkDim);

            return gui.Pass == Pass.Pass2Render && hot && interactable.OnClick();
        }
    }

    /// <summary>A fold arrow: closed points right, open points down.</summary>
    internal static void DrawArrow(Gui gui, bool isOpen) =>
        gui.DrawText(isOpen ? "▼" : "▶", Theme.Text(9), InkDim);
}
