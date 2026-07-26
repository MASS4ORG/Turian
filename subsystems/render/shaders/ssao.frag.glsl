#version 450

// Screen-space ambient occlusion. Samples the depth+normal prepass,
// reconstructs view-space position with `inv_proj`, and evaluates a fixed
// hemisphere kernel oriented per-pixel by the surface normal with
// interleaved-gradient noise rotation.

layout(location = 0) in vec2 in_uv;

layout(set = 2, binding = 0) uniform sampler2D prepass_depth;
layout(set = 2, binding = 1) uniform sampler2D prepass_normal;

const int KERNEL_SIZE = 24;

// SDL3GPU SPIR-V fragment uniforms: set=3, binding=slot_index.
layout(set = 3, binding = 0) uniform FragUB {
    mat4 inv_proj;
    mat4 proj;
    vec4 kernel_samples[KERNEL_SIZE]; // xyz = tangent-space offset, w unused
    vec4 params; // x=radius, y=bias, z=power, w unused
} ubo;

layout(location = 0) out vec4 out_ao;

// Reconstruct view-space position from a screen UV + hardware depth.
// Stored depth relates to GL NDC as `ndc_z = 2*depth - 1`, so the standard
// inverse-projection formula applies.
vec3 viewPosFromDepth(vec2 uv, float depth) {
    // UV uses fullscreen.vert.glsl's V-flipped convention relative to raw
    // NDC; un-flip so the kernel-sample reprojection below (which computes
    // true NDC from `ubo.proj`) and this reconstruction agree on orientation.
    vec2 ndc_xy = vec2(uv.x, 1.0 - uv.y) * 2.0 - 1.0;
    vec4 clip = vec4(ndc_xy, depth * 2.0 - 1.0, 1.0);
    vec4 view = ubo.inv_proj * clip;
    return view.xyz / view.w;
}

// Interleaved gradient noise (Jorge Jimenez) — per-pixel pseudo-random rotation
// without a dedicated noise texture.
float interleavedGradientNoise(vec2 p) {
    return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
}

void main() {
    float depth = texture(prepass_depth, in_uv).r;
    // Nothing was drawn here (background/sky, or no depth) — fully unoccluded.
    if (depth >= 0.9999) {
        out_ao = vec4(1.0);
        return;
    }

    vec3 frag_pos = viewPosFromDepth(in_uv, depth);
    vec3 N = normalize(texture(prepass_normal, in_uv).xyz * 2.0 - 1.0);

    float angle = interleavedGradientNoise(gl_FragCoord.xy) * 6.28318530718;
    vec3 rand_vec = vec3(cos(angle), sin(angle), 0.0);
    vec3 tangent = normalize(rand_vec - N * dot(rand_vec, N));
    vec3 bitangent = cross(N, tangent);
    mat3 TBN = mat3(tangent, bitangent, N);

    float radius = ubo.params.x;
    float bias = ubo.params.y;
    float occlusion = 0.0;

    for (int i = 0; i < KERNEL_SIZE; i++) {
        vec3 sample_pos = frag_pos + (TBN * ubo.kernel_samples[i].xyz) * radius;

        vec4 offset = ubo.proj * vec4(sample_pos, 1.0);
        offset.xyz /= offset.w;
        offset.xy = offset.xy * 0.5 + 0.5;
        // Same V flip as fullscreen.vert.glsl; baked into `in_uv` but not a
        // UV recomputed here from the projection matrix.
        offset.y = 1.0 - offset.y;

        if (offset.x < 0.0 || offset.x > 1.0 || offset.y < 0.0 || offset.y > 1.0)
            continue;

        float occluder_depth_raw = texture(prepass_depth, offset.xy).r;
        if (occluder_depth_raw >= 0.9999) continue;
        vec3 occluder_pos = viewPosFromDepth(offset.xy, occluder_depth_raw);

        float range_check = smoothstep(0.0, 1.0, radius / max(abs(frag_pos.z - occluder_pos.z), 1e-4));
        occlusion += (occluder_pos.z >= sample_pos.z + bias ? 1.0 : 0.0) * range_check;
    }

    occlusion = 1.0 - (occlusion / float(KERNEL_SIZE));
    out_ao = vec4(pow(clamp(occlusion, 0.0, 1.0), ubo.params.z));
}
