namespace Turian.Engine.Core;

/// <summary>A submesh candidate supplied to an optional GPU visibility pass in final draw order.</summary>
public readonly record struct OcclusionDraw(Model Model, int SubMesh, Matrix4x4 ModelMatrix, Bounds WorldBounds);

/// <summary>Records visibility work before the color pass and supplies GPU-written indirect draw commands.</summary>
public interface IOcclusionCuller : IDisposable
{
    /// <summary>Records the depth and visibility passes; returns false to use direct draws for this frame.</summary>
    bool Record(FrameInfo frame, ReadOnlySpan<OcclusionDraw> draws);

    /// <summary>The indirect commands, with one 20-byte entry per candidate in the supplied order.</summary>
    Silk.NET.Vulkan.Buffer IndirectBuffer { get; }

    /// <summary>Gets counts from the last completed use of the current frame slot.</summary>
    RenderCullingStats CompletedStats { get; }

    /// <summary>Gets the bytes allocated for this view's optional visibility resources.</summary>
    ulong AllocatedBytes { get; }
}

/// <summary>Creates a visibility pass for one view on the shared device.</summary>
public interface IOcclusionCullingFactory
{
    /// <summary>Whether this project's authored settings opt into the visibility pass.</summary>
    bool IsEnabled(AssetDatabase assets) => false;

    /// <summary>Creates a culler; the renderer owns its lifetime.</summary>
    IOcclusionCuller Create(Vulkan vulkan, Silk.NET.Vulkan.DescriptorSetLayout globalLayout);
}

/// <summary>Identifies the visibility factory provided by a loaded Brick.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class OcclusionCullingFactoryAttribute(Type factoryType) : Attribute
{
    /// <summary>The factory type, with a public parameterless constructor.</summary>
    public Type FactoryType { get; } = factoryType;
}

/// <summary>Finds a visibility factory in the Bricks the host has loaded.</summary>
public static class OcclusionCullers
{
    /// <summary>Returns the first declared factory, or null when no visibility Brick is loaded.</summary>
    public static IOcclusionCullingFactory? Find()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic &&
                assembly.GetCustomAttribute<OcclusionCullingFactoryAttribute>() is { } attribute)
                return (IOcclusionCullingFactory)Activator.CreateInstance(attribute.FactoryType)!;
        }
        return null;
    }
}
