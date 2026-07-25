#version 450

// Renders the scene's equirectangular HDR environment map as a background,
// sampled by the view ray reconstructed from the inverse view-projection.
// Drawn before opaque geometry (see `root.zig`), so no depth test is needed —
// opaque draws simply overwrite sky pixels as they render.

layout(location = 0) in vec2 in_ndc;

layout(set = 2, binding = 0) uniform sampler2D env_equirect;

layout(set = 3, binding = 0) uniform FragUB {
    mat4 inv_view_proj;
    vec4 camera_pos_intensity; // xyz = camera world position, w = intensity
} ubo;

layout(location = 0) out vec4 out_color;

const float PI = 3.14159265359;

// Must match `dirToEquirectUv` in scene.frag.glsl and the CPU-side SH
// projection convention in `subsystems/render/assets.zig`.
vec2 dirToEquirectUv(vec3 d) {
    float u = atan(d.x, -d.z) / (2.0 * PI) + 0.5;
    float v = acos(clamp(d.y, -1.0, 1.0)) / PI;
    return vec2(u, v);
}

void main() {
    vec4 far_h = ubo.inv_view_proj * vec4(in_ndc, 1.0, 1.0);
    vec3 far_world = far_h.xyz / far_h.w;
    vec3 dir = normalize(far_world - ubo.camera_pos_intensity.xyz);

    // Raw linear HDR out — tonemap and gamma encode happen once in the
    // post-process composite pass instead (`composite.frag.glsl`).
    vec3 color = texture(env_equirect, dirToEquirectUv(dir)).rgb * ubo.camera_pos_intensity.w;
    out_color = vec4(color, 1.0);
}
