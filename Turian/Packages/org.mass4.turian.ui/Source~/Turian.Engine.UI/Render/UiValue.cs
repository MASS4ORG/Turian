namespace Turian.Engine.UI;

/// <summary>Parsers for the scalar value forms used in <c>.ui</c> inline styles and attributes.</summary>
public static class UiValue
{
    static readonly Dictionary<string, bool> booleans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["true"] = true,
        ["1"] = true,
        ["yes"] = true,
        ["on"] = true,
        ["false"] = false,
        ["0"] = false,
        ["no"] = false,
        ["off"] = false,
    };

    /// <summary>Parses a length: bare number or <c>Npx</c> → pixels; <c>N%</c> → the fraction (0..1) via <paramref name="isPercent"/>.</summary>
    /// <param name="text">The length text.</param>
    /// <param name="value">The parsed magnitude (pixels, or the 0..1 fraction for a percentage).</param>
    /// <param name="isPercent">True when the text ended with <c>%</c>.</param>
    public static bool TryLength(string? text, out float value, out bool isPercent)
    {
        value = 0f;
        isPercent = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();
        if (t.EndsWith('%'))
        {
            isPercent = true;
            return float.TryParse(t[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && Finalise(ref value, static v => v / 100f);
        }

        if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            t = t[..^2].Trim();
        }

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
        return booleans.TryGetValue((text ?? string.Empty).Trim(), out value);
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
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();

        if (t.StartsWith('#'))
        {
            return TryHexColor(t[1..], out color);
        }

        if (t.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryRgbColor(t, out color);
        }

        return TryNamedColor(t, out color);
    }

    static bool TryHexColor(string hex, out GuiColor color)
    {
        color = GuiColor.Black;
        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }

        if (hex.Length != 6 && hex.Length != 8)
        {
            return false;
        }

        Span<int> channels = stackalloc int[3];
        for (var i = 0; i < channels.Length; i++)
        {
            if (!int.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out channels[i]))
            {
                return false;
            }
        }

        var alpha = 255;
        if (hex.Length == 8)
        {
            _ = int.TryParse(hex.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out alpha);
        }

        color = GuiColor.FromArgb(alpha, channels[0], channels[1], channels[2]);
        return true;
    }

    static bool TryRgbColor(string text, out GuiColor color)
    {
        color = GuiColor.Black;
        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.IndexOf(')', StringComparison.Ordinal);
        if (open < 0 || close < open)
        {
            return false;
        }

        var parts = text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        Span<float> channels = stackalloc float[3];
        for (var i = 0; i < channels.Length; i++)
        {
            if (!TryFloat(parts[i], out channels[i]))
            {
                return false;
            }
        }

        var alpha = RgbAlpha(parts);
        color = GuiColor.FromArgb((int)alpha, (int)channels[0], (int)channels[1], (int)channels[2]);
        return true;
    }

    static float RgbAlpha(string[] parts)
    {
        if (parts.Length != 4 || !TryFloat(parts[3], out var alpha))
        {
            return 255f;
        }

        return alpha <= 1f ? alpha * 255f : alpha;
    }

    static bool TryNamedColor(string text, out GuiColor color)
    {
        color = System.Drawing.Color.FromName(text);
        return color.A != 0 || text.Equals("transparent", StringComparison.OrdinalIgnoreCase);
    }

    static bool Finalise(ref float value, Func<float, float> f)
    {
        value = f(value);
        return true;
    }
}
