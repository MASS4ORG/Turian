namespace Turian.Editor.CLI;

/// <summary>
/// Camera placement and output size for a headless screenshot.
/// </summary>
sealed class ScreenshotOptions
{
    /// <summary>Gets the output width in pixels.</summary>
    public required uint Width { get; init; }

    /// <summary>Gets the output height in pixels.</summary>
    public required uint Height { get; init; }

    /// <summary>Gets the number of frames rendered before the pixels are read back.</summary>
    public required int Frames { get; init; }

    /// <summary>Gets the camera yaw in degrees.</summary>
    public required float Yaw { get; init; }

    /// <summary>Gets the camera pitch in degrees.</summary>
    public required float Pitch { get; init; }

    /// <summary>
    /// Gets the camera position, or <c>null</c> to place the camera so the whole scene fits in view.
    /// </summary>
    public Vector3? Position { get; init; }

    /// <summary>
    /// Gets the intensity of a point light attached to the camera, or 0 for no light.
    /// Intensity falls off with the square of the distance, so a scene viewed from
    /// <c>d</c> metres needs roughly <c>d²</c>.
    /// </summary>
    public required float Headlight { get; init; }

    /// <summary>
    /// Name of a scene node carrying a <c>CameraComponent</c> to render from. When set, its
    /// position, orientation and field of view replace <see cref="Position"/>, <see cref="Yaw"/>
    /// and <see cref="Pitch"/> — the point of a named camera is that it frames the same shot every
    /// time, which is what makes successive runs comparable.
    /// </summary>
    public string? CameraName { get; init; }

    /// <summary>
    /// Parses an <c>x,y,z</c> position.
    /// </summary>
    /// <param name="value">The text to parse, or <c>null</c>/empty for no position.</param>
    /// <returns>The parsed position, or <c>null</c>.</returns>
    public static Vector3? ParsePosition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !float.TryParse(parts[0], CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], CultureInfo.InvariantCulture, out var z))
        {
            throw new FormatException($"'{value}' is not a valid 'x,y,z' position.");
        }

        return new Vector3(x, y, z);
    }
}
