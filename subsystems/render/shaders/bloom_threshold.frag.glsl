#version 450

// Soft-knee bright-pass extract: `hdr_color` (full-res) -> bloom mip0
// (half-res, set as the render target's size by the caller). Bilinear
// sampling on a half-size target does the first downsample for free.

layout(location = 0) in vec2 in_uv;

layout(set = 2, binding = 0) uniform sampler2D hdr_color;

layout(set = 3, binding = 0) uniform FragUB {
    vec4 params; // x=threshold, y=knee, z unused, w unused
} ubo;

layout(location = 0) out vec4 out_color;

void main() {
    vec3 color = texture(hdr_color, in_uv).rgb;

    float threshold = ubo.params.x;
    float knee = max(ubo.params.y, 1e-5);
    float brightness = max(color.r, max(color.g, color.b));

    // Unity-style quadratic soft knee: a smooth ramp into the threshold
    // instead of a hard cutoff, so bright pixels don't flicker at the edge.
    float soft = clamp(brightness - threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee);
    float contribution = max(soft, brightness - threshold) / max(brightness, 1e-5);

    out_color = vec4(color * contribution, 1.0);
}
