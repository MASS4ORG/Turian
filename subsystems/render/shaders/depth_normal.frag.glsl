#version 450

// Depth+normal prepass fragment: opaque-only, mirroring scene.frag for
// alpha-cutout discard so masked foliage doesn't occlude as a solid quad.

layout(location = 0) in vec3 in_view_normal;
layout(location = 1) in vec2 in_uv;

layout(set = 2, binding = 0) uniform sampler2D albedo_tex;

// SDL3GPU SPIR-V fragment uniforms: set=3, binding=slot_index.
layout(set = 3, binding = 0) uniform FragUB {
    vec4 flags; // x=has_albedo, y=alpha_cutoff, z=alpha_mask_on, w unused
    vec4 base_color; // rgba — the material's constant tint, for the cutout test
} ubo;

layout(location = 0) out vec4 out_normal;

void main() {
    if (ubo.flags.z > 0.5) {
        float a = ubo.base_color.a;
        if (ubo.flags.x > 0.5) a *= texture(albedo_tex, in_uv).a;
        if (a < ubo.flags.y) discard;
    }

    out_normal = vec4(normalize(in_view_normal) * 0.5 + 0.5, 1.0);
}
