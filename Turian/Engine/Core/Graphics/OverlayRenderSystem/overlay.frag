#version 450

// Supplies straight linear color to fixed-function alpha blending.

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uSource;

layout(push_constant) uniform SourceEncoding { int premultipliedSrgb; } source;

vec4 straightLinear(vec4 color)
{
    if (source.premultipliedSrgb == 0) return color;
    if (color.a == 0.0) return vec4(0.0);
    vec3 rgb = clamp(color.rgb / color.a, 0.0, 1.0);
    return vec4(mix(pow((rgb + 0.055) / 1.055, vec3(2.4)), rgb / 12.92,
        lessThanEqual(rgb, vec3(0.04045))), color.a);
}

void main()
{
    outColor = straightLinear(texture(uSource, vUv));
}
