#version 450

layout(location = 0) in vec3 in_pos;
layout(location = 1) in vec3 in_normal;
layout(location = 2) in vec2 in_uv;

// SDL3GPU SPIR-V vertex uniforms: set=1, binding=slot_index
layout(set = 1, binding = 0) uniform VertexUB {
    mat4 mvp;
    mat4 model;
} ubo;

layout(location = 0) out vec3 out_world_normal;
layout(location = 1) out vec2 out_uv;
layout(location = 2) out vec3 out_world_pos;

void main() {
    vec4 world = ubo.model * vec4(in_pos, 1.0);
    out_world_pos = world.xyz;

    vec4 clip = ubo.mvp * vec4(in_pos, 1.0);
    // Vulkan NDC: Y points down, Z in [0,1]
    clip.y = -clip.y;
    clip.z = (clip.z + clip.w) * 0.5;
    gl_Position = clip;

    out_world_normal = normalize(mat3(ubo.model) * in_normal);
    out_uv = in_uv;
}
