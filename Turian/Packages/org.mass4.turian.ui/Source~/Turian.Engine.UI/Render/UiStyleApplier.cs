namespace Turian.Engine.UI;

/// <summary>
/// Applies a subset of CSS/USS layout declarations from a <c>.ui</c> inline <c>style</c> onto a
/// Guinevere <see cref="LayoutNode"/>. Class-based <c>.uss</c> resolution is a separate step; this
/// only handles literal inline declarations.
/// </summary>
public static class UiStyleApplier
{
    static readonly Dictionary<string, Action<LayoutNode, string>> declarations = new(StringComparer.Ordinal)
    {
        ["flex-direction"] = static (node, value) =>
            node.Direction(value is "row" or "row-reverse" ? Axis.Horizontal : Axis.Vertical),
        ["gap"] = static (node, value) => ApplyFloat(value, node.Gap),
        ["flex-grow"] = ApplyGrow,
        ["align-self"] = static (node, value) => node.AlignSelf(AlignFraction(value)),
        ["align-items"] = static (node, value) => node.ContentAlignX(AlignFraction(value)),
        ["justify-content"] = static (node, value) => node.ContentAlignY(AlignFraction(value)),
        ["width"] = static (node, value) => ApplyLength(value, node.Width, node.WidthPercent),
        ["height"] = static (node, value) => ApplyLength(value, node.Height, node.HeightPercent),
        ["min-width"] = static (node, value) => ApplyConstraint(value, node.MinWidth),
        ["max-width"] = static (node, value) => ApplyConstraint(value, node.MaxWidth),
        ["min-height"] = static (node, value) => ApplyConstraint(value, node.MinHeight),
        ["max-height"] = static (node, value) => ApplyConstraint(value, node.MaxHeight),
        ["padding"] = static (node, value) => ApplyBox(value, node.Padding, node.Padding, node.Padding),
        ["margin"] = static (node, value) => ApplyBox(value, node.Margin, node.Margin, node.Margin),
    };

    static readonly Dictionary<string, float> alignments = new(StringComparer.Ordinal)
    {
        ["center"] = 0.5f,
        ["middle"] = 0.5f,
        ["flex-end"] = 1f,
        ["end"] = 1f,
        ["right"] = 1f,
        ["bottom"] = 1f,
    };

    /// <summary>Applies every recognised declaration in <paramref name="style"/> to <paramref name="node"/>.</summary>
    /// <param name="node">The layout node being configured (must be in the build pass).</param>
    /// <param name="style">Parsed <c>prop → value</c> declarations.</param>
    public static void Apply(LayoutNode node, IReadOnlyDictionary<string, string> style)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(style);

        foreach (var (prop, raw) in style)
        {
            if (declarations.TryGetValue(prop, out var apply))
            {
                apply(node, raw.Trim());
            }
        }
    }

    static void ApplyFloat(string value, Func<float, LayoutNode> apply)
    {
        if (UiValue.TryFloat(value, out var number))
        {
            apply(number);
        }
    }

    static void ApplyConstraint(string value, Func<float, LayoutNode> apply)
    {
        if (UiValue.TryLength(value, out var number, out _))
        {
            apply(number);
        }
    }

    static void ApplyGrow(LayoutNode node, string value)
    {
        if (UiValue.TryFloat(value, out var grow) && grow > 0f)
        {
            node.Expand();
        }
    }

    static void ApplyLength(string value, Func<float, LayoutNode> px, Func<float, LayoutNode> percent)
    {
        if (!UiValue.TryLength(value, out var v, out var isPercent))
        {
            return;
        }

        if (isPercent)
        {
            percent(v);
        }
        else
        {
            px(v);
        }
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
        {
            if (!UiValue.TryLength(parts[i], out n[i], out _))
            {
                return;
            }
        }

        switch (parts.Length)
        {
            case 1: all(n[0]); break;
            case 2: hv(n[1], n[0]); break; // CSS order is vertical horizontal; Guinevere wants (h, v)
            case 4: tblr(n[0], n[1], n[2], n[3]); break;
        }
    }

    static float AlignFraction(string value) => alignments.GetValueOrDefault(value);
}
