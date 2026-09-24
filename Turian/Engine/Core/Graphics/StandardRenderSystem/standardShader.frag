#version 460

const int uboLights = 10;
const int uboDirectionalLights = 4;
const float PI = 3.14159265359;

layout(location = 0) in vec3 fragColor;
layout(location = 1) in vec3 fragPosWorld;
layout(location = 2) in vec3 fragNormalWorld;
layout(location = 3) in vec2 fragUv;
layout(location = 4) in vec3 fragTangentWorld;
layout(location = 5) in vec3 fragBitangentWorld;

layout(location = 0) out vec4 outColor;

struct PointLight {
    vec4 position;
    vec4 color;
};

struct DirectionalLight {
    vec4 direction;  // xyz = direction the light travels, normalized
    vec4 color;      // rgb = color, w = intensity; zero intensity disables the slot
};

layout(set = 0, binding = 0) uniform GlobalUbo {
    mat4 projection;
    mat4 view;
    vec4 front;
    vec4 ambientColor;
    PointLight pointLights[uboLights];
    DirectionalLight directionalLights[uboDirectionalLights];
} ubo;

// Per-material UBO + textures bound at set=1 — must match MaterialPbrUbo and the
// binding indices declared in MaterialDescriptorContext.
layout(set = 1, binding = 0) uniform PbrMaterial {
    vec4 baseColorFactor;
    vec4 emissiveFactor;     // xyz emissive, w padding
    float metallicFactor;
    float roughnessFactor;
    float occlusionStrength;
    float flipGreenChannel;
} mat;

layout(set = 1, binding = 1) uniform sampler2D baseColorTex;
layout(set = 1, binding = 2) uniform sampler2D metallicRoughnessTex;  // glTF: G=roughness, B=metallic
layout(set = 1, binding = 3) uniform sampler2D normalTex;
layout(set = 1, binding = 4) uniform sampler2D occlusionTex;          // R channel
layout(set = 1, binding = 5) uniform sampler2D emissiveTex;

layout(push_constant) uniform Push {
    mat4 modelMatrix;
    mat4 normalMatrix;
} push;

// ─── PBR helpers ───────────────────────────────────────────────────────────────

vec3 fresnelSchlick(float cosTheta, vec3 F0) {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

float distributionGGX(vec3 N, vec3 H, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;
    float denom = NdotH2 * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom);
}

float geometrySchlickGGX(float NdotV, float roughness) {
    // Direct-lighting remapping (k = (r+1)^2 / 8).
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}

float geometrySmith(vec3 N, vec3 V, vec3 L, float roughness) {
    return geometrySchlickGGX(max(dot(N, V), 0.0), roughness)
         * geometrySchlickGGX(max(dot(N, L), 0.0), roughness);
}

// One Cook-Torrance evaluation, shared by every light type. Only the incoming direction and the
// radiance reaching the surface differ between a point light and a directional one.
vec3 shade(vec3 N, vec3 V, vec3 L, vec3 albedo, float metallic, float roughness, vec3 F0, vec3 radiance) {
    vec3 H = normalize(V + L);

    float NDF = distributionGGX(N, H, roughness);
    float G = geometrySmith(N, V, L, roughness);
    vec3 F = fresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 numerator = NDF * G * F;
    float denom = 4.0 * max(dot(N, V), 0.0) * max(dot(N, L), 0.0) + 1e-4;
    vec3 specular = numerator / denom;

    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

    float NdotL = max(dot(N, L), 0.0);
    return (kD * albedo / PI + specular) * radiance * NdotL;
}

vec3 sampleNormal(vec3 N, vec3 T, vec3 B, vec2 uv) {
    // Default flat-normal texture is (128,128,255) — sampled to (0,0) here,
    // so this branchless path falls back to the geometric normal when no map is bound.
    vec2 xy = texture(normalTex, uv).xy * 2.0 - 1.0;

    // DirectX-convention maps store the green channel inverted. Block-compressed data cannot be
    // rewritten at import without recompressing it, so the flip happens here.
    if (mat.flipGreenChannel > 0.5) xy.y = -xy.y;

    // Z is reconstructed rather than sampled: BC5 stores only two channels, so its blue channel
    // reads as 0. Recomputing it is exact for a unit normal and correct for three-channel maps too.
    float z = sqrt(clamp(1.0 - dot(xy, xy), 0.0, 1.0));
    vec3 n = vec3(xy, z);

    if (length(T) < 1e-4) return normalize(N);
    return normalize(mat3(T, B, N) * n);
}

void main() {
    // ─── Sample textures ───────────────────────────────────────────────────────
    vec4 baseColorSample = texture(baseColorTex, fragUv) * mat.baseColorFactor;
    vec3 albedo = baseColorSample.rgb * fragColor;

    vec3 mrSample = texture(metallicRoughnessTex, fragUv).rgb;
    float metallic = mrSample.b * mat.metallicFactor;
    float roughness = clamp(mrSample.g * mat.roughnessFactor, 0.04, 1.0);

    float ao = mix(1.0, texture(occlusionTex, fragUv).r, mat.occlusionStrength);
    vec3 emissive = texture(emissiveTex, fragUv).rgb * mat.emissiveFactor.rgb;

    // ─── Surface basis ─────────────────────────────────────────────────────────
    vec3 N = sampleNormal(fragNormalWorld, fragTangentWorld, fragBitangentWorld, fragUv);
    vec3 V = normalize(-ubo.front.xyz);

    // F0 = 0.04 for dielectrics, baseColor for metals.
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // ─── Direct lighting (Cook-Torrance) ───────────────────────────────────────
    vec3 Lo = vec3(0.0);

    for (int i = 0; i < uboLights; i++) {
        PointLight light = ubo.pointLights[i];
        vec3 toLight = light.position.xyz - fragPosWorld;
        float distSq = dot(toLight, toLight);
        float attenuation = 1.0 / max(distSq, 1e-4);

        if (attenuation * light.color.w < 0.01) continue;

        vec3 radiance = light.color.rgb * light.color.w * attenuation;
        Lo += shade(N, V, toLight * inversesqrt(distSq), albedo, metallic, roughness, F0, radiance);
    }

    // Directional light does not attenuate: the sun reaches every surface at the same strength.
    for (int i = 0; i < uboDirectionalLights; i++) {
        DirectionalLight light = ubo.directionalLights[i];
        if (light.color.w < 1e-4) continue;

        vec3 L = normalize(-light.direction.xyz);
        Lo += shade(N, V, L, albedo, metallic, roughness, F0, light.color.rgb * light.color.w);
    }

    // ─── Ambient + emissive ───────────────────────────────────────────────────
    vec3 ambient = ubo.ambientColor.rgb * ubo.ambientColor.w * albedo * ao;
    vec3 color = ambient + Lo + emissive;

    // Reinhard tonemap + gamma — keeps the output in [0,1] without clipping bright
    // metals; sRGB swapchain handles final encoding when present.
    color = color / (color + vec3(1.0));
    outColor = vec4(color, baseColorSample.a);
}
