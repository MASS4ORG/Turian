# ADR 0013: GPU-Driven Frustum Culling + Indirect Multi-Draw

**Status**: Rounds 1-3 done. Round 1 (per-submesh CPU culling), Round 2
(GPU-driven compute culling + indirect multi-draw), and Round 3 (shadow-pass
culling against the light frustum, see below) all landed. The real Bistro FPS
regression turned out to be dominated not by draw submission but by
**per-frame material resolution**: `resolveMaterial` read each `.material`
file from disk and re-parsed its JSON on every call, and it was called ~264×
per frame (once per material group in the draw loop, once per material in the
per-frame `uploadNewAssets` texture scan). A GUID-keyed resolved-material cache
(`state.resolved_materials`, invalidated on save/reimport) eliminates that
disk+parse storm — the actual FPS lever for this scene, found by reading the
code path rather than more culling rounds. See
`docs/decisions/frustum-culling-143.md` for the full writeup.

## Context
#143's per-object frustum culling (docs/decisions/frustum-culling-143.md, Phase
1) didn't move Bistro's FPS off near-zero. Root cause, confirmed against the
real project data (`../turian-samples/bistro/assets/scene.prefab`): Bistro's
exterior is a **single `SceneNode`** with a **single `mesh_renderer`**
component — one `GpuMesh`, 1,591 submeshes, all sharing one vertex/index
buffer. Per-object culling tests one AABB spanning the entire building
complex; from anywhere near it, that box is always "in frustum," so every
submesh still draws every frame regardless of camera direction. Culling has to
be per-submesh to have any effect on this scene.

Since every submesh here already shares one buffer, this is close to the
textbook setup for GPU-driven rendering rather than a CPU per-submesh loop
patched on top of the existing architecture.

**SDL3 GPU capability, checked directly against the vendored header**
(`SDL_gpu.h`, `zig-out/include/SDL3/`): full compute pipeline support
(`SDL_CreateGPUComputePipeline`, `SDL_BeginGPUComputePass`,
`SDL_DispatchGPUCompute`), storage buffers writable from compute
(`SDL_GPU_BUFFERUSAGE_COMPUTE_STORAGE_WRITE`), and indexed indirect draws
(`SDL_DrawGPUIndexedPrimitivesIndirect`, buffer of
`SDL_GPUIndexedIndirectDrawCommand{num_indices, num_instances, first_index,
vertex_offset, first_instance}`). No compute shader exists anywhere in this
codebase yet — this is new infrastructure, not an extension of an existing
pattern. Shaders are hand-compiled via `glslc` and embedded with
`@embedFile` (`subsystems/render/pipeline.zig`); not wired into `build.zig`.

**Known SDL3 GPU gap**: `draw_count` for
`SDL_DrawGPUIndexedPrimitivesIndirect` is a fixed CPU-side `Uint32` at record
time — there is no Vulkan-`vkCmdDrawIndexedIndirectCountKHR`-style variant
that reads the count from a GPU buffer. Worked around with the standard
technique: always dispatch the full command count for a material group, and
have the compute pass zero `num_instances` for culled entries instead of
omitting them — a "zero-instance" sub-draw costs effectively nothing on the
GPU.

## Decision
Move visibility culling and draw submission off the CPU per-submesh loop
entirely, in three rounds:

1. **Correctness fix (CPU-side)**: per-submesh AABBs computed at cook time
   (currently only a whole-mesh AABB exists, from #143 Phase 1), CPU tests
   each submesh individually against the frustum instead of the whole mesh
   renderer. Still issues individual `SDL_DrawGPUIndexedPrimitives` calls.
   Needed regardless of Round 2/3 — validates the granularity fix cheaply
   before committing to new GPU infrastructure.
2. **GPU-driven culling + indirect multi-draw**: upload per-submesh
   `{min, max, material_slot}` into a compute-readable storage buffer per
   mesh. A new compute shader (first in this codebase) tests every submesh's
   world-space AABB against the frustum each frame and writes one
   `SDL_GPUIndexedIndirectDrawCommand` per submesh into a per-frame indirect
   buffer (`num_instances = 1` visible, `0` culled). The render pass replaces
   the CPU submesh loop with one `SDL_DrawGPUIndexedPrimitivesIndirect` call
   per material group (submeshes sorted/grouped by material once at cook
   time) — this also satisfies #143's original "material-sorted batching"
   scope as a side effect of the grouping, not a separate CPU sort pass.
3. **Shadow pass** (done): a second cull dispatch per shadow-casting mesh
   against the **light** frustum writes a parallel `shadow_indirect_buf`
   (`gpu_cull.dispatchShadowCulls`, run on the shadow pass's own command buffer
   in `gpu_timing.runShadowPass` so ordering holds even under per-pass
   fencing). `shadow.renderShadowPass` then issues one
   `SDL_DrawGPUIndexedPrimitivesIndirect` over all submeshes instead of one
   whole-mesh draw, falling back to the whole-mesh draw if the cull didn't run.
   **Caveat (measured, not assumed)**: for a single directional light whose
   ortho frustum is fit to the whole scene bounds, nothing is actually outside
   that frustum, so this culls ~nothing for Bistro — implemented for
   architectural completeness and future tighter/spot-light shadow frustums,
   not as a Bistro FPS win. The real Bistro win was the material cache above.

## Consequences
- New GPU resource category (compute pipelines, storage buffers, indirect
  buffers) needs lifecycle management in `subsystems/render/state.zig`
  alongside existing mesh/texture caches, and cleanup in `deinit()`.
- A new manual `glslc`-compiled `.comp` shader join the existing
  hand-compiled `.spv` set — same workflow as ADR-0010's vert/frag shaders,
  no new build automation.
- CPU-side material-switch/draw-count profiler counters
  (`engine.Profiler.Counters`) need reinterpretation once draws become
  indirect multi-draws: "draw calls" stops meaning "state changes," and a new
  counter (visible-vs-total submesh count from the compute pass) becomes the
  more meaningful signal — `objects_drawn`/`objects_culled` added in #143
  Phase 1 will need a submesh-granularity successor.
- Editor viewport and shipped game share `renderScene`
  (`subsystems/render/root.zig`), so this applies to both uniformly, same as
  #143 Phase 1 — no separate game-only or editor-only path.
- The software renderer fallback (`engine/SoftwareRenderer.zig`, used when no
  GPU backend is available) is untouched by any of this and gets none of the
  benefit — pre-existing limitation, not a regression.
