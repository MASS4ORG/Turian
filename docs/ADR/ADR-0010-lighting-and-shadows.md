# ADR 0010: Lighting & Shadows

**Status**: Implemented (MVP)

## Context
Engine needs multi-light support (directional, point, spot) and shadow mapping. Must render consistently across both GPU viewport (editor) and software rasterizer (built game) within software limits.

## Decision
- **Light component** (`engine/components/LightComponent.zig`): type (directional/point/spot), color+intensity, range, `spot_angle` (1..89° half-angle), `spot_softness` (0..1), `cast_shadows: bool`. serde fills defaults from `field.defaultValue()`.
- **Clustered forward**: up to 8 lights per draw (FragUB with `Light lights[8]`). Light struct: position(w=type), direction(w=range), color(w=intensity), cone(cos_outer, cos_inner). Inverse-square falloff + spot cone.
- **Shadow mapping** (GPU viewport only): 2048² D16_UNORM, PCF 3×3 via `sampler2DShadow`, depth bias 1.25/1.75, cull NONE. Ortho light VP fit to scene bounds. Runs as separate render pass before main pass in same command buffer.
- **Single shadow-casting light**: only light[0] (directional) casts shadows. Point/spot shadows deferred. Graceful: if shadow resources fail, `shadows_enabled=0`, multi-light remains.
- **Software renderer**: `shadePixel` implements directional/point/spot same math as GLSL. No shadows (documented gap).

## Consequences
- SPIR-V std140 offsets must match Zig extern FragUB exactly (verified manually — Light stride 64).
- Shaders NOT built by zig — recompile via `glslc`.
- Editor viewport lighting visuals not eyeball-checked (shadow acne, orientation).
- IBL/environment, skybox, point/spot shadows, sRGB/linear deferred.
