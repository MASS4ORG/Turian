namespace Turian.Engine.Core;

/// <summary>
/// The native window or view a Vulkan swapchain presents to, so the renderer does not depend on one windowing system.
/// </summary>
/// <remarks>
/// Events are raised on the thread running <see cref="Run"/>, between frames, so handlers may destroy and recreate
/// Vulkan objects. A host whose platform reports surface changes on another thread marshals them onto that thread.
/// </remarks>
public interface ISurfaceHost : IDisposable
{
    /// <summary>Gets the Vulkan instance extensions the host needs to create its surface.</summary>
    IReadOnlyList<string> RequiredInstanceExtensions { get; }

    /// <summary>Gets the drawable area in pixels; zero while the surface is minimized or hidden.</summary>
    Int2 FramebufferSize { get; }

    /// <summary>Raised with the new framebuffer size after the drawable area changes.</summary>
    event Action<Int2>? Resized;

    /// <summary>
    /// Raised when the native surface goes away; handlers release the Vulkan surface before returning, because the
    /// native window may be freed as soon as they do.
    /// </summary>
    event Action? SurfaceLost;

    /// <summary>Raised when a native surface is available again after <see cref="SurfaceLost"/>.</summary>
    event Action? SurfaceRestored;

    /// <summary>Raised when the user or the system asks the host to close.</summary>
    event Action? Closing;

    /// <summary>Creates a Vulkan surface for the current native surface.</summary>
    /// <param name="vk">The Vulkan API.</param>
    /// <param name="instance">An instance created with <see cref="RequiredInstanceExtensions"/> enabled.</param>
    /// <returns>The new surface, owned by the caller until it passes it to <see cref="DestroySurface"/>.</returns>
    SurfaceKHR CreateSurface(Vk vk, Instance instance);

    /// <summary>Destroys a surface returned by <see cref="CreateSurface"/> and releases the host's hold on it.</summary>
    /// <param name="vk">The Vulkan API.</param>
    /// <param name="instance">The instance the surface was created on.</param>
    /// <param name="surface">The surface to destroy.</param>
    void DestroySurface(Vk vk, Instance instance, SurfaceKHR surface);

    /// <summary>Pumps platform events and calls <paramref name="frame"/> once per frame until the host closes.</summary>
    /// <param name="frame">Runs one frame, given the seconds since the previous one.</param>
    void Run(Action<double> frame);

    /// <summary>Asks the host to close, ending <see cref="Run"/> after the current frame.</summary>
    void Close();

    /// <summary>Creates the input context that reports this host's keyboards, mice and gamepads.</summary>
    /// <returns>A new input context, owned by the caller.</returns>
    IInputContext CreateInput();
}
