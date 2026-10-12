namespace Turian.Tests;

/// <summary>Tests that the global descriptor set declares mesh and task stages only on a mesh-shading device.</summary>
public sealed class ShaderStagesTests
{
    /// <summary>A device without mesh shading gets only the vertex and fragment stages.</summary>
    [Fact]
    public void WithoutMeshShadingOnlyVertexAndFragmentStages()
    {
        Assert.Equal(
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            ShaderStages.GlobalSet(supportsMeshShaders: false)
        );
    }

    /// <summary>A mesh-shading device also gets the mesh and task stages.</summary>
    [Fact]
    public void WithMeshShadingAddsMeshAndTaskStages()
    {
        Assert.Equal(
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit
            | ShaderStageFlags.MeshBitExt | ShaderStageFlags.TaskBitExt,
            ShaderStages.GlobalSet(supportsMeshShaders: true)
        );
    }
}
