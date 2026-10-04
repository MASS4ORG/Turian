namespace Turian.Editor.Core;

/// <summary>Controls the reference grid's plane, spacing, emphasis and origin axes.</summary>
[EditorSetting("Scene Viewer/Grid", Description = "Reference grid spacing and world axes.")]
public sealed class SceneGridSettings
{
    /// <summary>Gets or sets whether the reference grid is visible.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Gets or sets the two world dimensions containing the grid.</summary>
    public SceneGridPlane Plane { get; set; } = SceneGridPlane.Xz;

    /// <summary>Gets or sets the world-space distance between adjacent lines.</summary>
    [Range(0.01f, 100f)]
    public float CellSize { get; set; } = 1f;

    /// <summary>Gets or sets how many cells extend either side of the camera.</summary>
    [Range(5, 500)]
    public int HalfExtent { get; set; } = 50;

    /// <summary>Gets or sets the interval of intermediate lines in cells.</summary>
    [Range(1, 100)]
    public int IntermediateEvery { get; set; } = 5;

    /// <summary>Gets or sets the interval of major lines in cells.</summary>
    [Range(1, 100)]
    public int MajorEvery { get; set; } = 10;

    /// <summary>Gets or sets the width of ordinary grid lines in pixels.</summary>
    [Range(0.5f, 5f)]
    public float MinorThickness { get; set; } = 1f;

    /// <summary>Gets or sets the width of intermediate lines in pixels.</summary>
    [Range(0.5f, 5f)]
    public float IntermediateThickness { get; set; } = 1.5f;

    /// <summary>Gets or sets the width of major lines in pixels.</summary>
    [Range(0.5f, 5f)]
    public float MajorThickness { get; set; } = 2f;

    /// <summary>Gets or sets whether the grid's two origin axes are colored.</summary>
    public bool ShowPlaneAxes { get; set; } = true;

    /// <summary>Gets or sets whether the third world axis is also displayed.</summary>
    public bool ShowNormalAxis { get; set; }

    /// <summary>Gets or sets the width of colored origin axes in pixels.</summary>
    [Range(0.5f, 5f)]
    public float AxisThickness { get; set; } = 2f;

    /// <summary>Gets or sets the opacity of ordinary grid lines.</summary>
    [Range(0f, 1f)]
    public float MinorOpacity { get; set; } = 0.08f;

    /// <summary>Gets or sets the opacity of intermediate lines.</summary>
    [Range(0f, 1f)]
    public float IntermediateOpacity { get; set; } = 0.16f;

    /// <summary>Gets or sets the opacity of major lines.</summary>
    [Range(0f, 1f)]
    public float MajorOpacity { get; set; } = 0.24f;

    /// <summary>Gets or sets the opacity of colored origin axes.</summary>
    [Range(0f, 1f)]
    public float AxisOpacity { get; set; } = 0.65f;
}

/// <summary>The world plane containing the reference grid.</summary>
public enum SceneGridPlane
{
    /// <summary>The X and Y dimensions, perpendicular to Z.</summary>
    Xy,
    /// <summary>The X and Z dimensions, perpendicular to Y.</summary>
    Xz,
    /// <summary>The Y and Z dimensions, perpendicular to X.</summary>
    Yz
}
