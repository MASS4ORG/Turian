# ADR 0017: Reflection Probes

**Status**: Implemented (v1)

## Context

SSR (ADR-adjacent, #132 phase 5) mirrors only what appears on-screen in last
frame's composited color, reprojected. That fundamentally cannot cover interior
reflections, hidden surfaces, or anything off-screen — the Bistro
storefront glass and interior keep mirroring the distant HDRI
(`env_prefiltered`) rather than the street or room genuinely in front of them.
Reflection probes (#157 item 3) close that hole with a baked local cubemap per
placed probe, blended in ahead of the global HDRI fallback.

The renderer is single-pass forward, static-scene-oriented (Bistro), and
SDL3 GPU offers no ray-tracing API — ruling out every dynamic/RT-based
approach. A bake-once, static local cubemap is the sole option that fits.

## Decision

- **`ReflectionProbeComponent`** (`engine/components/ReflectionProbeComponent.zig`):
  box/sphere influence volume (`is_global`, `shape`, `extents`, `radius`,
  `blend_distance`, `priority`) — precisely the fields and semantics
  `PostProcessVolumeComponent` already applies to camera volumes — plus
  `capture_size`, `near`, `far`, `intensity` for the bake itself.
- **Capture reuses the existing forward opaque draw path**
  (`draw.buildDrawParams`/`submitDraw`, `culling.aabbOutsideFrustum`),
  rendered from the probe's location into 6 cube faces
  (`subsystems/render/reflection_probes.zig:captureFace`), then GGX-prefiltered
  through the *same* machinery the global HDRI relies on
  (`ibl_prefilter.prefilterCubemap`, split out of `prefilterEnvironment` for
  this reuse). Capture is opaque-only (transparent groups skipped — they are
  never added to `draw.transparent_draws`, the main pass's own frame-global
  scratch array, preventing cross-pass corruption) and shadow-off (cascades are
  fit to the main camera's sub-frustum, with no guarantee of covering what a probe
  sees — a whole-scene shadow projection for capture is a believable v2, not
  built here).
- **Static, bake-once, throttled**: `bakeDirty` runs from `renderScene` every
  frame yet only genuinely captures a probe when its cached transform/shape/
  capture-size diverges from its component's live values (or it is new), and
  bakes at most one dirty probe per call — never re-baked during Play mode, never
  a per-frame cost once a scene's probes have settled. An explicit "Bake Now"
  Inspector action (`reflection_probes.invalidate`) forces a rebake for situations
  where the *scene* surrounding a static probe changed without the probe moving.
- **Storage is a fixed array of persistent, fully-baked slots**
  (`state.MAX_REFLECTION_PROBES = 8`, `state.ReflectionProbeSlot`), keyed by
  the owning `SceneNode`'s GUID — intentionally not the transient
  evict-cursor pattern `MAX_SSR_TARGETS`/`ssr_targets` follows, because probes are
  many, simultaneous, and persistent instead of one-per-resolution and
  disposable. A single pooled scratch capture pair
  (`probe_capture_base`/`probe_capture_depth`) is reused sequentially across
  probes and faces, bounding VRAM whatever `MAX_REFLECTION_PROBES` is set to.
  `probe_capture_depth` is intentionally a plain non-array 2D texture — SDL_GPU
  forbids `DEPTH_STENCIL_TARGET` on array textures, the same limitation the
  cascaded shadow atlas (ADR-0010) already works around; only the captured
  *color* cubemap requires cube layout.
- **Probe selection is CPU-side, per-submesh, cached.** Bistro is two enormous
  multi-material mesh nodes ("Bistro Exterior"/"Bistro Interior"), so
  deciding "which probe applies" per *node* would choose one probe for an
  entire building — it has to happen per submesh, mirroring
  `postprocess.zig`'s `resolveVolumes`/`volumeWeight` box/sphere/
  blend_distance/priority model (`reflection_probes.bestProbeMatch`). The
  outcome is cached on `state.GpuSubmesh` (`probe_gen`/`probe_slot`/
  `probe_weight`) behind `state.reflection_probes_generation`, a counter
  incremented on every successful bake — given that probes and geometry are both
  static, an unchanged generation means the cached assignment remains valid and
  requires no re-test. The cache holds the winning probe's *slot index*, not
  its texture pointer: a rebake releases and recreates the GPU texture object
  even when the winning assignment stays the same, so the pointer is
  re-derived from the slot afresh on every call.
  GPU-indirect multi-draw groups share one draw call and cannot vary a texture
  binding per submesh inside it, so that path resolves once per material
  group against the group's first submesh as a representative point.
  Transparent submeshes (drawn individually whichever path is taken, for back-to-
  front sort) resolve per submesh exactly.
- **Shading**: `scene.frag.glsl` gains a 10th fragment sampler
  (`probe_prefiltered`, binding 9 — the `LightBuffer` SSBO moves to binding
  10) and a `probe_params` uniform (weight, mip_count). The resolved probe
  blends into the ambient specular term *before* SSR's existing roughness-
  gated mix (`mix(prefiltered, probe_color, ubo.probe_params.x)`, then SSR
  mixes on top of that) — preserving priority SSR > local probe > global env
  without a second gating axis. This sits within the existing
  `env_params.z > 0.5` (has-environment) gate, so a scene holding probes but no
  `EnvironmentComponent` receives no specular IBL whatsoever, probes included — a
  genuine gap for non-Bistro projects, tolerable because Bistro always has an
  HDRI (open question, not resolved here).
- **Editor**: gizmo visualization reuses `engine.Gizmos.box`/`wireSphere`
  directly (no new gizmo primitives, ADR-0007-compliant) behind a
  `GizmoSystem.show.probes` toggle. Inspector editing comes free through the generic
  `turian_hints`-driven field walker; only the "Bake Now" button needed
  hand-written wiring.

## Consequences

- Every scene fragment shader draw now binds one additional sampler
  (`probe_prefiltered`) and pushes 16 further uniform bytes, even in scenes with
  no reflection probes placed (bound to a black-cubemap-equivalent fallback
  at weight 0 — no visual difference, small fixed per-draw cost).
- `MAX_REFLECTION_PROBES = 8` and the probe prefilter resolution
  (`64px/5 mips`, against the global env's `128px/6`) are hardcoded, not
  authorable — consistent with this renderer's remaining internal tunables (SSR's
  `THICKNESS`/`STEP_SCALE`, SSAO's `RADIUS`/`BIAS`/`POWER`). Revisit should VRAM
  or visual quality demands differ once running against real content.
  Placing more than 8 probes in a scene quietly stops baking the surplus
  (`log.warn` once) — no Inspector-side budget indicator yet.
- Capture omits shadows entirely — a probe's captured reflection will appear
  slightly flatter/brighter than the equivalent on-screen shading, which does
  receive cascaded shadows. Acceptable for a first pass; revisit should it read
  as wrong in practice.
- Shaders NOT built by zig — recompile `scene.frag.glsl` via `glslc`.
