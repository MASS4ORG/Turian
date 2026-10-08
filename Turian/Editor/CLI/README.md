# Turian CLI

Headless tools for project compilation, asset import, builds, scene diagnostics and brick distribution.

From the repository root:

```sh
dotnet run --project Turian/Editor/CLI -- --help
```

Requires .NET 10. Rendering commands also require Vulkan. Commands share their implementation with Studio through [Editor Core](../Core/).

## Frame statistics

The optional UI brick provides a **Frame Statistics HUD** component (`FrameStatisticsHudComponent`), authored on a scene node through **Add Component → UI → Frame Statistics HUD**. Bistro includes a node named **Frame Statistics HUD**. Its Inspector exposes **Show Hud**, position, size and font size; disabling or removing the component also disables its frame collection. The GUI runs only during Play in the Game view or game player. Stopped previews and the Scene viewer exclude it. The HUD shows its latest 60 frames: actual FPS and frame interval, render CPU elapsed time, submitted standard geometry draws and triangles, CPU frustum culling, material descriptor binds and rendering-thread managed allocations. Numeric formatting uses BCL spans and the drawable reuses ASCII glyphs.

Capture Bistro measurements without opening a window:

```sh
dotnet run --project Turian/Editor/CLI -- playmode ../Turian-examples/example-02-bistro --frames 120 --stats /tmp/bistro-stats.json
```

`--stats` enables offscreen rendering even when `--out` is omitted. Add `--out /tmp/bistro.png` to save the final image. Use an already imported project; asset import/compilation and device creation are outside the measured scene load. The JSON report includes scene read/deserialization/prefab expansion/awake time, cold model and texture decode/upload attempt counts and accumulated times, each frame's actual tick-and-render wall time, managed allocations and render preparation/submission scopes. The first frame can include material and texture loading; inspect the samples and median alongside the mean. Fixed simulation delta time is never used to calculate headless performance FPS.

Render CPU and submission elapsed times include the offscreen GPU completion wait and image staging transfer. Pixel copying and PNG encoding are excluded. Draw calls and triangle counts cover the standard geometry pass; editor gizmos and UI are excluded. With GPU occlusion enabled these are submitted indirect commands and candidate triangles, not the number ultimately executed by the GPU. HUD FPS includes the editor's frame rate cap, other panels and preview cameras.

Texture payload bytes are the existing process-wide cached texture data total, including mip levels; they are not an allocator residency or total GPU memory measurement. GPU timestamps and allocator buffer/texture/render-target totals are unavailable and explicitly `null` in JSON. GPU timestamp queries remain deferred to the graphics boundary and profiler work: Vulkan requires queue-family timestamp support, query pool ownership/reset, valid-bit handling and conversion by `timestampPeriod`. See the [Khronos timestamp sample](https://docs.vulkan.org/samples/latest/samples/api/timestamp_queries/README.html). CPU stopwatch scopes cannot isolate GPU execution.
