#version 450

// Metallic-roughness physically based shading for the editor viewport.
// Mirrors engine.shader.pbr: base_color, metallic, roughness, normal_scale,
// occlusion_strength, emissive(+strength), alpha_cutoff, and the five glTF maps.
//
// Supports up to MAX_LIGHTS lights of directional / point / spot type, plus a
// shadow map for the primary directional light (3x3 PCF), and optional
// image-based lighting (diffuse SH irradiance + roughness-mipped specular)
// sampled from an equirectangular HDR environment map.

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
layout(set = 2, binding = 5) uniform sampler2DShadow shadow_map;
layout(set = 2, binding = 6) uniform sampler2D env_equirect;  // equirect HDR environment

// One scene light. type: 0=directional, 1=point, 2=spot.
struct Light {
    vec4 position;   // xyz world position (point/spot), w = type
    vec4 direction;  // xyz travel direction (directional/spot), w = range
    vec4 color;      // rgb colour, w = intensity
    vec4 cone;       // x = cos(outer angle), y = cos(inner angle)
};

// SDL3GPU SPIR-V fragment uniforms: set=3, binding=slot_index.
layout(set = 3, binding = 0) uniform FragUB {
    vec4 camera_pos;      // xyz, w = light_count
    vec4 base_color;      // rgba
    vec4 mr_ns_oc;        // x=metallic, y=roughness, z=normal_scale, w=occlusion_strength
    vec4 emissive;        // rgb, w=strength
    vec4 flags;           // x=has_albedo, y=has_mr, z=has_normal, w=has_emissive
    vec4 flags2;          // x=has_occlusion, y=alpha_cutoff, z=alpha_mask_on, w=shadows_enabled
    vec4 env_params;      // x=intensity, y=mip_count, z=has_env, w unused
    vec4 env_sh[9];       // diffuse irradiance SH coefficients (rgb in xyz)
    mat4 light_vp;        // shadow light view-projection (primary directional)
} ubo;

// Scene lights. Storage buffer (not a fixed uniform array) so the light count is
// bounded only by GPU memory, not a per-draw uniform size. SDL3 SPIR-V places
// fragment storage buffers in set=2 after the sampled textures (7 here), so this
// is binding 7. Only `camera_pos.w` entries are read.
layout(std430, set = 2, binding = 7) readonly buffer LightBuffer {
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

// Narkowicz ACES filmic fit: maps linear HDR color to a displayable 0..1 range.
vec3 acesFilm(vec3 x) {
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

// Maps a world-space direction to an equirectangular UV. Must match the CPU-side
// convention used when projecting the environment onto SH (see
// `subsystems/render/assets.zig`'s `computeIrradianceSh`) and the skybox shader.
vec2 dirToEquirectUv(vec3 d) {
    float u = atan(d.x, -d.z) / (2.0 * PI) + 0.5;
    float v = acos(clamp(d.y, -1.0, 1.0)) / PI;
    return vec2(u, v);
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

// Shadow visibility (1 = lit, 0 = fully shadowed) for the primary directional
// light. Matches the clip-space conventions applied in shadow.vert.glsl.
float shadowFactor(float ndl) {
    if (ubo.flags2.w < 0.5) return 1.0;

    vec4 lp = ubo.light_vp * vec4(in_world_pos, 1.0);
    // Same Z remap shadow.vert applies before depth write (see scene.vert.glsl).
    lp.z = (lp.z + lp.w) * 0.5;
    vec3 proj = lp.xyz / lp.w;
    vec2 uv = proj.xy * 0.5 + 0.5;

    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || proj.z > 1.0)
        return 1.0;

    float bias = max(0.0025 * (1.0 - ndl), 0.0006);
    float depth = proj.z - bias;

    vec2 texel = 1.0 / vec2(textureSize(shadow_map, 0));
    float sum = 0.0;
    for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
            sum += texture(shadow_map, vec3(uv + vec2(dx, dy) * texel, depth));
    return sum / 9.0;
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
            // `range`. The naive `1/(1+d^2)` this replaced over-attenuates at
            // any real-world distance beyond ~1 unit — a light several meters
            // from a large scene (e.g. Bistro) read as completely black no
            // matter how high its intensity was pushed.
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

        vec3 irradiance = evalSH(N, ubo.env_sh) * intensity;
        vec3 diffuse_ibl = irradiance * albedo / PI * (1.0 - metallic);

        vec3 R = reflect(-V, N);
        vec2 env_uv = dirToEquirectUv(R);
        vec3 prefiltered = textureLod(env_equirect, env_uv, roughness * max_lod).rgb * intensity;
        vec2 env_brdf = envBRDFApprox(roughness, ndv);
        vec3 specular_ibl = prefiltered * (F0 * env_brdf.x + env_brdf.y);

        ambient = (diffuse_ibl + specular_ibl) * occlusion;
    }
    vec3 color = ambient + Lo * occlusion;

    vec3 emis = ubo.emissive.rgb * ubo.emissive.w;
    if (ubo.flags.w > 0.5) emis *= texture(emissive_tex, in_uv).rgb;
    color += emis;

    // Lighting above is computed in linear space (sRGB-tagged textures are
    // linearized on sample by the GPU sampler); tonemap then gamma-encode for
    // the UNORM (non-sRGB) swapchain, which expects pre-encoded bytes.
    color = acesFilm(color);
    color = pow(color, vec3(1.0 / 2.2));

    out_color = vec4(color, albedo_s.a);
}
