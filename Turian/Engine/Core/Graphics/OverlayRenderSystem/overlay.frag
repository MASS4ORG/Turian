#version 450

// Samples a straight-alpha RGBA source and lets fixed-function blending
// (src-alpha / one-minus-src-alpha) composite it over the scene.

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uSource;

void main()
{
    outColor = texture(uSource, vUv);
}
