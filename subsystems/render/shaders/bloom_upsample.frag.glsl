#version 450

// 3x3 tent-filter upsample: mip[i] -> mip[i-1], additively blended onto the
// existing downsample content at that level via GPU blend state (see
// `pipeline.blendStateFor(.additive)`), not shader math. `radius` widens the
// tent's footprint, giving `bloom_radius` a visible effect on glow spread.

layout(location = 0) in vec2 in_uv;

layout(set = 2, binding = 0) uniform sampler2D src_tex;

layout(set = 3, binding = 0) uniform FragUB {
    vec4 params; // x unused, y unused, z=radius, w unused
} ubo;

layout(location = 0) out vec4 out_color;

void main() {
    vec2 texel = (1.0 / vec2(textureSize(src_tex, 0))) * max(ubo.params.z, 0.0001);
    vec4 d = texel.xyxy * vec4(1.0, 1.0, -1.0, 0.0);

    vec3 s = texture(src_tex, in_uv - d.xy).rgb;
    s += texture(src_tex, in_uv - d.wy).rgb * 2.0;
    s += texture(src_tex, in_uv - d.zy).rgb;
    s += texture(src_tex, in_uv + d.zw).rgb * 2.0;
    s += texture(src_tex, in_uv).rgb * 4.0;
    s += texture(src_tex, in_uv + d.xw).rgb * 2.0;
    s += texture(src_tex, in_uv + d.xy).rgb;
    s += texture(src_tex, in_uv + d.wy).rgb * 2.0;
    s += texture(src_tex, in_uv + d.zy).rgb;

    out_color = vec4(s * (1.0 / 16.0), 1.0);
}
