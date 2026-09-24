namespace Turian.Tests;

/// <summary>
/// Holds one headless <see cref="Vulkan"/> device for a whole test class, or records why a
/// device could not be created so the tests can skip instead of fail on a machine without one.
/// </summary>
public sealed class VulkanFixture : IDisposable
{
    /// <summary>The shared headless Vulkan context; only valid when <see cref="Available"/>.</summary>
    public Vulkan Vulkan { get; } = null!;

    /// <summary>Whether a usable Vulkan device was created.</summary>
    public bool Available { get; }

    /// <summary>Why the device is unavailable, for the skip message.</summary>
    public string SkipReason { get; } = "Vulkan device available";

    /// <summary>Attempts to bring up a headless Vulkan device, tolerating environments without one.</summary>
    public VulkanFixture()
    {
        try
        {
            Vulkan = new Vulkan(Log.Logger);
            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"no usable Vulkan device: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Available)
            Vulkan.Device.Dispose();
    }
}
