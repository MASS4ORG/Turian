#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member: they are obvious
namespace Turian.Engine.Core;

/// <summary>
/// Named constants for <see cref="Color"/>, in linear light (as every <see cref="Color"/> is).
/// </summary>
public readonly partial record struct Color
{
    public static Color White => new(1f, 1f, 1f);
    public static Color Black => new(0f, 0f, 0f);
    public static Color RoughGreen => new(0f, 0.6f, 0f);

    // main
    public static Color Red => new(1f, 0f, 0f);
    public static Color Green => new(0f, 1f, 0f);
    public static Color Blue => new(0f, 0f, 1f);

    // mix1
    public static Color Yellow => new(1f, 1f, 0f);
    public static Color Magenta => new(1f, 0f, 1f);
    public static Color Cyan => new(0f, 1f, 1f);
    public static Color Gray => new(0.5f, 0.5f, 0.5f);

    // others
    public static Color Pink => new(1f, 0.75f, 0.8f);
    public static Color Orange => new(1f, 0.65f, 0f);
    public static Color Purple => new(0.6f, 0.4f, 0.8f);
    public static Color Brown => new(0.6f, 0.4f, 0.2f);
    public static Color Beige => new(0.96f, 0.96f, 0.86f);
    public static Color Olive => new(0.5f, 0.5f, 0f);
    public static Color Maroon => new(0.5f, 0f, 0f);
    public static Color Navy => new(0f, 0f, 0.5f);
    public static Color Teal => new(0f, 0.5f, 0.5f);

    // others2
    public static Color Lime => new(0.75f, 1f, 0f);
    public static Color Turquoise => new(0.25f, 0.88f, 0.82f);
    public static Color Lavender => new(0.9f, 0.9f, 0.98f);
    public static Color Coral => new(1f, 0.5f, 0.31f);
    public static Color Salmon => new(0.98f, 0.5f, 0.45f);
    public static Color Peach => new(1f, 0.8f, 0.64f);
    public static Color Mint => new(0.6f, 1f, 0.8f);
    public static Color PowderBlue => new(0.69f, 0.88f, 0.9f);
    public static Color LightGray => new(0.83f, 0.83f, 0.83f);

    public static Color WhiteSmoke => new(0.96f, 0.96f, 0.96f);
}
