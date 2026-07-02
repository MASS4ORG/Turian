#version 450

// Depth-only vertex shader for the directional-light shadow map pass.
// Renders scene geometry from the light's point of view; only depth is kept.
// The clip-space remap here must match shadowFactor() in scene.frag.glsl.

layout(location = 0) in vec3 in_pos;
layout(location = 1) in vec3 in_normal; // unused (kept for shared vertex layout)
layout(location = 2) in vec2 in_uv;     // unused

// SDL3GPU SPIR-V vertex uniforms: set=1, binding=slot_index.
layout(set = 1, binding = 0) uniform ShadowUB {
    mat4 light_mvp; // light_vp * model
} ubo;

void main() {
    vec4 clip = ubo.light_mvp * vec4(in_pos, 1.0);
    // Vulkan NDC: Y points down, Z in [0,1].
    clip.y = -clip.y;
    clip.z = (clip.z + clip.w) * 0.5;
    gl_Position = clip;
}
