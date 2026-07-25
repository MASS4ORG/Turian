#version 450

// 13-tap box/tent downsample (Call of Duty: Advanced Warfare's "Next
// Generation Post Processing" dual-filter technique) — mip[i] -> mip[i+1],
// one draw per mip level, same pipeline reused (pipelines bake in format and
// sample count only, not texture dimensions).

layout(location = 0) in vec2 in_uv;

layout(set = 2, binding = 0) uniform sampler2D src_tex;

layout(location = 0) out vec4 out_color;

void main() {
    vec2 texel = 1.0 / vec2(textureSize(src_tex, 0));

    vec3 a = texture(src_tex, in_uv + texel * vec2(-1.0, -1.0)).rgb;
    vec3 b = texture(src_tex, in_uv + texel * vec2(0.0, -1.0)).rgb;
    vec3 cc = texture(src_tex, in_uv + texel * vec2(1.0, -1.0)).rgb;
    vec3 d = texture(src_tex, in_uv + texel * vec2(-0.5, -0.5)).rgb;
    vec3 e = texture(src_tex, in_uv + texel * vec2(0.5, -0.5)).rgb;
    vec3 f = texture(src_tex, in_uv + texel * vec2(-1.0, 0.0)).rgb;
    vec3 g = texture(src_tex, in_uv).rgb;
    vec3 h = texture(src_tex, in_uv + texel * vec2(1.0, 0.0)).rgb;
    vec3 i = texture(src_tex, in_uv + texel * vec2(-0.5, 0.5)).rgb;
    vec3 j = texture(src_tex, in_uv + texel * vec2(0.5, 0.5)).rgb;
    vec3 k = texture(src_tex, in_uv + texel * vec2(-1.0, 1.0)).rgb;
    vec3 l = texture(src_tex, in_uv + texel * vec2(0.0, 1.0)).rgb;
    vec3 m = texture(src_tex, in_uv + texel * vec2(1.0, 1.0)).rgb;

    const float inner = 0.5 / 4.0;
    const float outer = 0.125 / 4.0;

    vec3 o = (d + e + i + j) * inner;
    o += (a + b + g + f) * outer;
    o += (b + cc + h + g) * outer;
    o += (f + g + l + k) * outer;
    o += (g + h + m + l) * outer;

    out_color = vec4(o, 1.0);
}
