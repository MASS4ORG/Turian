#version 450

// Metallic-roughness physically based shading for the editor viewport.
// Mirrors engine.shader.pbr: base_color, metallic, roughness, normal_scale,
// occlusion_strength, emissive(+strength), alpha_cutoff, and the five glTF maps.
//
// Supports up to MAX_LIGHTS lights of directional / point / spot type, plus a
// shadow map for the primary directional light (3x3 PCF).

#define MAX_LIGHTS 8

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

// One scene light. type: 0=directional, 1=point, 2=spot.
struct Light {
    vec4 position;   // xyz world position (point/spot), w = type
    vec4 direction;  // xyz travel direction (directional/spot), w = range
    vec4 color;      // rgb colour, w = intensity
    vec4 cone;       // x = cos(outer angle), y = cos(inner angle)
};

// SDL3GPU SPIR-V fragment uniforms: set=3, binding=slot_index.
layout(set = 3, binding = 0) uniform FragUB {
    vec4 ambient_color;   // rgb
    vec4 camera_pos;      // xyz, w = light_count
    vec4 base_color;      // rgba
    vec4 mr_ns_oc;        // x=metallic, y=roughness, z=normal_scale, w=occlusion_strength
    vec4 emissive;        // rgb, w=strength
    vec4 flags;           // x=has_albedo, y=has_mr, z=has_normal, w=has_emissive
    vec4 flags2;          // x=has_occlusion, y=alpha_cutoff, z=alpha_mask_on, w=shadows_enabled
    mat4 light_vp;        // shadow light view-projection (primary directional)
    Light lights[MAX_LIGHTS];
} ubo;

layout(location = 0) out vec4 out_color;

const float PI = 3.14159265359;

// Perturb the geometric normal with a tangent-space normal map, deriving the
// TBN basis from screen-space derivatives (no precomputed vertex tangents).
vec3 getNormal() {
    vec3 N = normalize(in_world_normal);
    if (ubo.flags.z < 0.5) return N;

    vec3 tn = texture(normal_tex, in_uv).xyz * 2.0 - 1.0;
    tn.xy *= ubo.mr_ns_oc.z; // normal_scale

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

// Roughness-aware Fresnel for the ambient term, so metals (which have no diffuse)
// still pick up an ambient specular tint instead of rendering black in the
// absence of an environment/IBL probe.
vec3 fresnelSchlickRoughness(float ct, vec3 F0, float rough) {
    vec3 Fr = max(vec3(1.0 - rough), F0);
    return F0 + (Fr - F0) * pow(clamp(1.0 - ct, 0.0, 1.0), 5.0);
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
    vec3 V = normalize(ubo.camera_pos.xyz - in_world_pos);
    float ndv = max(dot(N, V), 0.0);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    int light_count = int(ubo.camera_pos.w);
    vec3 Lo = vec3(0.0);
    for (int i = 0; i < light_count && i < MAX_LIGHTS; i++) {
        Light lt = ubo.lights[i];
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
            // Smooth inverse-square falloff clamped to range.
            float d2 = dist * dist;
            attenuation = clamp(1.0 - (d2 / (range * range)), 0.0, 1.0);
            attenuation *= attenuation / (1.0 + d2);
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

    // Ambient: a diffuse term plus a Fresnel-weighted specular term. Without the
    // specular part, metallic surfaces (kD ~ 0) would render black when no light
    // hits them directly. F0 tints the ambient specular with the metal's colour.
    vec3 F_amb = fresnelSchlickRoughness(ndv, F0, roughness);
    vec3 kD_amb = (vec3(1.0) - F_amb) * (1.0 - metallic);
    vec3 ambient = ubo.ambient_color.rgb * (kD_amb * albedo + F_amb) * occlusion;
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
