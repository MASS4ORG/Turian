#version 450

// Depth+normal prepass: single-sample, opaque-only geometry pass that outputs
// view-space normals so SSAO can reconstruct positions with only `inv_proj`.

layout(location = 0) in vec3 in_pos;
layout(location = 1) in vec3 in_normal;
layout(location = 2) in vec2 in_uv;

// SDL3GPU SPIR-V vertex uniforms: set=1, binding=slot_index
layout(set = 1, binding = 0) uniform VertexUB {
    mat4 mvp;
    mat4 model_view; // view * model — composed on the CPU (root.zig), like mvp
} ubo;

layout(location = 0) out vec3 out_view_normal;
layout(location = 1) out vec2 out_uv;

void main() {
    vec4 clip = ubo.mvp * vec4(in_pos, 1.0);
    // Same GL->SDL_GPU z remap as scene.vert.glsl.
    clip.z = (clip.z + clip.w) * 0.5;
    gl_Position = clip;

    // No inverse-transpose: uniform-scale-only transforms suffice here.
    out_view_normal = normalize(mat3(ubo.model_view) * in_normal);
    out_uv = in_uv;
}
