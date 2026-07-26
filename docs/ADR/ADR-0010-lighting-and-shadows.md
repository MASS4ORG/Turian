# ADR 0010: Lighting & Shadows

**Status**: Implemented (MVP)

## Context
Engine needs multi-light support (directional, point, spot) and shadow mapping. Must render consistently across both GPU viewport (editor) and software rasterizer (built game) within software limits.

## Decision
- **Light component** (`engine/components/LightComponent.zig`): type (directional/point/spot), color+intensity, range, `spot_angle` (1..89° half-angle), `spot_softness` (0..1), `cast_shadows: bool`. serde fills defaults from `field.defaultValue()`.
- **Clustered forward**: up to 8 lights per draw (FragUB with `Light lights[8]`). Light struct: position(w=type), direction(w=range), color(w=intensity), cone(cos_outer, cos_inner). Inverse-square falloff + spot cone.
- **Cascaded shadow mapping** (GPU viewport only, #132 phase 4): 4 cascades, each a 2048² D16_UNORM strip of one shadow *atlas* texture (not a texture array — SDL_GPU asserts array textures can't carry `DEPTH_STENCIL_TARGET` usage). Practical split scheme (log/uniform blend, λ=0.5) between the camera near plane and `min(camera far, scene AABB diameter)`. Each cascade's ortho frustum is fit to a bounding sphere of its camera sub-frustum corners (stable under camera yaw) and texel-snapped in light space (stable under camera translation). PCF 3×3 via `sampler2DShadow` against the cascade's atlas strip, with a world-unit bias (5cm–35cm by grazing angle) converted per-cascade through `cascade_depth_scale` — a single NDC bias doesn't work once cascades span wildly different world extents. Rasterizer depth bias 1.25/1.75, cull NONE unchanged. Each cascade has its own GPU-culled indirect draw buffer (own frustum, not one whole-scene superset) — see `gpu_cull.dispatchShadowCulls`. Runs as one render pass (viewport/scissor restricted per cascade strip) before the main pass, in the same command buffer.
- **Single shadow-casting light**: only light[0] (directional) casts shadows. Point/spot shadows deferred. Graceful: if shadow resources fail, `shadows_enabled=0`, multi-light remains.
- **Software renderer**: `shadePixel` implements directional/point/spot same math as GLSL. No shadows (documented gap).
- **sRGB/linear color management** (#27): albedo/emissive sample as sRGB (GPU hardware decode for DDS/KTX2; a small envelope tag baked in at import for stb_image sources) and lighting runs in linear space; `scene.frag.glsl` and the software renderer both apply an ACES filmic tonemap + gamma-2.2 encode at output, since render targets/swapchain stay UNORM. Normal/metallic-roughness/occlusion maps referenced by a glTF/FBX material default to linear on first import.

## Consequences
- SPIR-V std140 offsets must match Zig extern FragUB exactly (verified manually — Light stride 64).
- Shaders NOT built by zig — recompile via `glslc`.
- IBL/environment, skybox, point/spot shadows deferred.
- CSM's per-cascade GPU cull doesn't shrink the shadow pass's indirect-command count (still `submeshes.len` per cascade — SDL_GPU has no GPU-side indirect draw-count readback, per ADR-0013), only the per-instance vertex work. On a very-high-submesh-count scene this command-count multiplication by cascade count is a plausible cost worth watching, but headless (`TURIAN_CAPTURE_AFTER_MS`) FPS readings are not a valid way to measure it — dvui only redraws on real input/timers in that mode, so its on-screen counter isn't real steady-state frame timing (see #147). Confirm with a live interactive session before treating any headless FPS number here as a regression signal.
- `NUM_CASCADES` is hardcoded to 4 (`subsystems/render/types.zig`) and load-bearing in three places that must stay in sync: the Zig constant, the `FragUB`/`cascade_vp` array size, and the shader's `#define NUM_CASCADES` in `scene.frag.glsl` — changing it means editing and recompiling all three together.
