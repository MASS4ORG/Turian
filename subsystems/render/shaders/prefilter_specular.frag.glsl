#version 450

// GGX importance-sampled specular prefiltering (Karis split-sum, "Real
// Shading in Unreal Engine 4"). Drawn once per (face, mip) pair into the
// prefiltered cubemap, sampling the sharp un-prefiltered base cubemap
// produced by `equirect_to_cubemap.frag.glsl` — see `ibl_prefilter.zig`.
// Paired with `fullscreen.vert.glsl`. Replaces #135's equirect-mip stand-in
// (box-filtered in UV space, so it over-blurred near the poles and had no
// real GGX lobe shape) with a proper solid-angle-uniform prefilter.

layout(location = 0) in vec2 in_uv;

layout(set = 2, binding = 0) uniform samplerCube base_cubemap;

layout(set = 3, binding = 0) uniform FragUB {
    vec4 face_roughness; // x = face index (0..5), y = roughness, zw unused
} ubo;

layout(location = 0) out vec4 out_color;

const float PI = 3.14159265359;
const uint SAMPLE_COUNT = 32u;

// Same cube-face convention as `equirect_to_cubemap.frag.glsl` — must match
// so a prefiltered texel lands on the direction it was importance-sampled
// around.
vec3 faceDir(int face, vec2 uv) {
    float u = 2.0 * uv.x - 1.0;
    float v = 2.0 * uv.y - 1.0;
    if (face == 0) return vec3(1.0, -v, -u);
    if (face == 1) return vec3(-1.0, -v, u);
    if (face == 2) return vec3(u, 1.0, v);
    if (face == 3) return vec3(u, -1.0, -v);
    if (face == 4) return vec3(u, -v, 1.0);
    return vec3(-u, -v, -1.0);
}

// Van der Corput radical inverse (base 2), bit-reversal form.
float radicalInverseVdC(uint bits) {
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10; // / 2^32
}

vec2 hammersley(uint i, uint n) {
    return vec2(float(i) / float(n), radicalInverseVdC(i));
}

// Sample a half-vector around `n` from the GGX distribution for `roughness`.
vec3 importanceSampleGGX(vec2 xi, vec3 n, float roughness) {
    float a = roughness * roughness;
    float phi = 2.0 * PI * xi.x;
    float cos_theta = sqrt((1.0 - xi.y) / (1.0 + (a * a - 1.0) * xi.y));
    float sin_theta = sqrt(1.0 - cos_theta * cos_theta);

    vec3 h = vec3(cos(phi) * sin_theta, sin(phi) * sin_theta, cos_theta);

    vec3 up = abs(n.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(up, n));
    vec3 bitangent = cross(n, tangent);
    return normalize(tangent * h.x + bitangent * h.y + n * h.z);
}

void main() {
    vec3 n = normalize(faceDir(int(ubo.face_roughness.x), in_uv));
    float roughness = ubo.face_roughness.y;

    // Mirror reflection: importance sampling would collapse to this single
    // direction anyway — skip the loop and sample the base cubemap directly.
    if (roughness < 0.01) {
        out_color = vec4(texture(base_cubemap, n).rgb, 1.0);
        return;
    }

    vec3 v = n;
    vec3 prefiltered = vec3(0.0);
    float total_weight = 0.0;
    for (uint i = 0u; i < SAMPLE_COUNT; i++) {
        vec2 xi = hammersley(i, SAMPLE_COUNT);
        vec3 h = importanceSampleGGX(xi, n, roughness);
        vec3 l = normalize(2.0 * dot(v, h) * h - v);

        float ndl = dot(n, l);
        if (ndl > 0.0) {
            prefiltered += texture(base_cubemap, l).rgb * ndl;
            total_weight += ndl;
        }
    }
    out_color = vec4(prefiltered / max(total_weight, 1e-4), 1.0);
}
