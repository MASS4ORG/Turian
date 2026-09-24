#version 460

// Samples the panel's straight-alpha RGBA texture; fixed-function blending
// (src-alpha / one-minus-src-alpha) composites it into the scene. Fully transparent
// texels are discarded so the depth buffer is not written where the panel shows nothing.

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 1, binding = 0) uniform sampler2D uPanel;

void main()
{
    vec4 c = texture(uPanel, vUv);
    if (c.a < 0.004) discard;
    outColor = c;
}
