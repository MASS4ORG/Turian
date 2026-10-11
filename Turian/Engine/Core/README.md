# Turian Engine Core

The runtime for scenes, components, DataAssets, serialization, input contracts and simulation. It has no GPU, window or native graphics dependency: a game runs the player its render brick provides, such as the [Vulkan renderer](../../Packages/org.mass4.turian.vulkan/), or the headless player when none is installed, as in a server build. Components reach the renderer through `IRenderDevice`. Engine services are composed through dependency injection and engine modules.

Optional features ship as [bricks](../../Packages/) to keep the runtime small. Editor tooling belongs in [Editor Core](../../Editor/Core/).
