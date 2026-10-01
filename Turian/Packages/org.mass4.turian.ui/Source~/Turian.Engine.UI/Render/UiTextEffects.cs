namespace Turian.Engine.UI;

/// <summary>
/// Builds a Guinevere <see cref="TextEffects"/> from <c>.uss</c> text-effect declarations:
/// <list type="bullet">
///   <item><c>text-outline: &lt;color&gt; &lt;width&gt;</c></item>
///   <item><c>text-shadow: &lt;color&gt; &lt;offsetX&gt; &lt;offsetY&gt; [blur]</c></item>
///   <item><c>text-inner-shadow: &lt;color&gt; &lt;offsetX&gt; &lt;offsetY&gt; [blur]</c></item>
///   <item><c>text-gradient: linear(&lt;from&gt;, &lt;to&gt;, [angleDeg]) | radial(&lt;from&gt;, &lt;to&gt;)</c></item>
/// </list>
/// </summary>
public static class UiTextEffects
{
    /// <summary>Resolves the four text-effect declarations, or <c>null</c> when none are set.</summary>
    /// <param name="style">Looks up a style property value; returns <c>""</c> when unset.</param>
    public static TextEffects? Resolve(Func<string, string> style)
    {
        ArgumentNullException.ThrowIfNull(style);

        var outline = ParseOutline(style("text-outline"));
        var drop = ParseShadow(style("text-shadow"));
        var inner = ParseShadow(style("text-inner-shadow"));
        var gradient = ParseGradient(style("text-gradient"));

        if (outline is null && drop is null && inner is null && gradient is null)
            return null;

        return new TextEffects
        {
            Outline = outline,
            DropShadow = drop,
            InnerShadow = inner,
            Gradient = gradient,
        };
    }

    static TextEffects.TextOutline? ParseOutline(string value)
    {
        var parts = Tokenize(value);
        if (parts.Length < 2 || !UiValue.TryColor(parts[0], out var color) || !UiValue.TryFloat(parts[1], out var width))
            return null;
        return new TextEffects.TextOutline(color, width);
    }

    static TextEffects.TextShadow? ParseShadow(string value)
    {
        var parts = Tokenize(value);
        if (parts.Length < 3 || !UiValue.TryColor(parts[0], out var color)
            || !UiValue.TryFloat(parts[1], out var dx) || !UiValue.TryFloat(parts[2], out var dy))
        {
            return null;
        }

        var blur = parts.Length >= 4 && UiValue.TryFloat(parts[3], out var b) ? b : 0f;
        return new TextEffects.TextShadow(color, new Vector2(dx, dy), blur);
    }

    static TextEffects.TextGradient? ParseGradient(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        var radial = trimmed.StartsWith("radial", StringComparison.OrdinalIgnoreCase);
        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open < 0) return null;

        var inside = trimmed[(open + 1)..];
        var close = inside.IndexOf(')', StringComparison.Ordinal);
        if (close < 0) return null;

        var args = inside[..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (args.Length < 2 || !UiValue.TryColor(args[0], out var from) || !UiValue.TryColor(args[1], out var to))
            return null;

        var angle = args.Length >= 3 && UiValue.TryFloat(args[2], out var a) ? a : 0f;
        return new TextEffects.TextGradient(from, to, angle, radial);
    }

    static string[] Tokenize(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
