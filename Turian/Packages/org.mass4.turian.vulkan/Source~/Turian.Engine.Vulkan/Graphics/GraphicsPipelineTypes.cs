namespace Turian.Engine.Core;

/// <summary>
/// Enumerates the types of graphics pipelines used in a Vulkan-based graphics engine.
/// </summary>
/// <remarks>
/// Graphics pipelines define how vertices and fragments are processed during rendering.
/// The engine can have different pipeline types for various rendering scenarios, such as
/// standard rendering or specialized mesh rendering.
/// </remarks>
public enum GraphicsPipelineTypes
{
    /// <summary>
    /// Represents the standard graphics pipeline for rendering.
    /// </summary>
    Std,

    /// <summary>
    /// Represents a specialized graphics pipeline for mesh rendering.
    /// </summary>
    Mesh
}
