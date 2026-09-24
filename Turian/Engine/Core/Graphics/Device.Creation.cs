namespace Turian.Engine.Core;

public unsafe partial class Device
{
    void CreateInstance()
    {
        if (enableValidationLayers && !CheckValidationLayerSupport())
        {
            throw new VulkanException("validation layers requested, but not available!");
        }

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("Hello Triangle"),
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("No Engine"),
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version12
        };

        InstanceCreateInfo createInfo = new() { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &appInfo };

        var extensions = GetRequiredExtensions();
        createInfo.EnabledExtensionCount = (uint)extensions.Length;
        createInfo.PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);

        if (enableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);

            DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new();
            PopulateDebugMessengerCreateInfo(ref debugCreateInfo);
            createInfo.PNext = &debugCreateInfo;
        }
        else
        {
            createInfo.EnabledLayerCount = 0;
            createInfo.PNext = null;
        }

        var resultVulkan = vk.CreateInstance(in createInfo, null, out instance);
        if (resultVulkan != Result.Success)
        {
            throw new VulkanException("Vulkan: failed to create instance: {0}", resultVulkan);
        }

        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        _ = SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

        if (enableValidationLayers)
        {
            _ = SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
        }
    }

    void CreateSurface()
    {
        if (!vk.TryGetInstanceExtension(instance, out khrSurface))
        {
            throw new NotSupportedException("KHR_surface extension not found.");
        }

        if (window?.VkSurface is null)
        {
            throw new VulkanException("window.VkSurface is null and shouldn't be!");
        }

        surface = window.VkSurface
                        .Create<AllocationCallbacks>(instance.ToHandle(), null)
                        .ToSurface();
    }

    void CreateLogicalDevice()
    {
        uint deviceCount = 0;
        _ = vk.EnumeratePhysicalDevices(instance, ref deviceCount, null);

        if (deviceCount == 0)
        {
            throw new VulkanException("failed to find GPUs with Vulkan support!");
        }

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devicesPtr = devices)
        {
            _ = vk.EnumeratePhysicalDevices(instance, ref deviceCount, devicesPtr);
        }

        var found = false;
        foreach (var deviceToTry in devices)
        {
            if (IsDeviceSuitable(deviceToTry))
            {
                try
                {
                    device = CreateLogicalDevice(deviceToTry);
                    physicalDevice = deviceToTry;
                    msaaSamples = GetMaxUsableSampleCount();
                    found = true;
                    break;
                }
                catch (Exception ex)
                {
                    Log.Logger.LogWarning(ex, "Failed to create logical device");
                }
            }
        }

        if (!found)
        {
            throw new VulkanException("Vulkan: failed to find a suitable GPU");
        }

        vk.GetPhysicalDeviceProperties(physicalDevice, out var properties);
        deviceName = GetStringFromBytePointer(properties.DeviceName, 50).Trim();
        maxSamplerAnisotropy = properties.Limits.MaxSamplerAnisotropy;
        deviceLocalMemoryBytes = SumDeviceLocalHeaps();
        Log.Logger.Lap("device", $"Selected GPU: {deviceName}");
    }

    ulong SumDeviceLocalHeaps()
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var properties);

        ulong total = 0;
        for (var i = 0u; i < properties.MemoryHeapCount; i++)
        {
            var heap = properties.MemoryHeaps[(int)i];
            if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
            {
                total += heap.Size;
            }
        }

        return total;
    }

    static string GetStringFromBytePointer(byte* pointer, int length)
    {
        // Create a span from the byte pointer and decode the string
        var span = new Span<byte>(pointer, length);
        return Encoding.UTF8.GetString(span);
    }

    Silk.NET.Vulkan.Device CreateLogicalDevice(PhysicalDevice candidatePhysicalDevice)
    {
        var indices = FindQueueFamiliesInternal(candidatePhysicalDevice);

        var uniqueQueueFamilies = new[]
        {
            indices.GraphicsFamily!.Value,
            indices.PresentFamily!.Value
        }.Distinct().ToArray();

        graphicsFamilyIndex = indices.GraphicsFamily.Value;

        using var mem = GlobalMemory.Allocate(
            uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo)
        );
        var queueCreateInfos = (DeviceQueueCreateInfo*)
            Unsafe.AsPointer(ref mem.GetPinnableReference());

        var queuePriority = 1.0f;
        for (var i = 0; i < uniqueQueueFamilies.Length; i++)
        {
            queueCreateInfos[i] = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueQueueFamilies[i],
                QueueCount = 1,
                PQueuePriorities = &queuePriority
            };
        }

        // PhysicalDeviceFeatures deviceFeatures = new() { SamplerAnisotropy = true, };

        // Enable Synchronization 2 to eliminate a validation layer error, thanks gpt4!
        var sync2Features = new PhysicalDeviceSynchronization2FeaturesKHR
        {
            SType = StructureType.PhysicalDeviceSynchronization2FeaturesKhr,
            Synchronization2 = Vk.True
        };

        var features2 = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &sync2Features
        };
        features2.Features = new PhysicalDeviceFeatures { SamplerAnisotropy = Vk.True };

        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,
            PNext = &features2,
            EnabledExtensionCount = (uint)deviceExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions)
        };

        if (enableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);
        }
        else
        {
            createInfo.EnabledLayerCount = 0;
        }

        var resultVulkan = vk.CreateDevice(candidatePhysicalDevice, in createInfo, null, out var logicalDevice);
        if (resultVulkan != Result.Success)
        {
            throw new VulkanException("Vulkan: failed to create logical device: {0}", resultVulkan);
        }

        vk.GetDeviceQueue(logicalDevice, indices.GraphicsFamily!.Value, 0, out graphicsQueue);
        vk.GetDeviceQueue(logicalDevice, indices.PresentFamily!.Value, 0, out presentQueue);

        if (enableValidationLayers)
        {
            _ = SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
        }

        _ = SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

        return logicalDevice;
    }

    void CreateCommandPool()
    {
        var queueFamilyIndexes = FindQueueFamiliesInternal(physicalDevice);

        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndexes.GraphicsFamily!.Value,
            // added flag below to eliminate a validation layer error about clearing command buffer before recording
            Flags =
                CommandPoolCreateFlags.TransientBit
                | CommandPoolCreateFlags.ResetCommandBufferBit
        };

        var resultVulkan = vk.CreateCommandPool(device, in poolInfo, null, out commandPool);
        if (resultVulkan != Result.Success)
        {
            throw new VulkanException("Vulkan: failed to create command pool: {0}", resultVulkan);
        }
    }

    CommandBuffer BeginSingleTimeCommands()
    {
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1,
        };

        _ = vk.AllocateCommandBuffers(device, in allocateInfo, out var commandBuffer);

        CommandBufferBeginInfo beginInfo =
            new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

        _ = vk.BeginCommandBuffer(commandBuffer, in beginInfo);

        return commandBuffer;
    }

    void EndSingleTimeCommands(CommandBuffer commandBuffer)
    {
        _ = vk.EndCommandBuffer(commandBuffer);

        SubmitInfo submitInfo =
            new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };

        _ = vk.QueueSubmit(graphicsQueue, 1, in submitInfo, default);
        _ = vk.QueueWaitIdle(graphicsQueue);

        vk.FreeCommandBuffers(device, commandPool, 1, in commandBuffer);
    }

    internal static string GetString(byte* stringStart)
    {
        var characters = 0;
        while (stringStart[characters] != 0)
        {
            characters++;
        }

        return Encoding.UTF8.GetString(stringStart, characters);
    }

    void PopulateDebugMessengerCreateInfo(ref DebugUtilsMessengerCreateInfoEXT createInfo)
    {
        createInfo.SType = StructureType.DebugUtilsMessengerCreateInfoExt;
        createInfo.MessageSeverity =
            DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt
            | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
            | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
        createInfo.MessageType =
            DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
            | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
            | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt;
        createInfo.PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback;
    }

}
