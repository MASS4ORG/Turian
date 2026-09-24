namespace Turian.Engine.Core;

public partial class SwapChain
{
    /// <summary>
    /// Initializes the swap chain.
    /// </summary>
    void Initialize()
    {
        CreateSwapChain();
        CreateImageViews();
        CreateRenderPass();
        CreateColorResources();
        CreateDepthResources();
        CreateFrameBuffers();
        CreateSyncObjects();
    }

    unsafe void CreateSwapChain()
    {
        var swapChainSupport = device.QuerySwapChainSupport;

        var surfaceFormat = ChooseSwapSurfaceFormat(swapChainSupport.Formats);
        var presentMode = ChoosePresentMode(swapChainSupport.PresentModes);
        var extent = ChooseSwapExtent(swapChainSupport.Capabilities);

        var imageCount = swapChainSupport.Capabilities.MinImageCount + 1;
        if (
            swapChainSupport.Capabilities.MaxImageCount > 0
            && imageCount > swapChainSupport.Capabilities.MaxImageCount
        )
        {
            imageCount = swapChainSupport.Capabilities.MaxImageCount;
        }

        SwapchainCreateInfoKHR creatInfo =
            new()
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = device.Surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            };

        var indices = device.FindQueueFamilies;
        var queueFamilyIndices = stackalloc[]
        {
            indices.GraphicsFamily!.Value,
            indices.PresentFamily!.Value
        };

        if (indices.GraphicsFamily != indices.PresentFamily)
        {
            creatInfo = creatInfo with
            {
                ImageSharingMode = SharingMode.Concurrent,
                QueueFamilyIndexCount = 2,
                PQueueFamilyIndices = queueFamilyIndices,
            };
        }
        else
        {
            creatInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        creatInfo = creatInfo with
        {
            PreTransform = swapChainSupport.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
        };

        if (!vk.TryGetDeviceExtension(device.Instance, vkDevice, out khrSwapChain))
        {
            throw new NotSupportedException("VK_KHR_swapchain extension not found.");
        }

        creatInfo.OldSwapchain = oldSwapChain == default ? default : oldSwapChain.VkSwapChain;

        var resultVulkan = khrSwapChain.CreateSwapchain(vkDevice, in creatInfo, null, out swapChain);
        if (resultVulkan != Result.Success)
        {
            throw new VulkanException("Vulkan: failed to create swap chain: {0}", resultVulkan);
        }

        _ = khrSwapChain.GetSwapchainImages(vkDevice, swapChain, ref imageCount, null);
        swapChainImages = new Image[imageCount];
        fixed (Image* swapChainImagesPtr = swapChainImages)
        {
            _ = khrSwapChain.GetSwapchainImages(
                vkDevice,
                swapChain,
                ref imageCount,
                swapChainImagesPtr
            );
        }

        swapChainImageFormat = surfaceFormat.Format;
        swapChainExtent = extent;
    }

    unsafe void CreateImageViews()
    {
        swapChainImageViews = new ImageView[swapChainImages.Length];

        for (var i = 0; i < swapChainImages.Length; i++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = swapChainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = swapChainImageFormat,
                SubresourceRange =
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (
                vk.CreateImageView(vkDevice, in createInfo, null, out swapChainImageViews[i])
                != Result.Success
            )
            {
                throw new VulkanException("failed to create image view!");
            }
        }
    }

    unsafe void CreateRenderPass()
    {
        AttachmentDescription depthAttachment = new()
        {
            Format = device.FindDepthFormat(),
            Samples = device.MsaaSamples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        AttachmentReference depthAttachmentRef =
            new() { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal, };

        AttachmentDescription colorAttachment = new()
        {
            Format = swapChainImageFormat,
            Samples = device.MsaaSamples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
        };

        AttachmentReference colorAttachmentRef = new() { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal, };

        AttachmentDescription colorAttachmentResolve = new()
        {
            Format = swapChainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        AttachmentReference colorAttachmentResolveRef =
            new() { Attachment = 2, Layout = ImageLayout.AttachmentOptimalKhr };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
            PDepthStencilAttachment = &depthAttachmentRef,
            PResolveAttachments = &colorAttachmentResolveRef
        };

        SubpassDependency dependency = new()
        {
            DstSubpass = 0,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcSubpass = Vk.SubpassExternal,
            SrcAccessMask = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
        };

        AttachmentDescription[] attachments = [colorAttachment, depthAttachment, colorAttachmentResolve];

        fixed (AttachmentDescription* attachmentsPtr = attachments)
        {
            RenderPassCreateInfo renderPassInfo = new()
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)attachments.Length,
                PAttachments = attachmentsPtr,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };

            if (
                vk.CreateRenderPass(vkDevice, in renderPassInfo, null, out renderPass)
                != Result.Success
            )
            {
                throw new VulkanException("failed to create render pass!");
            }
        }
    }

    unsafe void CreateFrameBuffers()
    {
        swapChainFramebuffers = new Framebuffer[swapChainImageViews.Length];

        for (var i = 0; i < swapChainImageViews.Length; i++)
        {
            ImageView[] attachments =
            [
                colorImageViews[i],
                depthImageViews[i],
                swapChainImageViews[i]
            ];

            fixed (ImageView* attachmentsPtr = attachments)
            {
                FramebufferCreateInfo framebufferInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = renderPass,
                    AttachmentCount = (uint)attachments.Length,
                    PAttachments = attachmentsPtr,
                    Width = swapChainExtent.Width,
                    Height = swapChainExtent.Height,
                    Layers = 1,
                };

                var resultVulkan =
                    vk.CreateFramebuffer(device.VkDevice, in framebufferInfo, null, out swapChainFramebuffers[i]);
                if (resultVulkan != Result.Success)
                {
                    throw new VulkanException("Vulkan: failed to create framebuffer: {0}", resultVulkan);
                }
            }
        }
    }

    unsafe void CreateColorResources()
    {
        var colorFormat = swapChainImageFormat;

        var imageCount = ImageCount();
        colorImages = new Image[imageCount];
        colorImageMemorys = new DeviceMemory[imageCount];
        colorImageViews = new ImageView[imageCount];

        for (var i = 0; i < imageCount; i++)
        {
            ImageCreateInfo imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Extent =
                {
                    Width = swapChainExtent.Width,
                    Height = swapChainExtent.Height,
                    Depth = 1,
                },
                MipLevels = 1,
                ArrayLayers = 1,
                Format = colorFormat,
                Tiling = ImageTiling.Optimal,
                InitialLayout = ImageLayout.Undefined,
                Usage = ImageUsageFlags.TransientAttachmentBit | ImageUsageFlags.ColorAttachmentBit,
                Samples = device.MsaaSamples,
                SharingMode = SharingMode.Exclusive,
                Flags = 0
            };

            fixed (Image* imagePtr = &colorImages[i])
            {
                var resultVulkan = vk.CreateImage(vkDevice, in imageInfo, null, imagePtr);
                if (resultVulkan != Result.Success)
                {
                    throw new VulkanException("Vulkan: failed to create color image: {0}", resultVulkan);
                }
            }

            vk.GetImageMemoryRequirements(vkDevice, colorImages[i], out var memRequirements);

            MemoryAllocateInfo allocInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memRequirements.Size,
                MemoryTypeIndex = device.FindMemoryType(
                    memRequirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit
                ),
            };

            fixed (DeviceMemory* imageMemoryPtr = &colorImageMemorys[i])
            {
                var resultVulkan = vk.AllocateMemory(vkDevice, in allocInfo, null, imageMemoryPtr);
                if (resultVulkan != Result.Success)
                {
                    throw new VulkanException("Vulkan: failed to allocate color image memory: {0}", resultVulkan);
                }
            }

            _ = vk.BindImageMemory(vkDevice, colorImages[i], colorImageMemorys[i], 0);

            // color image view
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = colorImages[i],
                ViewType = ImageViewType.Type2D,
                Format = colorFormat,
                SubresourceRange =
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (
                vk.CreateImageView(vkDevice, in createInfo, null, out colorImageViews[i])
                != Result.Success
            )
            {
                throw new VulkanException("failed to create color image views!");
            }
        }
    }

    unsafe void CreateDepthResources()
    {
        var depthFormat = device.FindDepthFormat();
        swapChainDepthFormat = depthFormat;

        var imageCount = ImageCount();
        depthImages = new Image[imageCount];
        depthImageMemorys = new DeviceMemory[imageCount];
        depthImageViews = new ImageView[imageCount];

        for (var i = 0; i < imageCount; i++)
        {
            ImageCreateInfo imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Extent =
                {
                    Width = swapChainExtent.Width,
                    Height = swapChainExtent.Height,
                    Depth = 1,
                },
                MipLevels = 1,
                ArrayLayers = 1,
                Format = depthFormat,
                Tiling = ImageTiling.Optimal,
                InitialLayout = ImageLayout.Undefined,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit,
                Samples = device.MsaaSamples,
                SharingMode = SharingMode.Exclusive,
                Flags = 0
            };

            fixed (Image* imagePtr = &depthImages[i])
            {
                var resultVulkan = vk.CreateImage(vkDevice, in imageInfo, null, imagePtr);
                if (resultVulkan != Result.Success)
                {
                    throw new VulkanException("Vulkan: failed to create depth image: {0}", resultVulkan);
                }
            }

            vk.GetImageMemoryRequirements(vkDevice, depthImages[i], out var memRequirements);

            MemoryAllocateInfo allocInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memRequirements.Size,
                MemoryTypeIndex = device.FindMemoryType(
                    memRequirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit
                ),
            };

            fixed (DeviceMemory* imageMemoryPtr = &depthImageMemorys[i])
            {
                var resultVulkan = vk.AllocateMemory(vkDevice, in allocInfo, null, imageMemoryPtr);
                if (resultVulkan != Result.Success)
                {
                    throw new VulkanException("vulkan: failed to allocate depth image memory: {0}", resultVulkan);
                }
            }

            _ = vk.BindImageMemory(vkDevice, depthImages[i], depthImageMemorys[i], 0);

            // depth image view
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = depthImages[i],
                ViewType = ImageViewType.Type2D,
                Format = depthFormat,
                SubresourceRange =
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (
                vk.CreateImageView(vkDevice, in createInfo, null, out depthImageViews[i])
                != Result.Success
            )
            {
                throw new VulkanException("failed to create depth image views!");
            }
        }
    }

    unsafe void CreateSyncObjects()
    {
        imageAvailableSemaphores = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];
        renderFinishedSemaphores = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];
        inFlightFences = new Fence[MaxFramesInFlight];
        imagesInFlight = new Fence[swapChainImages.Length];

        SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo, };

        FenceCreateInfo fenceInfo =
            new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit, };

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            var resultVulkan = vk.CreateSemaphore(vkDevice, in semaphoreInfo, null, out imageAvailableSemaphores[i]);
            if (resultVulkan != Result.Success)
            {
                throw new VulkanException("Vulkan: failed to create synchronization objects for a frame: {0}",
                    resultVulkan);
            }

            resultVulkan = vk.CreateSemaphore(vkDevice, in semaphoreInfo, null, out renderFinishedSemaphores[i]);
            if (resultVulkan != Result.Success)
            {
                throw new VulkanException("Vulkan: failed to create synchronization objects for a frame: {0}",
                    resultVulkan);
            }

            resultVulkan = vk.CreateFence(vkDevice, in fenceInfo, null, out inFlightFences[i]);
            if (resultVulkan != Result.Success)
            {
                throw new VulkanException("Vulkan: failed to create synchronization objects for a frame: {0}",
                    resultVulkan);
            }
        }
    }

}
