# Turian Engine Vulkan

The Vulkan render backend: device, swapchain, windowing, window input, GPU resources (models, textures, materials) and the render systems. Its types keep the `Turian.Engine.Core` namespace, so code written against them only needs the assembly reference.

[Core](../Core/) never references this assembly; a host that renders references it and registers the device (`Vulkan`, also `IRenderDevice`) with the scene services. A host without it simulates scenes with no GPU, window or native graphics libraries.
