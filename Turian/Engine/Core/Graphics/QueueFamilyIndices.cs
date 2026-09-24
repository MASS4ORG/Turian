namespace Turian.Engine.Core;

/// <summary>
/// Represents queue family indices.
/// </summary>
public struct QueueFamilyIndices
{
    /// <summary>
    /// Gets or sets the index of the graphics family queue.
    /// </summary>
    public uint? GraphicsFamily { get; set; }

    /// <summary>
    /// Gets or sets the index of the present family queue.
    /// </summary>
    public uint? PresentFamily { get; set; }

    /// <summary>
    /// Checks if both graphics and present queue families are available.
    /// </summary>
    /// <returns>True if both families are available, otherwise false.</returns>
    public bool IsComplete()
    {
        return GraphicsFamily.HasValue && PresentFamily.HasValue;
    }
}
