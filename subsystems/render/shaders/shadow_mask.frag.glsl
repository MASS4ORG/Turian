#version 450

// Fragment stage of the alpha-cutout shadow pipeline: discards texels below the
// material's cutoff so masked foliage casts a leaf-shaped shadow instead of the
// shadow of its solid quad. The cutout test mirrors scene.frag.glsl's exactly,
// or the shadow would not line up with the lit surface. The pipeline has no
// colour targets, so surviving fragments emit nothing but depth.

layout(location = 0) in vec2 in_uv;

layout(set = 2, binding = 0) uniform sampler2D albedo_tex;

// SDL3GPU SPIR-V fragment uniforms: set=3, binding=slot_index.
layout(set = 3, binding = 0) uniform FragUB {
    vec4 flags; // x=has_albedo, y=alpha_cutoff, zw unused
    vec4 base_color; // rgba — the material's constant tint, for the cutout test
} ubo;

void main() {
    float a = ubo.base_color.a;
    if (ubo.flags.x > 0.5) a *= texture(albedo_tex, in_uv).a;
    if (a < ubo.flags.y) discard;
}
