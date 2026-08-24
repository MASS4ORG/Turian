# ADR 0018: Diffuse GI — baked irradiance volumes

**Status**: Proposed

## Context

`bistro-visual-fidelity.md` phases 1–6 and #157 items 1–3 have shipped: SSAO
delivers contact darkening, SSR delivers on-screen specular, and reflection probes
(ADR-0017) supply local specular wherever SSR holds no information. What remains is
#157 item 4 — indirect *diffuse* light — and it is the final entry in both
documents.

Today the only diffuse indirect term inside `scene.frag.glsl` is the distant
environment:

```glsl
vec3 irradiance  = evalSH(N, ubo.env_sh) * intensity;
vec3 diffuse_ibl = irradiance * albedo / PI * (1.0 - metallic);
```

`env_sh` holds nine order-2 spherical-harmonic coefficients projected from the
scene's single HDRI. It carries no notion of position: a fragment beneath the Bistro's
awning, a fragment inside the shop, and a fragment under open sunlight all receive
the identical hemisphere of sky. SSAO darkens that term but cannot tint it, so there
is no red spill from the awnings onto the stucco and no bounce from the storefront
onto the pavement — and interior surfaces, which see almost no sky at all, end up lit
by a full sky term that SSAO can only attenuate, never replace.

#157 lists four candidate approaches. Three are unavailable or a weak fit:

- **DDGI / RTXGI** requires hardware ray tracing. SDL3 GPU exposes no ray-tracing API
  on any backend — the identical constraint that settled ADR-0017.
- **Lightmaps** require a second UV set plus an atlas. Bistro ships no lightmap UVs,
  and the importer has no unwrapper; building one is a bigger project than the GI
  itself.
- **Voxel cone tracing** is fully dynamic but costly and aliasing-prone, and would
  add a voxelization pass to a renderer already submission-bound at Bistro
  scale (`bistro-performance-132.md`).

That leaves **baked irradiance volumes**, which additionally match the static/dynamic
call #157 asks for and ADR-0017 already made for the same scene and the same reasons.

## Decision

A grid of light probes, baked offline into a versioned asset, sampled per fragment as
a position-dependent replacement for the distant `env_sh` term.

### Authoring: `IrradianceVolumeComponent`

The same field shape and semantics as `ReflectionProbeComponent` and
`PostProcessVolumeComponent`, so volume resolution reuses the established
box/blend_distance/priority model instead of inventing a third one:

- `extents: Vector3` — box half-size along the node's local axes (box only; a sphere
  volume makes no sense for a rectangular probe grid).
- `spacing: f32` — target probe spacing in world units, from which the grid counts
  are derived and clamped, so moving or resizing the volume cannot silently explode
  the probe count.
- `blend_distance`, `priority`, `intensity` — as in `ReflectionProbeComponent`.
- `data: AssetRef(.irradiance)` — the baked result.
- Bake settings: `capture_size` (cube-face resolution for the bake; 16–32 suffices
  for irradiance), `bounces`, `near`/`far`.

### Baked asset: `.irradiance`, magic `TIRR`

A new cooked type, versioned from the first release per ADR-0012 (the mistake
TMSH v1→v2 had to be retrofitted around). Header: format version, grid counts
`[3]u32`, the world→grid transform, and the spacing the bake used. Then, per probe:

- **Order-2 SH irradiance**, nine RGB coefficients. Order 2, *not* the L1 that DDGI
  and Unity's LPPV use: the shader already carries a correct `evalSH` for nine
  coefficients, and the storage decision below renders the coefficient count nearly
  free, so matching the existing distant-env representation costs nothing and keeps
  one evaluation path rather than two.
- **Mean distance and mean squared distance** to the closest geometry, for a
  Chebyshev visibility test.
- **A validity flag** — probes whose capture lands entirely inside geometry are
  excluded from interpolation instead of blended in as black.

The visibility terms are not optional decoration. A naive trilinear grid leaks
sunlight straight through the wall separating the Bistro's exterior and interior,
which is exactly the comparison this sample exists to win.

### Bake: an editor background job

Reflection probes bake at most one probe per frame from within `renderScene`. That
cannot scale here: a 16×8×16 grid means 2,048 probes × 6 faces, three orders of
magnitude more capture work than ADR-0017's handful of probes.

The bake therefore runs as a `TaskManager` job (ADR-0016) with progress and cancel:

1. Extract `reflection_probes.captureFace` into a shared
   `subsystems/render/cube_capture.zig` — it already renders the skybox plus every
   opaque mesh renderer from an arbitrary position with shadows off, precisely what
   a probe capture needs. ADR-0017's probe bake becomes a second caller.
2. For each probe, capture 6 faces at `capture_size` and read them back with
   `gpu.captureTexture`.
3. Project each cube to SH on the CPU. The nine-term basis in
   `assets.computeIrradianceSh` is reused verbatim; only the integration domain
   changes (cube-face texels with solid-angle weights, rather than an equirect
   image's `sin θ dθ dφ`). Extract the basis into a shared helper and add the cube
   integrator alongside the existing equirect one.
4. Accumulate the depth buffer's mean and mean-squared distance per probe during the
   same pass.
5. Repeat for `bounces` iterations, each reading the previous iteration's baked
   irradiance as its ambient term. Two or three iterations produce multi-bounce colour
   bleed; one produces direct-plus-sky only.

Bake is explicit — an Inspector "Bake" action mirroring ADR-0017's "Bake Now" — and
never runs during Play mode.

### Runtime: one storage buffer, blend-then-evaluate

Probes upload into a **fragment storage buffer**, not 3D textures.

The obvious choice would be RGBA16F 3D textures with hardware trilinear filtering,
except hardware filtering is useless here: Chebyshev visibility weighting and backface
rejection need the eight surrounding probes' weights computed individually
before blending. The eight corners must be fetched by hand regardless. Once
they are, a storage buffer costs one binding however many coefficients each
probe carries — which is what makes order-2 affordable — where 3D textures would cost
nine samplers plus two more for visibility, on top of the ten the scene shader
already binds.

The precedent already sits in the shader: scene lights live in a storage buffer at
`set=2, binding=10`, bound once per pass (`root.zig:611`). The probe buffer becomes
slot 1 at `binding=11`, and `pipeline.zig`'s scene fragment shader declares
`num_storage_buffers = 2`.

Per fragment, in `scene.frag.glsl`:

1. Offset the sample position along the surface normal (and slightly along the view
   vector) to push it clear of the surface it rests on — the standard normal-bias that
   stops a probe behind the wall being sampled for a point on the wall's face.
2. Locate the eight surrounding probes, and for each compute a weight: the trilinear
   weight, times a backface term rejecting probes behind the shading point, times the
   Chebyshev term derived from the probe's stored mean/mean-squared distance.
3. Blend the eight probes' nine coefficient sets into one weighted set — 72
   multiply-adds — then call the **existing** `evalSH(N, blended)` once. Blending
   coefficients before evaluation is exact for a linear basis and collapses eight
   evaluations into one.
4. Mix against the distant `env_sh` term by the volume's resolved weight, so a
   fragment outside every volume behaves exactly as it does today.

The existing `ambient * occlusion * ssao` multiply remains. SSAO and the volume compose
rather than double-count: the volume supplies *where the light arrives from*, SSAO the
small-scale contact detail beneath the probe grid's resolution.

The volume itself is resolved per frame on the CPU using the same box/blend/priority
walk `postprocess.resolveVolumes` performs, and its world→grid matrix and weight go in
the frame-constant uniform block that `bistro-performance-132.md` phase 2c introduces
— not the per-draw `FragUB`, which is already too large.

### Editor

- A grid gizmo through `engine/Gizmos.zig` (pure-data, per ADR-0007): the volume box
  plus a dot per probe, greyed for invalid probes.
- An Inspector "Bake" action, and a debug view drawing each probe as a sphere shaded
  by its own irradiance — the standard way to spot a leak before it reaches a
  screenshot.

## Consequences

- **Additive, so no migration.** A new component and a new asset type; no existing
  scene, mesh or material changes meaning. Scenes without a volume render exactly as
  they do today.
- **Static geometry only.** Moving a wall invalidates the bake, and nothing detects
  that automatically — the same constraint ADR-0017 accepted, made more conspicuous by
  the fact that a diffuse bake takes minutes rather than a frame.
- **Bake time is real.** 2,048 probes × 6 faces × `bounces` is a background job
  measured in minutes on Bistro, not a save-time step. It needs progress, cancel, and
  to survive being interrupted.
- **Leaks are the failure mode.** Chebyshev visibility, normal bias and backface
  rejection reduce them; they do not remove them. Expect per-scene tuning of
  spacing and normal bias, and expect the Bistro exterior/interior boundary to be the
  toughest case in the sample.
- **It costs frame time.** Eight probe fetches and 72 madds per fragment is not free
  on a scene that is already slow. This is why it is scheduled after the phases in
  `bistro-performance-132.md`, and why it lands behind a toggle in that document's
  phase-1 feature set.
- **Order-2 is a bet on the storage-buffer decision.** Should probe fetch cost turn out
  to dominate, dropping to L1 is a contained change: fewer coefficients per probe,
  a different `evalSH`, same buffer, same weighting.
- **`cube_capture.zig` becomes shared infrastructure.** Extracting it improves
  ADR-0017's code too, but it does mean a bug there now breaks both probe types.
