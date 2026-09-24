namespace Turian.Engine.UI;

/// <summary>
/// Applies a subset of CSS/USS layout declarations from a <c>.ui</c> inline <c>style</c> onto a
/// Guinevere <see cref="LayoutNode"/>. Class-based <c>.uss</c> resolution is a separate step; this
/// only handles literal inline declarations.
/// </summary>
public static class UiStyleApplier
{
    /// <summary>Applies every recognised declaration in <paramref name="style"/> to <paramref name="node"/>.</summary>
    /// <param name="node">The layout node being configured (must be in the build pass).</param>
    /// <param name="style">Parsed <c>prop → value</c> declarations.</param>
    public static void Apply(LayoutNode node, IReadOnlyDictionary<string, string> style)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(style);

        foreach (var (prop, raw) in style)
        {
            var value = raw.Trim();
            switch (prop)
            {
                case "flex-direction":
                    node.Direction(value is "row" or "row-reverse" ? Axis.Horizontal : Axis.Vertical);
                    break;

                case "gap":
                    if (UiValue.TryFloat(value, out var gap)) node.Gap(gap);
                    break;

                case "flex-grow":
                    if (UiValue.TryFloat(value, out var grow) && grow > 0f) node.Expand();
                    break;

                case "align-self":
                    node.AlignSelf(AlignFraction(value));
                    break;

                case "align-items":
                    node.ContentAlignX(AlignFraction(value));
                    break;

                case "justify-content":
                    node.ContentAlignY(AlignFraction(value));
                    break;

                case "width":
                    ApplyLength(value, node.Width, node.WidthPercent);
                    break;
                case "height":
                    ApplyLength(value, node.Height, node.HeightPercent);
                    break;
                case "min-width":
                    if (UiValue.TryLength(value, out var mnw, out _)) node.MinWidth(mnw);
                    break;
                case "max-width":
                    if (UiValue.TryLength(value, out var mxw, out _)) node.MaxWidth(mxw);
                    break;
                case "min-height":
                    if (UiValue.TryLength(value, out var mnh, out _)) node.MinHeight(mnh);
                    break;
                case "max-height":
                    if (UiValue.TryLength(value, out var mxh, out _)) node.MaxHeight(mxh);
                    break;

                case "padding":
                    ApplyBox(value, node.Padding, node.Padding, node.Padding);
                    break;
                case "margin":
                    ApplyBox(value, node.Margin, node.Margin, node.Margin);
                    break;
            }
        }
    }

    static void ApplyLength(string value, Func<float, LayoutNode> px, Func<float, LayoutNode> percent)
    {
        if (!UiValue.TryLength(value, out var v, out var isPercent)) return;
        if (isPercent) percent(v);
        else px(v);
    }

    static void ApplyBox(
        string value,
        Func<float, LayoutNode> all,
        Func<float, float, LayoutNode> hv,
        Func<float, float, float, float, LayoutNode> tblr)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var n = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!UiValue.TryLength(parts[i], out n[i], out _))
                return;

        switch (parts.Length)
        {
            case 1: all(n[0]); break;
            case 2: hv(n[1], n[0]); break; // CSS order is vertical horizontal; Guinevere wants (h, v)
            case 4: tblr(n[0], n[1], n[2], n[3]); break;
        }
    }

    static float AlignFraction(string value) => value switch
    {
        "flex-start" or "start" or "left" or "top" => 0f,
        "center" or "middle" => 0.5f,
        "flex-end" or "end" or "right" or "bottom" => 1f,
        _ => 0f,
    };
}
