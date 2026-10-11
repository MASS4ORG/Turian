namespace Turian.Engine.Core;

/// <summary>
/// Represents swap chain support details.
/// </summary>
public struct SwapChainSupportDetails
{
    /// <summary>
    /// Gets or sets the surface capabilities.
    /// </summary>
    public SurfaceCapabilitiesKHR Capabilities;

    /// <summary>
    /// Gets or sets the supported surface formats.
    /// </summary>
    public SurfaceFormatKHR[] Formats { get; set; }

    /// <summary>
    /// Gets or sets the supported present modes.
    /// </summary>
    public PresentModeKHR[] PresentModes { get; set; }
}
