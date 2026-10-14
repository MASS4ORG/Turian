namespace Turian.Engine.Core;

public partial class Device
{
    /// <summary>Gets whether the device currently holds a Vulkan surface to present to.</summary>
    public bool HasSurface => surface.Handle != 0;

    void CreateSurface()
    {
        if (!vk.TryGetInstanceExtension(instance, out khrSurface))
        {
            throw new NotSupportedException("KHR_surface extension not found.");
        }

        surface = surfaceHost!.CreateSurface(vk, instance);
    }

    /// <summary>
    /// Destroys the Vulkan surface after the host's native surface went away. Swapchains built on it must already be
    /// destroyed.
    /// </summary>
    public void DestroySurface()
    {
        if (!HasSurface)
        {
            return;
        }

        surfaceHost!.DestroySurface(vk, instance, surface);
        surface = default;
    }

    /// <summary>Creates a new Vulkan surface once the host's native surface is back; a presenting device only.</summary>
    /// <exception cref="VulkanException">The present queue cannot present to the new surface.</exception>
    public void RecreateSurface()
    {
        if (HasSurface)
        {
            return;
        }

        surface = surfaceHost!.CreateSurface(vk, instance);
        EnsurePresentSupport();
    }

    void EnsurePresentSupport()
    {
        _ = khrSurface.GetPhysicalDeviceSurfaceSupport(physicalDevice, presentFamilyIndex, surface, out var supported);
        if (!supported)
        {
            throw new VulkanException("Vulkan: the present queue cannot present to the recreated surface");
        }
    }
}
