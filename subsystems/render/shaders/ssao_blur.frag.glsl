#version 450

// Depth-aware 4x4 box blur to denoise the raw SSAO pass. Rejects taps whose
// prepass depth differs too much from the center, preventing occlusion bleed
// across depth discontinuities.

layout(location = 0) in vec2 in_uv;

layout(set = 2, binding = 0) uniform sampler2D ao_tex;
layout(set = 2, binding = 1) uniform sampler2D prepass_depth;

layout(location = 0) out vec4 out_color;

void main() {
    vec2 texel = 1.0 / vec2(textureSize(ao_tex, 0));
    float center_depth = texture(prepass_depth, in_uv).r;

    float sum = 0.0;
    float wsum = 0.0;
    for (int x = -1; x <= 2; x++) {
        for (int y = -1; y <= 2; y++) {
            vec2 uv = in_uv + vec2(x, y) * texel;
            float d = texture(prepass_depth, uv).r;
            float w = (abs(d - center_depth) < 0.001) ? 1.0 : 0.0;
            sum += texture(ao_tex, uv).r * w;
            wsum += w;
        }
    }

    out_color = vec4(wsum > 0.0 ? sum / wsum : texture(ao_tex, in_uv).r);
}
