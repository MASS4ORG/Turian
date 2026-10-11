namespace Turian.Engine.Core;

/// <summary>
/// Represents an interface for a rendering system.
/// </summary>
public interface IRenderSystem : IDisposable
{
    /// <summary>
    /// Gathers what this frame draws and writes this system's share of the global UBO. Runs before the UBO is
    /// uploaded, so values written here reach the GPU the same frame.
    /// </summary>
    /// <param name="frameInfo">Information about the frame to render.</param>
    /// <param name="ubo">The global uniform buffer object about to be uploaded.</param>
    void Prepare(FrameInfo frameInfo, GlobalUbo ubo)
    {
    }

    /// <summary>Records depth or compute work after the UBO upload and before the main render pass begins.</summary>
    void RecordBeforeRenderPass(FrameInfo frameInfo)
    {
    }

    /// <summary>
    /// Renders a frame using the rendering system.
    /// </summary>
    /// <param name="frameInfo">Information about the frame to render.</param>
    /// <param name="ubo">The global uniform buffer object used for rendering.</param>
    void Render(FrameInfo frameInfo, ref GlobalUbo ubo);
}
