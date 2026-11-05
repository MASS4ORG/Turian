using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Turian.Android;

/// <summary>The startup graphics decision and GPU/driver details that explain it.</summary>
/// <param name="IsSupported">Whether the device meets the Android renderer's feature floor.</param>
/// <param name="Diagnostics">The GPU, Vulkan version, driver and required feature evidence.</param>
public sealed record AndroidGraphicsSupportResult(bool IsSupported, string Diagnostics);

/// <summary>Checks physical-device support before creating a rendering device or surface.</summary>
public static class AndroidGraphicsSupport
{
    /// <summary>Creates a temporary Vulkan instance and queries devices without initializing the engine.</summary>
    /// <returns>The support decision and diagnostic details.</returns>
    public static unsafe AndroidGraphicsSupportResult Probe()
    {
        using var vk = Vk.GetApi();
        var application = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version13 };
        var create = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &application,
        };
        var result = vk.CreateInstance(in create, null, out var instance);
        if (result != Result.Success)
        {
            return new(false, $"Vulkan 1.3 instance creation failed: {result}.");
        }
        try
        {
            uint count = 0;
            result = vk.EnumeratePhysicalDevices(instance, ref count, null);
            if (result != Result.Success || count == 0)
            {
                return new(false, $"No Vulkan physical device is available: {result}.");
            }
            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* pointer = devices)
            {
                result = vk.EnumeratePhysicalDevices(instance, ref count, pointer);
            }
            if (result != Result.Success)
            {
                return new(false, $"Vulkan device enumeration failed: {result}.");
            }
            List<string> diagnostics = [];
            foreach (var device in devices.Take((int)count))
            {
                vk.GetPhysicalDeviceProperties(device, out var properties);
                var name = Marshal.PtrToStringUTF8((nint)properties.DeviceName) ?? "Unknown GPU";
                var features13 = new PhysicalDeviceVulkan13Features
                {
                    SType = StructureType.PhysicalDeviceVulkan13Features,
                };
                var features = new PhysicalDeviceFeatures2
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    PNext = &features13,
                };
                if (properties.ApiVersion >= Vk.Version13)
                {
                    vk.GetPhysicalDeviceFeatures2(device, &features);
                }
                var detail = $"GPU={name}; Vulkan={properties.ApiVersion >> 22}.{(properties.ApiVersion >> 12) & 1023}.{properties.ApiVersion & 4095}; driver={properties.DriverVersion}; anisotropy={features.Features.SamplerAnisotropy}; synchronization2={features13.Synchronization2}; dynamicRendering={features13.DynamicRendering}";
                if (MeetsRequirements(properties.ApiVersion, features.Features.SamplerAnisotropy,
                    features13.Synchronization2, features13.DynamicRendering))
                {
                    return new(true, detail);
                }
                diagnostics.Add(detail);
            }
            return new(false, string.Join(Environment.NewLine, diagnostics));
        }
        finally
        {
            vk.DestroyInstance(instance, null);
        }
    }

    internal static bool MeetsRequirements(uint version, bool anisotropy, bool synchronization2, bool dynamicRendering) =>
        version >= Vk.Version13 && anisotropy && synchronization2 && dynamicRendering;
}
