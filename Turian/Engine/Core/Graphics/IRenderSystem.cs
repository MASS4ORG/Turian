namespace Turian.Engine.Core;

/// <summary>
/// Represents an interface for a rendering system.
/// </summary>
public interface IRenderSystem : IDisposable
{
    /// <summary>
    /// Renders a frame using the rendering system.
    /// </summary>
    /// <param name="frameInfo">Information about the frame to render.</param>
    /// <param name="ubo">The global uniform buffer object used for rendering.</param>
    void Render(FrameInfo frameInfo, ref GlobalUbo ubo);
}
