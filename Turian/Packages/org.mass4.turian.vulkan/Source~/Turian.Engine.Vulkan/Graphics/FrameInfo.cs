namespace Turian.Engine.Core;

/// <summary>
/// Represents information about a frame in a rendering context.
/// </summary>
[PublicAPI]
public struct FrameInfo
{
    /// <summary>
    /// Gets or sets the index of the frame.
    /// </summary>
    public int FrameIndex { get; set; }

    /// <summary>
    /// Gets or sets the time taken to render the frame in seconds.
    /// </summary>
    public float FrameTime { get; set; }

    /// <summary>
    /// Gets or sets the command buffer associated with the frame.
    /// </summary>
    public CommandBuffer CommandBuffer { get; set; }

    /// <summary>
    /// Gets or sets the camera used for rendering the frame.
    /// </summary>
    public ICamera Camera { get; set; }

    /// <summary>
    /// Gets or sets the global descriptor set used for the frame.
    /// </summary>
    public DescriptorSet GlobalDescriptorSet { get; set; }

    /// <summary>
    /// Gets or sets the list of nodes included in the frame.
    /// </summary>
    public ObservableCollection<Node> Nodes { get; set; }

    /// <summary>Gets or sets the width of the framebuffer in pixels.</summary>
    public uint ViewportWidth { get; set; }

    /// <summary>Gets or sets the height of the framebuffer in pixels.</summary>
    public uint ViewportHeight { get; set; }
}
