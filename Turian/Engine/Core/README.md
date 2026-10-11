# Turian Engine Core

The runtime for scenes, components, DataAssets, serialization, input contracts and simulation. It has no GPU, window or native graphics dependency, so a server or headless build runs scenes without a display; rendering comes from a backend such as [Vulkan](../Vulkan/), reached through `IRenderDevice`. Engine services are composed through dependency injection and engine modules.

Optional features ship as [bricks](../../Packages/) to keep the runtime small. Editor tooling belongs in [Editor Core](../../Editor/Core/).
