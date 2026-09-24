namespace Turian.Engine.Core;

/// <summary>
/// A deterministic packed sRGB color for UI rendering, serialization and native interop.
/// The packed value is <c>0xRRGGBBAA</c>; RGB bytes are sRGB encoded and alpha is linear.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Color32
{
    /// <summary>The packed <c>0xRRGGBBAA</c> value.</summary>
    public readonly uint Rgba;

    /// <summary>Creates an sRGB color from byte channels.</summary>
    public Color32(byte red, byte green, byte blue, byte alpha = byte.MaxValue)
    {
        Rgba = (uint)(red << 24 | green << 16 | blue << 8 | alpha);
    }

    /// <summary>Creates a color from a packed <c>0xRRGGBBAA</c> value.</summary>
    public Color32(uint rgba) => Rgba = rgba;

    /// <summary>The sRGB-encoded red channel.</summary>
    public byte R => (byte)(Rgba >> 24);

    /// <summary>The sRGB-encoded green channel.</summary>
    public byte G => (byte)(Rgba >> 16);

    /// <summary>The sRGB-encoded blue channel.</summary>
    public byte B => (byte)(Rgba >> 8);

    /// <summary>The linear alpha channel.</summary>
    public byte A => (byte)Rgba;

    /// <summary>Parses <c>RRGGBB</c>, <c>RRGGBBAA</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
    public static Color32 FromHex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hex = value.AsSpan().Trim();
        if (!hex.IsEmpty && hex[0] == '#') hex = hex[1..];
        if (hex.Length is not (6 or 8))
            throw new FormatException("A color must contain six RGB or eight RGBA hexadecimal digits.");

        var packed = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (hex.Length == 6) packed = packed << 8 | byte.MaxValue;
        return new Color32(packed);
    }

    /// <summary>Formats this color as <c>#RRGGBBAA</c>.</summary>
    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{Rgba:X8}");

    // Conversions. Evaluate if we need to add/link to these libraries
    // public static implicit operator SKColor(Color32 color) => new(color.R, color.G, color.B, color.A);
    // public static implicit operator Color32(SKColor color) => new(color.Red, color.Green, color.Blue, color.Alpha);
    // public static implicit operator Color32(Color color) => new(color.R, color.G, color.B, color.A);
    // public static implicit operator Color(Color32 color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
