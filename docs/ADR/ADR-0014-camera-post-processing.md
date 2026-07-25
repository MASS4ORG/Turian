# ADR 0014: Camera Post-Processing (Vignette, Color Grading, Bloom)

**Status**: Implemented (GPU viewport / shipped game; software renderer
untouched). Settings ownership since redesigned from flat `CameraComponent`
fields (as originally shipped) to a separate `PostProcessVolumeComponent` —
see "Settings" below.

## Context
#136 asked for a composable camera post-process stack — vignette, color
grading, bloom — applied after the lit pass and before/combined with the
existing ACES tonemap (#27), targeting Unity-level quality for the Bistro
milestone's marketing screenshots.

Before this, the renderer had **no HDR intermediate and no fullscreen-pass
infrastructure**: the main pass rendered straight into an `R8G8B8A8_UNORM`
`color_tex` — which for the shipped game *is* the swapchain texture itself,
and for Studio is the viewport's offscreen backing texture — with ACES
tonemap + gamma baked into the tail of `scene.frag.glsl`/`skybox.frag.glsl`.
Real bloom needs a bright-pass extract off unclamped HDR data before
tonemapping, so this required introducing an HDR render target and a new
composite pass, comparable in weight to ADR-0013's first compute shader.

## Decision

### HDR pipeline
The main pass now renders into an `R16G16B16A16_FLOAT` HDR target
(`state.HDR_COLOR_FORMAT`) instead of the caller's UNORM `color_tex` directly.
`scene.frag.glsl` and `skybox.frag.glsl` emit raw linear HDR (tonemap/gamma
removed from both). A new composite pass (`composite.frag.glsl`) reads the
HDR target, applies bloom → per-channel grading → vignette → ACES tonemap →
gamma, and writes the final UNORM result.

### Bloom: mip-chain dual filter
Full mip-chain bloom (Call of Duty: Advanced Warfare's "Next Generation Post
Processing" technique), not a fixed-radius single blur, so `bloom_radius` has
a real, visible effect:
1. **Threshold** (`bloom_threshold.frag.glsl`): soft-knee bright-pass extract,
   full-res HDR → half-res mip0.
2. **Downsample chain** (`bloom_downsample.frag.glsl`): 13-tap box/tent
   filter, mip[i] → mip[i+1], up to `MAX_BLOOM_MIPS = 6` or until a mip
   dimension would drop below ~16px.
3. **Upsample chain** (`bloom_upsample.frag.glsl`): 3×3 tent filter,
   mip[i] → mip[i-1], **additively blended via GPU blend state**
   (`pipeline.blendStateFor(.additive)`, reused as-is) rather than shader
   math — mip0 ends up holding the combined result.

All bloom mips share **one** downsample pipeline and **one** upsample
pipeline object across every mip size and viewport resolution — SDL3 GPU
pipelines bake in format + sample count only, never texture dimensions.

**Gotcha (cost a real debugging pass — worth flagging for future fullscreen
passes)**: `fullscreen.vert.glsl`'s original UV derivation (`out_uv = pos`)
produced a vertically flipped image in every pass that samples a
previously-rendered target. SDL_GPU exposes a "unified Y-up" NDC to vertex
shaders (see `scene.vert.glsl`'s own comment) but implements it via an
internal flip against its Y-down-native backends (Vulkan/D3D12/Metal) — so a
render target's texel row 0 (`v=0`) is the *top* of what's displayed, not the
bottom. The fix was `out_uv = vec2(pos.x, 1.0 - pos.y)`. A tempting but wrong
way to "verify" this: comparing against `scene.frag.glsl`'s shadow-map UV
convention (`uv = light_clip.xy*0.5+0.5`, no flip) — that comparison proves
nothing, because a shadow map is never displayed directly, so a *consistent*
flip on both its write and read side is invisible either way.

### Color grading: per-channel RGB Lift/Gamma/Gain
`hdr = hdr * gain + lift * (1 - hdr); hdr = pow(hdr, 1/gamma)` — the ASC CDL
slope/offset/power form, applied per-channel (matching Unity's own grading
wheels, so color *balance* can shift, not just brightness). The weighted lift
term avoids blowing out highlights at high lift values.

### Settings: `PostProcessVolumeComponent` (global or area-driven), blended

Originally shipped as 15 flat fields directly on `CameraComponent`. Redesigned
Unity-style: a separate `PostProcessVolumeComponent`
(`engine/components/PostProcessVolumeComponent.zig`), global (affects the
whole scene) or local (a box/sphere region around the node's `Transform`).
Effects are grouped into per-category nested structs — `VignetteSettings`,
`ColorGradingSettings`, `BloomSettings`, each with its own `enabled` flag —
rather than flat prefixed fields; the Inspector already renders nested struct
fields as automatic collapsible expanders (`drawNestedStruct` in
`studio/inspector/PropDraw.zig`) and `Vector3` fields as X/Y/Z-colored number
rows, so no bespoke Inspector code was needed for either the categorization or
`ColorGradingSettings.lift/gamma/gain` being `Vector3`s (chosen over named
r/g/b scalars for the free color-coded grouping, at the cost of losing
slider/range-clamp UX — `PropDrawMath.drawVec3Row` has no min/max support).

**Blending** (`subsystems/render/postprocess.zig`'s `resolveVolumes`, called
from `root.sceneCamera` after any editor free-cam override is applied, so the
editor camera correctly previews volumes as you fly through them): every
active volume affecting the camera is collected (weight 1 for global volumes;
for local ones, 1 inside the shape falling off linearly to 0 across
`blend_distance` outside it — an oriented box SDF or point-to-sphere
distance), sorted ascending by `priority`, then blended via a sequential
per-category lerp (`result = lerp(result, volume, weight)`) — a
higher-priority volume at weight 1 fully overrides what came before it. Each
of vignette/grading/bloom blends independently, only among volumes with that
category's own `enabled = true`, so one volume can override just one category.

**Extension API**: `postprocess.registerEffect` lets Zig/plugin code inject a
custom post-process pass, in registration order, after bloom generation but
before the composite step — operating in HDR space (ping-ponged through
cached scratch textures so `src != dst` always holds), independent of bloom
(which always sources from the original, pre-custom-effects HDR buffer).
Mirrors `root.setSources`'s plain global-registration precedent rather than
introducing a `Services`/`Frame`-based extension point.

`CameraComponent` is back to just `fov`/`near`/`far`/`orthographic` — the 15
post-process fields are gone with no migration path (every instance in this
checkout was at neutral defaults; the fields being orphaned in existing scene
JSON is a deliberate, documented break, not an oversight).

### Gizmos and pass ordering
Gizmos must draw crisp/undistorted after post-processing, but SDL3 GPU has
**no depth-resolve** — there's no cheap way to depth-test gizmos against a
single-sample copy of the scene's multisampled depth. Gizmos therefore still
share the scene's multisampled color+depth exactly as before (only the shared
color format changed, from UNORM to HDR), and now resolve into the **HDR**
target rather than the final `color_tex`. Post-processing became an explicit
final step the caller invokes:

- `root.renderScene(cmd, w, h, objects)` — no longer takes a destination
  texture; renders into the internal HDR target only.
- `root.hdrColorFor(w, h)` — exposes that HDR target so a caller can draw
  gizmos into it via `root.renderGizmos`.
- `root.runPostProcess(cmd, color_tex, w, h, objects)` — composites HDR (+ any
  gizmos drawn into it) into the final `color_tex`. Called once per frame,
  after `renderScene` and any `renderGizmos` calls for that target.

This is a breaking change to `render`'s internal Zig API (not a serialized
asset format), applied at every call site in the same commit: Studio's
`GpuRenderer.zig` (viewport, game panel, asset preview, thumbnail capture) and
the shipped-game codegen (`MainZigCodegen.zig`).

## Consequences
- New GPU resource categories in `state.zig`: the HDR resolve target
  (extends `DepthTarget`), a bloom mip-chain cache (`BloomChain`, same
  per-(w,h) round-robin-eviction pattern as `depth_targets`), and 4 new
  graphics pipelines — lifecycle managed in `subsystems/render/postprocess.zig`
  / `postprocess_pipeline.zig` (split from `pipeline.zig`/`root.zig` to stay
  under this repo's file-size convention).
- Gizmos are still subject to vignette/grading/bloom (they share the
  pre-post-process HDR buffer) — a known limitation, not a regression from
  before (gizmos were never post-process-aware previously either, since no
  post-process existed). A proper fix would need a manual MSAA depth-resolve
  pass or a dedicated depth prepass; out of scope here.
- `engine/SoftwareRenderer.zig`'s CPU path is untouched — no post-processing
  there, same precedent as shadows (#136's own stated scope).
- Profiler counters are unaffected (post-process passes aren't part of the
  existing draw-call/material-switch counters); a future pass could add
  bloom-specific timing under the existing per-pass zone convention
  (`engine.Profiler.zone("render.postprocess")` already added).
- **Two pre-existing latent bugs surfaced and fixed while adding
  `PostProcessVolumeComponent`** (the first built-in component with multiple
  `Vector3` fields in one struct going through the generic Inspector
  reflection walker):
  - `PropDrawMath.drawVec3`/`drawVec3Row` had no `id`-based widget
    disambiguation (unlike the sibling `drawVec2`/`drawVec4`, which do this
    correctly with inline `id_extra` math) — every `Vector3` field drawn via
    the generic struct walker shared the same widget id, so a struct with 2+
    `Vector3` fields (or a user script / material with 2+ vector params
    drawn via the same legacy `drawVec3Row`/`drawVec2Row`/`drawVec4Row`
    helpers) could see one field's edit bleed into another's. Fixed by
    threading `id` through and applying `id_extra = id*3 + 0/1/2` (etc.),
    all 5 call sites updated.
  - `SceneNode` is ~124 KB (`engine/scene/SceneNode.zig`'s own doc comment) —
    `UndoRedo.pushCommand`'s `modify_object` mutation copies it multiple
    times as locals inside the Inspector's already non-trivial dvui widget
    recursion. Combined with the extra recursion depth a nested-struct
    component adds, this reliably overflowed the default 8 MiB stack.
    Mitigated (not redesigned — that's a separate, larger undo-snapshot
    concern) by raising the soft `RLIMIT_STACK` toward its hard limit at
    Studio startup (`studio/Main.zig`'s `raiseStackLimit`, Linux/macOS only;
    a no-op on Windows, where `SceneNode`'s size is the same risk but a
    different mitigation would be needed if it manifests there).
