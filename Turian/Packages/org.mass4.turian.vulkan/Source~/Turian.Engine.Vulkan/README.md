# Turian Engine Vulkan

The source of the [Vulkan renderer brick](../../): device, swapchain, windowing, window input, GPU resources (models, textures, materials), the render systems and `VulkanPlayer`, the windowed game player. Its types keep the `Turian.Engine.Core` namespace, so code written against them only needs the assembly reference.

[Core](../../../../Engine/Core/) never references this assembly. The player registers the device (`Vulkan`, also `IRenderDevice`) with the scene services; the editor references the project directly and registers it the same way.
