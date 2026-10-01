namespace Turian.Engine.UI;

public sealed partial class UiRenderer
{
    void DrawBackground(Gui gui, UiElement el)
    {
        var (bgBound, bgValue) = BoundValue(el, "background-color");
        string bg;
        if (bgBound)
        {
            bg = bgValue?.ToString() ?? string.Empty;
        }
        else if (!el.InlineStyle.TryGetValue("background-color", out bg!))
        {
            return;
        }
        else if (BindingExpression.IsBinding(bg))
        {
            bg = ResolveBinding(BindingExpression.Parse(bg))?.ToString() ?? string.Empty;
        }

        if (!UiValue.TryColor(bg, out var color))
            return;

        var radius = 0f;
        if (el.InlineStyle.TryGetValue("border-radius", out var r))
            UiValue.TryFloat(r, out radius);

        gui.DrawBackgroundRect(color, radius);
    }

    string? ResolveText(UiElement el)
    {
        var bound = el.AttributeBindings.FirstOrDefault(b => b.TargetAttribute is "text");
        var source = bound is not null
            ? ResolveBinding(bound.Expression)?.ToString() ?? string.Empty
            : Attr(el, "text") ?? el.Text;

        var textResolver = TextResolver;
        if (textResolver is null) return source;

        var key = Attr(el, "text-key");
        return textResolver(string.IsNullOrEmpty(key) ? null : key, source);
    }

    /// <summary>
    /// The localized value of a text attribute such as <c>label</c>, <c>placeholder</c> or
    /// <c>header</c>: an explicit <c>&lt;attribute&gt;-key</c> when one is named, otherwise the
    /// attribute's own text as its key.
    /// </summary>
    string? ResolveTextAttribute(UiElement el, string attribute)
    {
        var source = Attr(el, attribute);
        if (source is null) return null;
        var textResolver = TextResolver;
        if (textResolver is null) return source;

        var key = Attr(el, $"{attribute}-key");
        return textResolver(string.IsNullOrEmpty(key) ? null : key, source);
    }

    /// <summary>The bound value for <paramref name="attribute"/> on <paramref name="el"/>, or <c>null</c> if unbound.</summary>
    (bool Bound, object? Value) BoundValue(UiElement el, string attribute)
    {
        var b = el.AttributeBindings.FirstOrDefault(x => x.TargetAttribute == attribute);
        return b is null ? (false, null) : (true, ResolveBinding(b.Expression));
    }

    object? ResolveBinding(BindingExpression expr)
    {
        var value = ResolvePath(expr.Path);
        var converter = ValueConverters.Get(expr.Converter);
        return converter is null ? value : converter.Convert(value);
    }

    void WriteBinding(UiElement el, string attribute, object? value)
    {
        var b = el.AttributeBindings.FirstOrDefault(x => x.TargetAttribute == attribute);
        if (b is null || b.Expression.Mode != BindingMode.TwoWay || bindingContext is null) return;

        var converter = ValueConverters.Get(b.Expression.Converter);
        bindingContext.TrySet(b.Expression.Path, converter is null ? value : converter.ConvertBack(value));
    }

    object? ResolvePath(string path)
    {
        foreach (var (alias, item) in repeatScope)
        {
            if (path == alias) return item;
            if (path.StartsWith(alias + ".", StringComparison.Ordinal) && item is not null)
                return MemberPath.Read(item, path[(alias.Length + 1)..]);
        }

        return DataResolver?.Invoke(path);
    }

    SKImage? ResolveImage(UiElement el, string attribute)
    {
        var src = Attr(el, attribute)
                  ?? (el.InlineStyle.TryGetValue(attribute, out var s) ? s : null);
        return string.IsNullOrEmpty(src) ? null : ImageResolver?.Invoke(src);
    }

    void Fire(UiElement el, string eventName)
    {
        if (el.Events.TryGetValue(eventName, out var method) && Handlers.TryGetValue(method, out var action))
            action();
    }

    static string? Attr(UiElement el, string name) =>
        el.Attributes.TryGetValue(name, out var v) ? v : null;

    /// <summary>
    /// The effective value of style property <paramref name="name"/> for a leaf element: the inline
    /// <c>style="…"</c> declaration if present, otherwise the value the active <c>.uss</c> sheets
    /// resolve for the element's type / classes / id. Containers get this through
    /// <see cref="Gui.StyledNode"/>; leaves (text, buttons, fields) have no node and read it here.
    /// </summary>
    string Style(Gui gui, UiElement el, string name)
    {
        if (el.InlineStyle.TryGetValue(name, out var inline))
            return inline;

        if (gui.StyleSheets.Count == 0)
            return string.Empty;

        return gui.ResolveStyle(el.Tag, el.Classes, el.Name).Get(name) ?? string.Empty;
    }

    string StateKey(UiElement el, string kind) =>
        el.Name is { Length: > 0 } name ? $"{name}:{kind}" : $"#{autoKey++}:{kind}";

    static Insets? ParseInsets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var n = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => UiValue.TryFloat(p, out var f) ? f : 0f)
            .ToArray();

        return n.Length switch
        {
            1 => new Insets(n[0]),
            2 => new Insets(n[0], n[1]),
            4 => new Insets(n[0], n[1], n[2], n[3]),
            _ => null,
        };
    }
}
