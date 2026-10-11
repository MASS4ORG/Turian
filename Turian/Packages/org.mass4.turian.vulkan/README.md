# Vulkan renderer

The render backend of Turian games: Vulkan device, swapchain, render systems, GPU asset caches, the GLFW window and window input. New projects install it; the [UI](../org.mass4.turian.ui/) and [GPU occlusion culling](../org.mass4.turian.hzb/) bricks depend on it.

The brick is `clientOnly`: a server build profile leaves it out, along with every brick depending on it, and the game then runs the engine's headless player. Without it a game has no window, GPU or native graphics library.

Its types keep the `Turian.Engine.Core` namespace. Scene components reach the GPU only through `IRenderDevice`; the brick's player registers `Vulkan` as that service.

## Build

Sources are under `Source~/Turian.Engine.Vulkan`, with shaders compiled by `./build.sh compile-shaders`. The solution build copies the library into `Precast~/lib`. The Studio and command line reference the project directly, since the editor always renders.
