#version 450

// Metallic-roughness physically based shading for the editor viewport.
// Mirrors engine.shader.pbr: base_color, metallic, roughness, normal_scale,
// occlusion_strength, emissive(+strength), alpha_cutoff, and the five glTF maps.
//
// Supports up to MAX_LIGHTS lights of directional / point / spot type, plus a
// cascaded shadow map for the primary directional light (3x3 PCF per
// cascade), and optional image-based lighting (diffuse SH irradiance +
// GGX-prefiltered specular cubemap) derived from the scene's equirectangular
// HDR environment map.

#define NUM_CASCADES 4

layout(location = 0) in vec3 in_world_normal;
layout(location = 1) in vec2 in_uv;
layout(location = 2) in vec3 in_world_pos;

// SDL3GPU SPIR-V: fragment samplers at set=2, binding=slot_index.
// Order must match GpuRenderer's binding array.
layout(set = 2, binding = 0) uniform sampler2D albedo_tex;
layout(set = 2, binding = 1) uniform sampler2D mr_tex;        // glTF: G=roughness, B=metallic
layout(set = 2, binding = 2) uniform sampler2D normal_tex;    // tangent-space
layout(set = 2, binding = 3) uniform sampler2D emissive_tex;
layout(set = 2, binding = 4) uniform sampler2D occlusion_tex; // R channel
// One SHADOW_DIM x (SHADOW_DIM*NUM_CASCADES) atlas: cascade i occupies the
// vertical strip [i/NUM_CASCADES, (i+1)/NUM_CASCADES) in V (see
// pipeline.createShadowMap — array textures can't be depth-stencil targets).
layout(set = 2, binding = 5) uniform sampler2DShadow shadow_map;
layout(set = 2, binding = 6) uniform samplerCube env_prefiltered; // GGX-prefiltered specular cubemap
layout(set = 2, binding = 7) uniform sampler2D ssao_tex; // blurred SSAO, 1.0 = unoccluded
layout(set = 2, binding = 8) uniform sampler2D ssr_tex; // rgb=reflected color, a=hit confidence
layout(set = 2, binding = 9) uniform samplerCube probe_prefiltered; // resolved local reflection probe (reflection_probes.zig), or a placeholder at weight 0

// One scene light. type: 0=directional, 1=point, 2=spot.
struct Light {
    vec4 position;   // xyz world position (point/spot), w = type
    vec4 direction;  // xyz travel direction (directional/spot), w = range
    vec4 color;      // rgb colour, w = intensity
    vec4 cone;       // x = cos(outer angle), y = cos(inner angle)
};

// SDL3GPU SPIR-V fragment uniforms: set=3, binding=slot_index.
// Per-draw values only — anything frame-constant lives in FrameFragUB
// (binding=1) instead, pushed once per pass rather than once per draw.
layout(set = 3, binding = 0) uniform FragUB {
    vec4 camera_pos;      // xyz, w = light_count
    vec4 base_color;      // rgba
    vec4 mr_ns_oc;        // x=metallic, y=roughness, z=normal_scale, w=occlusion_strength
    vec4 emissive;        // rgb, w=strength
    vec4 flags;           // x=has_albedo, y=has_mr, z=has_normal, w=has_emissive
    vec4 flags2;          // x=has_occlusion, y=alpha_cutoff, z=alpha_mask_on, w=shadows_enabled
    vec4 env_params;      // x=intensity, y=mip_count, z=has_env, w unused
    vec4 cam_forward;     // xyz camera forward (world space); picks the shadow cascade
    vec4 probe_params;        // x=resolved reflection-probe weight, y=probe mip_count, zw unused
} ubo;

// Frame-constant fragment uniforms, pushed once per render pass (see
// draw.pushFrameUniforms) instead of per draw — mirrors how scene lights
// live in a storage buffer bound once per pass rather than the per-draw UBO.
layout(set = 3, binding = 1) uniform FrameFragUB {
    vec4 env_sh[9];       // diffuse irradiance SH coefficients (rgb in xyz)
    mat4 cascade_vp[NUM_CASCADES]; // per-cascade shadow light view-projection
    vec4 cascade_splits;      // per-cascade far distance along cam_forward (world units)
    vec4 cascade_depth_scale; // per-cascade 1/(ortho far-near); converts a world-unit bias to NDC depth
} frame_ubo;

// Scene lights. Storage buffer (not a fixed uniform array) so the light count is
// bounded only by GPU memory, not a per-draw uniform size. SDL3 SPIR-V places
// fragment storage buffers in set=2 after the sampled textures (10 here), so this
// is binding 10. Only `camera_pos.w` entries are read.
layout(std430, set = 2, binding = 10) readonly buffer LightBuffer {
    Light lights[];
} light_buf;

layout(location = 0) out vec4 out_color;

const float PI = 3.14159265359;

// Perturb the geometric normal with a tangent-space normal map, deriving the
// TBN basis from screen-space derivatives (no precomputed vertex tangents).
vec3 getNormal() {
    vec3 N = normalize(in_world_normal);
    if (ubo.flags.z < 0.5) return N;

    // Reconstruct Z from XY rather than reading the blue channel: two-channel
    // BC5/ATI2 normal maps (the usual cooked form, and what all of Bistro uses)
    // carry no blue channel, so sampling .z yields 0 → a normal pointing into
    // the surface. Reconstruction is equally valid for three-channel maps, since
    // a tangent-space normal is unit length.
    vec3 tn;
    tn.xy = texture(normal_tex, in_uv).xy * 2.0 - 1.0;
    tn.xy *= ubo.mr_ns_oc.z; // normal_scale
    tn.z = sqrt(max(1.0 - dot(tn.xy, tn.xy), 0.0));

    vec3 dp1 = dFdx(in_world_pos);
    vec3 dp2 = dFdy(in_world_pos);
    vec2 duv1 = dFdx(in_uv);
    vec2 duv2 = dFdy(in_uv);

    vec3 dp2perp = cross(dp2, N);
    vec3 dp1perp = cross(N, dp1);
    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;
    float invmax = inversesqrt(max(dot(T, T), dot(B, B)));
    mat3 TBN = mat3(T * invmax, B * invmax, N);
    return normalize(TBN * tn);
}

float distributionGGX(float ndh, float rough) {
    float a = rough * rough;
    float a2 = a * a;
    float d = ndh * ndh * (a2 - 1.0) + 1.0;
    return a2 / max(PI * d * d, 1e-6);
}

float geometrySchlickGGX(float nv, float rough) {
    float r = rough + 1.0;
    float k = (r * r) / 8.0;
    return nv / (nv * (1.0 - k) + k);
}

float geometrySmith(float ndv, float ndl, float rough) {
    return geometrySchlickGGX(ndv, rough) * geometrySchlickGGX(ndl, rough);
}

vec3 fresnelSchlick(float ct, vec3 F0) {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - ct, 0.0, 1.0), 5.0);
}


// Order-2 spherical-harmonics irradiance evaluation (Ramamoorthi & Hanrahan):
// `sh` holds the raw radiance projection coefficients computed on the CPU;
// this applies the per-band cosine-lobe convolution constants (A0=pi,
// A1=2*pi/3, A2=pi/4) and evaluates the same basis functions at `n`.
vec3 evalSH(vec3 n, vec4 sh[9]) {
    vec3 result = sh[0].rgb * (0.282095 * PI);
    const float a1 = 0.488603 * (2.0 * PI / 3.0);
    result += sh[1].rgb * (a1 * n.y);
    result += sh[2].rgb * (a1 * n.z);
    result += sh[3].rgb * (a1 * n.x);
    const float a2 = PI / 4.0;
    result += sh[4].rgb * (1.092548 * a2 * n.x * n.y);
    result += sh[5].rgb * (1.092548 * a2 * n.y * n.z);
    result += sh[6].rgb * (0.315392 * a2 * (3.0 * n.z * n.z - 1.0));
    result += sh[7].rgb * (1.092548 * a2 * n.x * n.z);
    result += sh[8].rgb * (0.546274 * a2 * (n.x * n.x - n.y * n.y));
    return max(result, vec3(0.0));
}

// Karis' analytic environment-BRDF approximation (split-sum second term),
// avoiding a baked 2D LUT texture. Returns (scale, bias) applied to F0:
// specular = prefilteredColor * (F0 * result.x + result.y).
vec2 envBRDFApprox(float roughness, float ndv) {
    const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * ndv)) * r.x + r.y;
    return vec2(-1.04, 1.04) * a004 + r.zw;
}

// Samples one cascade's shadow-atlas strip at `in_world_pos`, 1 = lit. Returns
// 1.0 outside this cascade's own frustum, so a boundary blend can lean on it.
float sampleCascade(int cascade, float bias_world) {
    vec4 lp = frame_ubo.cascade_vp[cascade] * vec4(in_world_pos, 1.0);
    // Same Z remap shadow.vert applies before depth write (see scene.vert.glsl).
    lp.z = (lp.z + lp.w) * 0.5;
    vec3 proj = lp.xyz / lp.w;
    vec2 uv = proj.xy * 0.5 + 0.5;
    // Same V flip as ssao.frag.glsl/ssr.frag.glsl: a render target's texel
    // row 0 is the top, so a UV recomputed from a projection matrix must
    // invert V or it reads the atlas mirrored inside the cascade strip.
    uv.y = 1.0 - uv.y;

    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || proj.z > 1.0)
        return 1.0;

    // World-unit bias (5cm facing the light, up to 35cm at grazing angles),
    // converted to this cascade's NDC depth range — cascades span wildly
    // different world extents, so a single NDC bias either acnes the near
    // cascade or peter-pans the far one.
    float bias = frame_ubo.cascade_depth_scale[cascade] * bias_world;
    float depth = proj.z - bias;

    // Remap into this cascade's strip of the atlas; PCF taps are clamped to
    // the strip's V range so they can't sample a neighbouring cascade's data
    // at the boundary rows.
    float v_min = float(cascade) / float(NUM_CASCADES);
    float v_max = float(cascade + 1) / float(NUM_CASCADES);
    vec2 atlas_uv = vec2(uv.x, mix(v_min, v_max, uv.y));

    vec2 texel = 1.0 / vec2(textureSize(shadow_map, 0));
    float sum = 0.0;
    for (int dx = -1; dx <= 1; dx++) {
        for (int dy = -1; dy <= 1; dy++) {
            vec2 s = atlas_uv + vec2(dx, dy) * texel;
            s.y = clamp(s.y, v_min, v_max);
            sum += texture(shadow_map, vec3(s, depth));
        }
    }
    return sum / 9.0;
}

// Shadow visibility for the primary directional light, cascade picked by
// distance along the camera's forward axis. Cascades refit every frame, so a
// hard cutoff at the split would seam visibly as the camera rotates — blended
// across the last 10% of each cascade's range instead.
float shadowFactor(float ndl) {
    if (ubo.flags2.w < 0.5) return 1.0;

    float cam_dist = dot(in_world_pos - ubo.camera_pos.xyz, ubo.cam_forward.xyz);
    int cascade = NUM_CASCADES - 1;
    for (int i = 0; i < NUM_CASCADES - 1; i++) {
        if (cam_dist < frame_ubo.cascade_splits[i]) {
            cascade = i;
            break;
        }
    }

    float bias_world = mix(0.05, 0.35, 1.0 - ndl);
    float s = sampleCascade(cascade, bias_world);

    if (cascade < NUM_CASCADES - 1) {
        float far_split = frame_ubo.cascade_splits[cascade];
        float near_split = cascade == 0 ? 0.0 : frame_ubo.cascade_splits[cascade - 1];
        float margin = max(far_split - near_split, 1e-3) * 0.1;
        float dist_to_far = far_split - cam_dist;
        if (dist_to_far < margin) {
            float s_next = sampleCascade(cascade + 1, bias_world);
            float t = clamp(1.0 - dist_to_far / margin, 0.0, 1.0);
            s = mix(s, s_next, t);
        }
    }
    return s;
}

void main() {
    vec4 albedo_s = ubo.base_color;
    if (ubo.flags.x > 0.5) albedo_s *= texture(albedo_tex, in_uv);

    // Alpha cutoff (mask) — works without framebuffer blending.
    if (ubo.flags2.z > 0.5 && albedo_s.a < ubo.flags2.y) discard;

    float metallic  = ubo.mr_ns_oc.x;
    float roughness = ubo.mr_ns_oc.y;
    if (ubo.flags.y > 0.5) {
        vec4 mr = texture(mr_tex, in_uv);
        roughness *= mr.g;
        metallic  *= mr.b;
    }
    roughness = clamp(roughness, 0.04, 1.0);

    float occlusion = 1.0;
    if (ubo.flags2.x > 0.5) {
        occlusion = mix(1.0, texture(occlusion_tex, in_uv).r, ubo.mr_ns_oc.w);
    }

    vec3 albedo = albedo_s.rgb;
    vec3 N = getNormal();
    // Two-sided shading: a back-facing fragment (e.g. the inner side of a
    // single-sided wall on a two-sided material) must light from the side the
    // camera actually sees, so flip the normal to face the viewer.
    if (!gl_FrontFacing) N = -N;
    vec3 V = normalize(ubo.camera_pos.xyz - in_world_pos);
    float ndv = max(dot(N, V), 0.0);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    int light_count = int(ubo.camera_pos.w);
    vec3 Lo = vec3(0.0);
    for (int i = 0; i < light_count; i++) {
        Light lt = light_buf.lights[i];
        int type = int(lt.position.w);

        // Direction to the light and distance attenuation.
        vec3 L;
        float attenuation = 1.0;
        if (type == 0) {
            L = normalize(-lt.direction.xyz);
        } else {
            vec3 to_light = lt.position.xyz - in_world_pos;
            float dist = length(to_light);
            L = to_light / max(dist, 1e-4);
            float range = max(lt.direction.w, 1e-4);
            // Windowed inverse-square falloff (glTF KHR_lights_punctual / UE4
            // style): true 1/d^2 near the source, smoothly windowed to zero at
            // `range`.
            float d2 = max(dist * dist, 1e-4);
            float range2 = range * range;
            float win = clamp(1.0 - (d2 * d2) / (range2 * range2), 0.0, 1.0);
            attenuation = (win * win) / d2;
            if (type == 2) {
                // Spot cone: cos between spot dir and fragment direction.
                float cos_a = dot(normalize(lt.direction.xyz), -L);
                float cone = clamp((cos_a - lt.cone.x) / max(lt.cone.y - lt.cone.x, 1e-4), 0.0, 1.0);
                attenuation *= cone;
            }
        }
        if (attenuation <= 0.0) continue;

        vec3 H = normalize(V + L);
        float ndl = max(dot(N, L), 0.0);
        float ndh = max(dot(N, H), 0.0);
        float hdv = max(dot(H, V), 0.0);

        vec3 radiance = lt.color.rgb * lt.color.w * attenuation;

        float NDF = distributionGGX(ndh, roughness);
        float G   = geometrySmith(ndv, ndl, roughness);
        vec3  F   = fresnelSchlick(hdv, F0);

        vec3 specular = (NDF * G * F) / max(4.0 * ndv * ndl, 1e-4);
        vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);

        // Shadow only the primary directional light (index 0, directional).
        float shadow = (i == 0 && type == 0) ? shadowFactor(ndl) : 1.0;

        Lo += (kD * albedo / PI + specular) * radiance * ndl * shadow;
    }

    // Ambient: image-based lighting sampled from the scene's environment map
    // (diffuse SH irradiance + roughness-mipped specular), or nothing at all
    // when no environment is bound — an unlit scene should render dark, not a
    // free flat-gray "fill light".
    vec3 ambient = vec3(0.0);
    if (ubo.env_params.z > 0.5) {
        float intensity = ubo.env_params.x;
        float max_lod = max(ubo.env_params.y - 1.0, 0.0);

        vec3 irradiance = evalSH(N, frame_ubo.env_sh) * intensity;
        vec3 diffuse_ibl = irradiance * albedo / PI * (1.0 - metallic);

        vec3 R = reflect(-V, N);
        vec3 prefiltered = textureLod(env_prefiltered, R, roughness * max_lod).rgb * intensity;

        // Local reflection probe (reflection_probes.zig): CPU-resolved per
        // submesh, so "environment" here means the global HDRI locally
        // overridden by the nearest baked probe in range — SSR still mixes
        // on top below, preserving SSR > local probe > global env.
        float probe_max_lod = max(ubo.probe_params.y - 1.0, 0.0);
        vec3 probe_color = textureLod(probe_prefiltered, R, roughness * probe_max_lod).rgb * intensity;
        prefiltered = mix(prefiltered, probe_color, ubo.probe_params.x);

        vec4 ssr = texture(ssr_tex, gl_FragCoord.xy / vec2(textureSize(ssr_tex, 0)));
        // Bistro's roughness is ~0.74 for nearly every material, ~0 for glass
        // (measured, see bistro-visual-fidelity.md). A single-tap unblurred
        // SSR sample only reads as correct near the mirror end of that range,
        // so it's gated off well before the generic-surface cluster —
        // hardcoded, consistent with this renderer's other renderer-internal
        // tunables (SSAO's RADIUS/BIAS/POWER, shadow bias).
        const float SSR_ROUGH_START = 0.15;
        const float SSR_ROUGH_END = 0.35;
        float ssr_fade = 1.0 - smoothstep(SSR_ROUGH_START, SSR_ROUGH_END, roughness);
        vec3 reflection = mix(prefiltered, ssr.rgb, ssr.a * ssr_fade);

        vec2 env_brdf = envBRDFApprox(roughness, ndv);
        vec3 specular_ibl = reflection * (F0 * env_brdf.x + env_brdf.y);

        // Screen-space AO modulates the distant-environment ambient term.
        float ssao = texture(ssao_tex, gl_FragCoord.xy / vec2(textureSize(ssao_tex, 0))).r;
        ambient = (diffuse_ibl + specular_ibl) * occlusion * ssao;
    }
    vec3 color = ambient + Lo * occlusion;

    vec3 emis = ubo.emissive.rgb * ubo.emissive.w;
    if (ubo.flags.w > 0.5) emis *= texture(emissive_tex, in_uv).rgb;
    color += emis;

    // Lighting in linear space (sRGB textures linearized by the GPU sampler).
    // Raw linear HDR out — tonemap and gamma encode in the post-process
    // composite pass (`composite.frag.glsl`) which reads the HDR target.
    out_color = vec4(color, albedo_s.a);
}
