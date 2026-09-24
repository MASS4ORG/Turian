namespace Turian.Editor.Core;

/// <summary>
/// Default import settings for model assets.
/// </summary>
public class ModelImportSettings
{
    /// <summary>
    /// Gets or sets the source model format inferred from the file extension.
    /// </summary>
    public string Format { get; set; } = "obj";

    /// <summary>
    /// Gets or sets whether meshes should be imported.
    /// </summary>
    public bool ImportMeshes { get; set; } = true;

    /// <summary>
    /// Gets or sets whether materials should be imported.
    /// </summary>
    public bool ImportMaterials { get; set; } = true;

    /// <summary>
    /// Gets or sets whether animations should be imported.
    /// </summary>
    public bool ImportAnimations { get; set; } = true;

    /// <summary>
    /// Gets or sets whether normals should be recalculated during import.
    /// </summary>
    public bool RecalculateNormals { get; set; }

    /// <summary>
    /// Gets or sets whether tangents should be generated during import.
    /// </summary>
    public bool GenerateTangents { get; set; } = true;

    /// <summary>
    /// Gets or sets a global scale multiplier applied on import.
    /// </summary>
    public float ScaleFactor { get; set; } = 1.0f;

    /// <summary>
    /// Gets or sets whether the imported hierarchy should be optimized.
    /// </summary>
    public bool OptimizeHierarchy { get; set; } = true;

    // GLTF-specific options
    /// <summary>
    /// Gets or sets whether to import glTF "extras" metadata.
    /// </summary>
    public bool ImportExtras { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to enable experimental extension imports (e.g., KHR extensions).
    /// </summary>
    public bool ImportExtensions { get; set; } = false;
}
