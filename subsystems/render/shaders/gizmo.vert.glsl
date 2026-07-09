#version 450

// Expanded gizmo line vertex. Each segment is drawn as two triangles; every
// corner carries both segment endpoints, the corner's color/thickness, which
// side of the line it sits on, and which endpoint it sits at. We project both
// endpoints, then push this corner perpendicular to the segment by the
// thickness (in pixels) to build a constant-width, screen-facing quad.
layout(location = 0) in vec3 in_a;
layout(location = 1) in vec3 in_b;
layout(location = 2) in vec4 in_color;
layout(location = 3) in float in_thickness; // pixels
layout(location = 4) in float in_side;      // -1 / +1
layout(location = 5) in float in_end;       // 0 = at A, 1 = at B

// SDL3GPU SPIR-V vertex uniforms: set=1, binding=slot_index.
layout(set = 1, binding = 0) uniform GizmoUB {
    mat4 view_proj;
    vec2 viewport; // pixels
    vec2 _pad;
} ubo;

layout(location = 0) out vec4 out_color;

void main() {
    vec4 ca = ubo.view_proj * vec4(in_a, 1.0);
    vec4 cb = ubo.view_proj * vec4(in_b, 1.0);
    vec4 clip = (in_end < 0.5) ? ca : cb;

    // Endpoints in pixel space (guard against the near-plane w → 0).
    float wa = abs(ca.w) < 1e-5 ? 1e-5 : ca.w;
    float wb = abs(cb.w) < 1e-5 ? 1e-5 : cb.w;
    vec2 px_a = (ca.xy / wa) * 0.5 * ubo.viewport;
    vec2 px_b = (cb.xy / wb) * 0.5 * ubo.viewport;

    vec2 dir = px_b - px_a;
    float len = length(dir);
    dir = len > 1e-6 ? dir / len : vec2(1.0, 0.0);
    vec2 nrm = vec2(-dir.y, dir.x);

    // Offset this corner by half the line width, converted from pixels back to
    // clip space (multiply by w so it survives the perspective divide).
    vec2 offset_px = nrm * in_side * (in_thickness * 0.5);
    vec2 ndc_off = offset_px / (0.5 * ubo.viewport);
    clip.xy += ndc_off * clip.w;

    // SDL_GPU's unified NDC is Y-up (it auto-converts per backend); only the
    // Z range needs remapping here, from our GL-style [-1,1] to SDL_GPU's [0,1].
    clip.z = (clip.z + clip.w) * 0.5;
    gl_Position = clip;
    out_color = in_color;
}
