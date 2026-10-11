#version 460

// Samples the panel texture and supplies straight linear color; fixed-function blending
// (src-alpha / one-minus-src-alpha) composites it into the scene. Fully transparent
// texels are discarded so the depth buffer is not written where the panel shows nothing.

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 1, binding = 0) uniform sampler2D uPanel;

layout(push_constant) uniform SourceEncoding { layout(offset = 64) int premultipliedSrgb; } source;

vec4 straightLinear(vec4 color)
{
    if (source.premultipliedSrgb == 0) return color;
    vec3 rgb = clamp(color.rgb / color.a, 0.0, 1.0);
    return vec4(mix(pow((rgb + 0.055) / 1.055, vec3(2.4)), rgb / 12.92,
        lessThanEqual(rgb, vec3(0.04045))), color.a);
}

void main()
{
    vec4 c = texture(uPanel, vUv);
    if (c.a < 0.004) discard;
    outColor = straightLinear(c);
}
