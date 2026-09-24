namespace Turian.Engine.UI;

/// <summary>Parsers for the scalar value forms used in <c>.ui</c> inline styles and attributes.</summary>
public static class UiValue
{
    /// <summary>Parses a length: bare number or <c>Npx</c> → pixels; <c>N%</c> → the fraction (0..1) via <paramref name="isPercent"/>.</summary>
    /// <param name="text">The length text.</param>
    /// <param name="value">The parsed magnitude (pixels, or the 0..1 fraction for a percentage).</param>
    /// <param name="isPercent">True when the text ended with <c>%</c>.</param>
    public static bool TryLength(string? text, out float value, out bool isPercent)
    {
        value = 0f;
        isPercent = false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.Trim();
        if (t.EndsWith('%'))
        {
            isPercent = true;
            return float.TryParse(t[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && Finalise(ref value, v => v / 100f);
        }

        if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
        return float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Parses a bare float (invariant culture).</summary>
    /// <param name="text">The number text.</param>
    /// <param name="value">The parsed value.</param>
    public static bool TryFloat(string? text, out float value) =>
        float.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Parses a bool: <c>true</c>/<c>false</c>/<c>1</c>/<c>0</c>/<c>yes</c>/<c>no</c>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="value">The parsed bool.</param>
    public static bool TryBool(string? text, out bool value)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t is "1"
            || t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t is "0"
            || t.Equals("no", StringComparison.OrdinalIgnoreCase) || t.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    /// <summary>
    /// Parses a color: <c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>rgb(r,g,b)</c>,
    /// <c>rgba(r,g,b,a)</c> (a in 0..1 or 0..255), or a named color Guinevere knows.
    /// </summary>
    /// <param name="text">The color text.</param>
    /// <param name="color">The parsed color.</param>
    public static bool TryColor(string? text, out GuiColor color)
    {
        color = GuiColor.Black;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();

        if (t.StartsWith('#'))
        {
            var hex = t[1..];
            if (hex.Length == 3)
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);

            if ((hex.Length == 6 || hex.Length == 8)
                && int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
                && int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
                && int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                var a = 255;
                if (hex.Length == 8)
                    _ = int.TryParse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a);
                color = GuiColor.FromArgb(a, r, g, b);
                return true;
            }

            return false;
        }

        if (t.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = t.IndexOf('(', StringComparison.Ordinal);
            var close = t.IndexOf(')', StringComparison.Ordinal);
            if (open < 0 || close < open) return false;

            var parts = t[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length is < 3 or > 4) return false;
            if (!TryFloat(parts[0], out var rr) || !TryFloat(parts[1], out var gg) || !TryFloat(parts[2], out var bb))
                return false;

            var aa = 255f;
            if (parts.Length == 4 && TryFloat(parts[3], out var av))
                aa = av <= 1f ? av * 255f : av;

            color = GuiColor.FromArgb((int)aa, (int)rr, (int)gg, (int)bb);
            return true;
        }

        try
        {
            color = System.Drawing.Color.FromName(t);
            return color.A != 0 || t.Equals("transparent", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static bool Finalise(ref float value, Func<float, float> f)
    {
        value = f(value);
        return true;
    }
}
