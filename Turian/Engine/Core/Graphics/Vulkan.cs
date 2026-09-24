namespace Turian.Engine.Core;

/// <summary>
/// Represents a Vulkan graphics context and device manager.
/// </summary>
public class Vulkan
{
    /// <summary>
    /// Gets the Vulkan API instance.
    /// </summary>
    public Vk Vk { get; init; }

    /// <summary>
    /// Gets the Vulkan device.
    /// </summary>
    public Device Device { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Vulkan"/> class with the specified window manager and logger.
    /// </summary>
    /// <param name="windowManager">The window manager used for creating the Vulkan device.</param>
    /// <param name="logger">The logger used for reporting startup progress.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="windowManager"/> is <c>null</c>.</exception>
    public Vulkan(WindowManager windowManager, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(windowManager);

        Vk = Vk.GetApi();
        Device = new Device(Vk, windowManager.Window);

        logger.Lap("startup", "got vk");
    }

    /// <summary>
    /// Initializes a headless instance of the <see cref="Vulkan"/> class (no window surface).
    /// Use this in the editor process where rendering goes to offscreen targets, not a swapchain.
    /// </summary>
    /// <param name="logger">The logger used for reporting startup progress.</param>
    public Vulkan(ILogger logger)
    {
        Vk = Vk.GetApi();
        Device = new Device(Vk);

        logger.Lap("startup", "Vulkan headless device ready");
    }
}
