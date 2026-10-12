namespace Turian.Engine.Core;

/// <summary>Shader stage flags the renderer declares on its descriptor set layouts.</summary>
static class ShaderStages
{
    /// <summary>
    /// The stages the global descriptor set is visible to: vertex and fragment always, plus mesh and task only
    /// when the device enables mesh shading, since those stage bits are invalid on a device without it.
    /// </summary>
    /// <param name="supportsMeshShaders">Whether task and mesh shaders are enabled on the device.</param>
    /// <returns>The stage flags to declare on the global set's bindings.</returns>
    internal static ShaderStageFlags GlobalSet(bool supportsMeshShaders) =>
        supportsMeshShaders
            ? ShaderStageFlags.VertexBit
              | ShaderStageFlags.FragmentBit
              | ShaderStageFlags.MeshBitExt
              | ShaderStageFlags.TaskBitExt
            : ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
}
