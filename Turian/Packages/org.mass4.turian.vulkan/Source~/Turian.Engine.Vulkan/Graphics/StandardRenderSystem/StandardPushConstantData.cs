namespace Turian.Engine.Core;

/// <summary>
/// Represents a simple push constant data containing model and normal matrices.
/// </summary>
[PublicAPI]
public struct StandardPushConstantData
{
    /// <summary>
    /// Gets or sets the model matrix.
    /// </summary>
    public Matrix4x4 ModelMatrix { get; set; }

    /// <summary>
    /// Gets or sets the normal matrix.
    /// </summary>
    public Matrix4x4 NormalMatrix { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="StandardPushConstantData"/> struct.
    /// Sets the <see cref="ModelMatrix"/> and <see cref="NormalMatrix"/> to Identity by default.
    /// </summary>
    public StandardPushConstantData()
    {
        ModelMatrix = Matrix4x4.Identity;
        NormalMatrix = Matrix4x4.Identity;
    }

    /// <summary>
    /// Gets the size of the <see cref="StandardPushConstantData"/> struct.
    /// </summary>
    /// <returns>The size of the <see cref="StandardPushConstantData"/> struct in bytes.</returns>
    public static uint SizeOf() => (uint)Unsafe.SizeOf<StandardPushConstantData>();
}
