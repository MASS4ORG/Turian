#version 450

// Final post-process combine: bloom add -> per-channel RGB Lift/Gamma/Gain
// grading -> vignette -> ACES tonemap -> gamma encode. Writes the caller's
// UNORM `color_tex`. Tonemap/gamma moved here from scene.frag.glsl and
// skybox.frag.glsl, which now emit raw linear HDR into `hdr_scene`.

layout(location = 0) in vec2 in_uv;

layout(set = 2, binding = 0) uniform sampler2D hdr_scene;
layout(set = 2, binding = 1) uniform sampler2D bloom_tex;

layout(set = 3, binding = 0) uniform FragUB {
    vec4 vignette; // x=intensity, y=radius, z=smoothness, w=aspect
    vec4 lift;     // rgb, w unused
    vec4 gamma;    // rgb, w unused
    vec4 gain;     // rgb, w unused
    vec4 bloom;    // x=bloom_intensity, yzw unused
} ubo;

layout(location = 0) out vec4 out_color;

vec3 acesFilm(vec3 x) {
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main() {
    vec3 hdr = texture(hdr_scene, in_uv).rgb;
    vec3 bloom = texture(bloom_tex, in_uv).rgb;
    hdr += bloom * ubo.bloom.x;

    // Per-channel Lift/Gamma/Gain (ASC CDL slope/offset/power form — the same
    // formula Unity's grading wheels use). Weighted lift avoids blowing out
    // highlights at high lift values.
    hdr = hdr * ubo.gain.rgb + ubo.lift.rgb * (1.0 - hdr);
    hdr = pow(max(hdr, vec3(0.0)), 1.0 / ubo.gamma.rgb);

    // Vignette: smoothstep must ramp low->high edge (radius-smoothness ->
    // radius); GLSL requires edge0 < edge1, so the ramp direction matters.
    vec2 d = (in_uv - 0.5) * vec2(ubo.vignette.w, 1.0);
    float dist = length(d);
    float radius = ubo.vignette.y;
    float smoothness = ubo.vignette.z;
    float vig = 1.0 - ubo.vignette.x * smoothstep(max(radius - smoothness, 0.0), radius, dist);
    hdr *= clamp(vig, 0.0, 1.0);

    hdr = acesFilm(hdr);
    hdr = pow(hdr, vec3(1.0 / 2.2));
    out_color = vec4(hdr, 1.0);
}
