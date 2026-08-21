#version 450

// Vertex stage of the alpha-cutout shadow pipeline: the same light-space
// transform as shadow.vert.glsl, plus the UV its cutout discard needs.

layout(location = 0) in vec3 in_pos;
layout(location = 1) in vec3 in_normal; // unused (kept for shared vertex layout)
layout(location = 2) in vec2 in_uv;

// SDL3GPU SPIR-V vertex uniforms: set=1, binding=slot_index.
layout(set = 1, binding = 0) uniform ShadowUB {
    mat4 light_mvp; // light_vp * model
} ubo;

layout(location = 0) out vec2 out_uv;

void main() {
    out_uv = in_uv;
    vec4 clip = ubo.light_mvp * vec4(in_pos, 1.0);
    // SDL_GPU's unified NDC is Y-up (it auto-converts per backend); only the
    // Z range needs remapping here, from our GL-style [-1,1] to SDL_GPU's [0,1].
    clip.z = (clip.z + clip.w) * 0.5;
    gl_Position = clip;
}
