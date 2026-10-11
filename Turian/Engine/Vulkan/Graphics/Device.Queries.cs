namespace Turian.Engine.Core;

public unsafe partial class Device
{
    void SetupDebugMessenger()
    {
        if (!enableValidationLayers)
        {
            return;
        }

        if (!vk.TryGetInstanceExtension(instance, out debugUtils))
        {
            return;
        }

        DebugUtilsMessengerCreateInfoEXT createInfo = new();
        PopulateDebugMessengerCreateInfo(ref createInfo);

        if (
            debugUtils!.CreateDebugUtilsMessenger(instance, in createInfo, null, out _)
            != Result.Success
        )
        {
            throw new VulkanException("failed to set up debug messenger!");
        }
    }

    uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData
    )
    {
        if (messageSeverity == DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt)
        {
            return Vk.False;
        }

        var msg = Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage);

        Debug.WriteLine($"{messageSeverity} | validation layer: {msg}");

        return Vk.False;
    }

    SwapChainSupportDetails QuerySwapChainSupportInternal(PhysicalDevice candidatePhysicalDevice)
    {
        if (IsHeadless) return new SwapChainSupportDetails { Formats = [], PresentModes = [] };

        var details = new SwapChainSupportDetails();

        _ = khrSurface.GetPhysicalDeviceSurfaceCapabilities(
            candidatePhysicalDevice,
            surface,
            out details.Capabilities
        );

        uint formatCount = 0;
        _ = khrSurface.GetPhysicalDeviceSurfaceFormats(candidatePhysicalDevice, surface, ref formatCount, null);

        if (formatCount != 0)
        {
            details.Formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatsPtr = details.Formats)
            {
                _ = khrSurface.GetPhysicalDeviceSurfaceFormats(
                    candidatePhysicalDevice,
                    surface,
                    ref formatCount,
                    formatsPtr
                );
            }
        }
        else
        {
            details.Formats = [];
        }

        uint presentModeCount = 0;
        _ = khrSurface.GetPhysicalDeviceSurfacePresentModes(
            candidatePhysicalDevice,
            surface,
            ref presentModeCount,
            null
        );

        if (presentModeCount != 0)
        {
            details.PresentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* formatsPtr = details.PresentModes)
            {
                _ = khrSurface.GetPhysicalDeviceSurfacePresentModes(
                    candidatePhysicalDevice,
                    surface,
                    ref presentModeCount,
                    formatsPtr
                );
            }
        }
        else
        {
            details.PresentModes = [];
        }

        return details;
    }

    QueueFamilyIndices FindQueueFamiliesInternal(PhysicalDevice candidateDevice)
    {
        var indices = new QueueFamilyIndices();

        uint queueFamilityCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(candidateDevice, ref queueFamilityCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilityCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(
                candidateDevice,
                ref queueFamilityCount,
                queueFamiliesPtr
            );
        }

        uint i = 0;
        foreach (var queueFamily in queueFamilies)
        {
            if (queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                indices.GraphicsFamily = i;
            }

            if (!IsHeadless)
            {
                _ = khrSurface.GetPhysicalDeviceSurfaceSupport(candidateDevice, i, surface, out var presentSupport);
                if (presentSupport) indices.PresentFamily = i;
            }

            if (indices.IsComplete())
            {
                break;
            }

            i++;
        }

        // In headless mode, present family == graphics family (no real presentation)
        if (IsHeadless && indices.GraphicsFamily.HasValue)
        {
            indices.PresentFamily = indices.GraphicsFamily;
        }

        return indices;
    }

    bool IsDeviceSuitable(PhysicalDevice candidateDevice)
    {
        var indices = FindQueueFamiliesInternal(candidateDevice);

        var extensionsSupported = CheckDeviceExtensionsSupport(candidateDevice);

        var swapChainAdequate = IsHeadless; // headless doesn't need swapchain support
        if (!IsHeadless && extensionsSupported)
        {
            var swapChainSupport = QuerySwapChainSupportInternal(candidateDevice);
            swapChainAdequate =
                swapChainSupport.Formats.Length > 0 && swapChainSupport.PresentModes.Length > 0;
        }

        vk.GetPhysicalDeviceFeatures(candidateDevice, out var supportedFeatures);

        PhysicalDeviceSynchronization2FeaturesKHR sync2Features =
            new()
            {
                SType = StructureType.PhysicalDeviceSynchronization2FeaturesKhr,
                Synchronization2 = Vk.True
            };

        PhysicalDeviceFeatures2 deviceFeatures2 =
            new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &sync2Features };

        vk.GetPhysicalDeviceFeatures2(candidateDevice, &deviceFeatures2);

        return indices.IsComplete()
               && extensionsSupported
               && swapChainAdequate
               && supportedFeatures.SamplerAnisotropy
               && sync2Features.Synchronization2;
    }

    bool CheckDeviceExtensionsSupport(PhysicalDevice candidateDevice)
    {
        uint extentionsCount = 0;
        _ = vk.EnumerateDeviceExtensionProperties(candidateDevice, (byte*)null, ref extentionsCount, null);

        var availableExtensions = new ExtensionProperties[extentionsCount];
        fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
        {
            _ = vk.EnumerateDeviceExtensionProperties(
                candidateDevice,
                (byte*)null,
                ref extentionsCount,
                availableExtensionsPtr
            );
        }

        var availableExtensionNames = availableExtensions
                                      .Select(extension => Marshal.PtrToStringAnsi((IntPtr)extension.ExtensionName))
                                      .ToHashSet();

        return deviceExtensions.All(availableExtensionNames.Contains);
    }

    string[] GetRequiredExtensions()
    {
        if (IsHeadless)
        {
            return enableValidationLayers ? [ExtDebugUtils.ExtensionName] : [];
        }

        var glfwExtensions = window!.VkSurface!.GetRequiredExtensions(out var glfwExtensionCount);
        var extensions = SilkMarshal.PtrToStringArray(
            (nint)glfwExtensions,
            (int)glfwExtensionCount
        );

        if (enableValidationLayers)
        {
            return [.. extensions, ExtDebugUtils.ExtensionName];
        }

        return extensions;
    }

    bool CheckValidationLayerSupport()
    {
        uint layerCount = 0;
        _ = vk.EnumerateInstanceLayerProperties(ref layerCount, null);
        var availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
        {
            _ = vk.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPtr);
        }

        var availableLayerNames = availableLayers
                                  .Select(layer => Marshal.PtrToStringAnsi((IntPtr)layer.LayerName))
                                  .ToHashSet();

        return validationLayers.All(availableLayerNames.Contains);
    }

    SampleCountFlags GetMaxUsableSampleCount()
    {
        vk.GetPhysicalDeviceProperties(physicalDevice, out var physicalDeviceProperties);

        var counts =
            physicalDeviceProperties.Limits.FramebufferColorSampleCounts
            & physicalDeviceProperties.Limits.FramebufferDepthSampleCounts;

        return counts switch
        {
            var c when (c & SampleCountFlags.Count64Bit) != 0 => SampleCountFlags.Count64Bit,
            var c when (c & SampleCountFlags.Count32Bit) != 0 => SampleCountFlags.Count32Bit,
            var c when (c & SampleCountFlags.Count16Bit) != 0 => SampleCountFlags.Count16Bit,
            var c when (c & SampleCountFlags.Count8Bit) != 0 => SampleCountFlags.Count8Bit,
            var c when (c & SampleCountFlags.Count4Bit) != 0 => SampleCountFlags.Count4Bit,
            var c when (c & SampleCountFlags.Count2Bit) != 0 => SampleCountFlags.Count2Bit,
            _ => SampleCountFlags.Count1Bit
        };
    }

    Format FindSupportedFormat(
        IEnumerable<Format> candidates,
        ImageTiling tiling,
        FormatFeatureFlags features
    )
    {
        foreach (var format in candidates)
        {
            vk.GetPhysicalDeviceFormatProperties(physicalDevice, format, out var props);

            if (tiling == ImageTiling.Linear && (props.LinearTilingFeatures & features) == features)
            {
                return format;
            }
            else if (
                tiling == ImageTiling.Optimal
                && (props.OptimalTilingFeatures & features) == features
            )
            {
                return format;
            }
        }

        throw new VulkanException("failed to find supported format!");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        vk.DestroyCommandPool(device, commandPool, null);
        vk.DestroyDevice(device, null);
        GC.SuppressFinalize(this);
    }
}
