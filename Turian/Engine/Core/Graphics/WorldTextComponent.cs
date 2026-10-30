namespace Turian.Engine.Core;

/// <summary>A multiline world-space SDF label drawn with a Skia font and linear RGBA tint.</summary>
[ComponentContextMenu("Rendering/World Text")]
[TypeId("79b3acae-463f-4c5d-b868-17104f59cf25")]
public sealed class WorldTextComponent : Component
{
    /// <summary>Text to draw; newline separates lines and an empty string draws nothing.</summary>
    public string Text { get; set; } = "Text";

    /// <summary>A Skia font family, project font path or asset:// font identifier, shared with runtime UI fonts.</summary>
    public string Font { get; set; } = "sans-serif";

    /// <summary>Font em height in world units before the node's scale.</summary>
    public float FontSize
    {
        get;
        set
        {
            CurveValidation.Finite(value);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 0.5f;

    /// <summary>Linear RGBA tint; alpha supports fading damage numbers and labels.</summary>
    public Color Color { get; set; } = Color.White;

    /// <summary>Whether each camera sees the front of the label; false keeps the node's orientation.</summary>
    public bool Billboard { get; set; } = true;

    /// <summary>Normalized anchor, measured from the lower-left corner; the default centers the text.</summary>
    public Vector2 Anchor { get; set; } = new(0.5f);

    /// <summary>Builds a world-space text rectangle with top-left texture origin.</summary>
    public WorldGraphicGeometry BuildGeometry(ICamera camera, Vector2 size)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var transform = Node?.GlobalTransform ?? Transform.Identity;
        var right = Billboard ? camera.Right * transform.Scale.X : transform.Right * transform.Scale.X;
        var up = Billboard ? camera.Up * transform.Scale.Y : transform.Up * transform.Scale.Y;
        var lower = transform.Position - right * (Anchor.X * size.X) - up * (Anchor.Y * size.Y);
        var color = (Vector4)Color;
        WorldGraphicVertex[] vertices =
        [
            new(lower, color, new(0, 1)),
            new(lower + right * size.X, color, new(1, 1)),
            new(lower + up * size.Y, color, new(0, 0)),
            new(lower + right * size.X + up * size.Y, color, new(1, 0)),
        ];
        return new(vertices, [0, 1, 2, 1, 3, 2]);
    }
}
