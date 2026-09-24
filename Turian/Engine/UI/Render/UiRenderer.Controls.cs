namespace Turian.Engine.UI;

public sealed partial class UiRenderer
{
    void RenderLabel(Gui gui, UiElement el)
    {
        var text = ResolveText(el) ?? string.Empty;
        UiValue.TryFloat(Style(gui, el, "font-size"), out var size);
        var hasColor = UiValue.TryColor(Style(gui, el, "color"), out var color);
        var effects = UiTextEffects.Resolve(name => Style(gui, el, name));
        gui.DrawText(text, size, hasColor ? color : null, font: ResolveFont(gui, el, size), effects: effects);
    }

    /// <summary>The font a text leaf should draw with: the <c>font</c> attribute or the
    /// <c>font-family</c> / <c>font</c> style declaration, resized to <paramref name="size"/>.</summary>
    Font? ResolveFont(Gui gui, UiElement el, float size)
    {
        if (FontResolver is null) return null;

        var reference = Attr(el, "font")
                        ?? NonEmpty(Style(gui, el, "font-family"))
                        ?? NonEmpty(Style(gui, el, "font"));
        if (reference is null) return null;

        var font = FontResolver(reference);
        return font is not null && size > 0f ? font.WithSize(size) : font;
    }

    static string? NonEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// A button is a styled node with a label: <see cref="Gui.StyledNode"/> already paints the box and
    /// re-resolves it for <c>:hover</c> and <c>:active</c>, so the whole appearance comes from the
    /// document's <c>.uss</c> rather than from a fixed control palette.
    /// </summary>
    void RenderButton(Gui gui, UiElement el)
    {
        var text = ResolveText(el) ?? string.Empty;
        var node = gui.StyledNode(el.Tag, el.Classes, el.Name);
        ApplyAttributeSize(node, el);
        UiStyleApplier.Apply(node, el.InlineStyle);

        using (node.Enter())
        {
            LogRect(gui, el, node);
            DrawBackground(gui, el);

            var clicked = gui.Pass == Pass.Pass2Render && gui.GetInteractable().OnClick();
            DrawLabel(gui, el, text);

            if (clicked) Fire(el, "click");
        }
    }

    /// <summary>Draws an element's own text with the font size, color and effects its style resolves.</summary>
    void DrawLabel(Gui gui, UiElement el, string text)
    {
        if (text.Length == 0) return;

        UiValue.TryFloat(Style(gui, el, "font-size"), out var size);
        var hasColor = UiValue.TryColor(Style(gui, el, "color"), out var color);
        gui.DrawText(text, size, hasColor ? color : null, font: ResolveFont(gui, el, size),
            effects: UiTextEffects.Resolve(name => Style(gui, el, name)));
    }

    void RenderImage(Gui gui, UiElement el)
    {
        var image = ResolveImage(el, "src");
        UiValue.TryLength(Style(gui, el, "width"), out var w, out _);
        UiValue.TryLength(Style(gui, el, "height"), out var h, out _);

        if (image is not null)
        {
            gui.Image(image, w > 0 ? w : -1, h > 0 ? h : -1);
            return;
        }

        using (gui.Node(w > 0 ? w : 32, h > 0 ? h : 32).Enter()) { }
    }

    void RenderImageButton(Gui gui, UiElement el)
    {
        var normal = ResolveImage(el, "image-normal");
        if (normal is null)
        {
            RenderContainer(gui, el);
            return;
        }

        var hover = ResolveImage(el, "image-hover") ?? normal;
        var pressed = ResolveImage(el, "image-pressed") ?? normal;
        var disabled = ResolveImage(el, "image-disabled") ?? normal;
        var slice = ParseInsets(Attr(el, "nine-slice"));
        var enabled = !UiValue.TryBool(Attr(el, "enabled"), out var en) || en;

        var node = gui.StyledNode(el.Tag, el.Classes, el.Name);
        ApplyAttributeSize(node, el);
        UiStyleApplier.Apply(node, el.InlineStyle);

        using (node.Enter())
        {
            LogRect(gui, el, node);

            if (gui.Pass != Pass.Pass2Render)
            {
                DrawLabel(gui, el, ResolveText(el) ?? string.Empty);
                return;
            }

            var interactable = gui.GetInteractable();
            var image = !enabled ? disabled
                : interactable.OnHold() ? pressed
                : interactable.OnHover() ? hover
                : normal;

            if (slice is { } insets) gui.DrawImageNineSlice(image, node.Rect, insets);
            else gui.DrawImage(image, node.Rect);

            var clicked = enabled && interactable.OnClick();
            DrawLabel(gui, el, ResolveText(el) ?? string.Empty);

            if (clicked) Fire(el, "click");
        }
    }

    void RenderTextField(Gui gui, UiElement el)
    {
        var key = StateKey(el, "value");
        var (bound, boundValue) = BoundValue(el, "value");
        var current = bound
            ? boundValue?.ToString() ?? string.Empty
            : state.TryGetValue(key, out var v) && v is string s
                ? s
                : Attr(el, "value") ?? string.Empty;

        var placeholder = ResolveTextAttribute(el, "placeholder") ?? string.Empty;
        var enabled = !UiValue.TryBool(Attr(el, "enabled"), out var en) || en;

        // The behavior — caret, selection, clipboard — is Guinevere's; only the paint is ours. The
        // state is fetched before the node so last frame's focus can select the styling for this one.
        var edit = TextEditor.State(gui, FieldId(el), current);

        var node = gui.StyledNode(el.Tag, WithClass(el.Classes, "focused", edit.IsFocused), el.Name);
        ApplyAttributeSize(node, el);
        UiStyleApplier.Apply(node, el.InlineStyle);

        string next;
        using (node.Enter())
        {
            LogRect(gui, el, node);
            DrawBackground(gui, el);

            if (enabled) TextEditor.Process(gui, edit, gui.GetInteractable(), FontSize(gui, el));
            else edit.IsFocused = false;

            DrawFieldText(gui, el, edit, placeholder);
            next = edit.Text;
        }

        if (!string.Equals(next, current, StringComparison.Ordinal))
        {
            WriteBinding(el, "value", next);
            Fire(el, "value-changed");
        }

        state[key] = next;
    }

    /// <summary>Paints a field's selection, its value (or placeholder) and its caret.</summary>
    void DrawFieldText(Gui gui, UiElement el, TextEditState edit, string placeholder)
    {
        var size = FontSize(gui, el);
        var hasColor = UiValue.TryColor(Style(gui, el, "color"), out var color);
        var ink = hasColor ? color : GuiColor.White;

        if (gui.Pass == Pass.Pass2Render && edit.IsFocused && edit.HasSelection)
        {
            var font = TextEditor.MeasuringFont(gui, size);
            var inner = gui.CurrentNode.InnerRect;
            var origin = TextEditor.TextOriginX(gui, font, edit.Text, inner);
            var from = origin + TextEditor.MeasureWidth(font, edit.Text[..edit.SelectionStart]);
            var to = origin + TextEditor.MeasureWidth(font, edit.Text[..edit.SelectionEnd]);

            var selection = UiValue.TryColor(Style(gui, el, "selection-color"), out var sc)
                ? sc
                : GuiColor.FromArgb(110, ink);
            gui.DrawRect(new Rect(from, inner.Y, Math.Max(1f, to - from), inner.H), selection);
        }

        var empty = edit.Text.Length == 0;
        var shown = empty ? placeholder : edit.Text;
        var shownColor = empty && UiValue.TryColor(Style(gui, el, "placeholder-color"), out var pc) ? pc : ink;

        if (shown.Length > 0)
            gui.DrawText(shown, size, shownColor, font: ResolveFont(gui, el, size), centerInRect: false);

        if (gui.Pass != Pass.Pass2Render || !edit.IsFocused || !edit.ShowCursor) return;

        var caretFont = TextEditor.MeasuringFont(gui, size);
        var rect = gui.CurrentNode.InnerRect;
        var caretX = TextEditor.TextOriginX(gui, caretFont, edit.Text, rect)
                     + TextEditor.MeasureWidth(caretFont, edit.Text[..Math.Min(edit.CursorPosition, edit.Text.Length)]);

        gui.DrawRect(new Rect(caretX, rect.Y, 1.5f, rect.H), ink);
    }

    string FieldId(UiElement el) =>
        el.Name is { Length: > 0 } name ? $"UiTextField:{name}" : $"UiTextField:#{autoKey}";

    float FontSize(Gui gui, UiElement el)
    {
        UiValue.TryFloat(Style(gui, el, "font-size"), out var size);
        return size > 0f ? size : defaultFontSize;
    }

    /// <summary>Font size used when a field's style names none.</summary>
    const float defaultFontSize = 14f;

    void RenderToggle(Gui gui, UiElement el)
    {
        var key = StateKey(el, "value");
        var (bound, boundValue) = BoundValue(el, "value");
        var on = bound
            ? boundValue is true
            : state.TryGetValue(key, out var v) && v is bool b
                ? b
                : UiValue.TryBool(Attr(el, "value"), out var attrOn) && attrOn;

        var classes = WithClass(el.Classes, "checked", on);
        var node = gui.StyledNode(el.Tag, classes, el.Name).Direction(Axis.Horizontal).Gap(markGap);
        ApplyAttributeSize(node, el);
        UiStyleApplier.Apply(node, el.InlineStyle);

        var next = on;
        using (node.Enter())
        {
            LogRect(gui, el, node);
            DrawBackground(gui, el);

            var clicked = gui.Pass == Pass.Pass2Render && gui.GetInteractable().OnClick();
            DrawToggleMark(gui, el, on);
            DrawLabel(gui, el, ResolveTextAttribute(el, "label") ?? string.Empty);

            if (clicked) next = !on;
        }

        if (next != on)
        {
            WriteBinding(el, "value", next);
            Fire(el, "value-changed");
        }

        state[key] = next;
    }

    /// <summary>Gap between a toggle's box and its label.</summary>
    const float markGap = 6f;

    /// <summary>
    /// The toggle's box, drawn rather than styled as its own node: a document with no sheet still gets
    /// a visible control, while <c>border-color</c> and <c>color</c> let one restyle it.
    /// </summary>
    void DrawToggleMark(Gui gui, UiElement el, bool on)
    {
        var size = FontSize(gui, el);

        using (gui.Node(size, size, "toggle/mark").Enter())
        {
            if (gui.Pass != Pass.Pass2Render) return;

            var ink = UiValue.TryColor(Style(gui, el, "color"), out var c) ? c : GuiColor.White;
            var outline = UiValue.TryColor(Style(gui, el, "border-color"), out var b) ? b : ink;
            var rect = gui.CurrentNode.Rect;

            gui.DrawRectBorder(rect, outline, 1f, 2f);

            if (!on) return;

            var inset = size * 0.25f;
            gui.DrawRect(new Rect(rect.X + inset, rect.Y + inset, rect.W - (inset * 2f), rect.H - (inset * 2f)),
                ink, 1f);
        }
    }

    // ── templates & lists ────────────────────────────────────────────────────

    void RenderInstance(Gui gui, UiInstanceRef instance)
    {
        if (!document.Templates.TryGetValue(instance.TemplateName, out var template))
        {
            gui.DrawText($"<missing template '{instance.TemplateName}'>", 12, GuiColor.Red);
            return;
        }

        var cacheKey = instance.TemplateName + "|" +
                       string.Join(";", instance.Parameters.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

        if (!expandedTemplates.TryGetValue(cacheKey, out var expanded))
        {
            var raw = template.RawXml;
            foreach (var (name, value) in instance.Parameters)
                raw = raw.Replace("$" + name, value, StringComparison.Ordinal);

            expanded = UiXmlParser.ParseFragment(raw);
            expandedTemplates[cacheKey] = expanded;
        }

        RenderElement(gui, expanded);
    }

    void RenderRepeat(Gui gui, UiElement el, UiRepeat repeat)
    {
        if (DataResolver?.Invoke(repeat.ItemsPath) is not IEnumerable items || items is string)
            return;

        foreach (var item in items)
        {
            repeatScope.Push((repeat.ItemAlias, item));
            try
            {
                foreach (var child in el.Children)
                    RenderElement(gui, child);
            }
            finally
            {
                repeatScope.Pop();
            }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

}
