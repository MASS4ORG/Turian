#version 450

// Screen-space reflections. Raymarches the depth+normal prepass in view
// space; on a hit, reprojects into last frame's clip space and samples last
// frame's composited color (single-pass forward — see ssr.zig). Material-
// agnostic: the roughness blend against `env_prefiltered` lives in scene.frag.glsl.

layout(location = 0) in vec2 in_uv;

layout(set = 2, binding = 0) uniform sampler2D prepass_depth;
layout(set = 2, binding = 1) uniform sampler2D prepass_normal;
layout(set = 2, binding = 2) uniform sampler2D history_color; // last frame's hdr_color

// SDL3GPU SPIR-V fragment uniforms: set=3, binding=slot_index.
layout(set = 3, binding = 0) uniform FragUB {
    mat4 inv_proj;
    mat4 proj;
    // `prev_view_proj * inverse(current_view)` — takes a view-space hit
    // position directly to previous-frame clip space in one multiply.
    mat4 reproject;
    vec4 params; // x=thickness, y=step_scale, z=max_distance, w=active step count
} ubo;

layout(location = 0) out vec4 out_ssr; // rgb = reflected color, a = confidence

const int MAX_STEPS = 32;
const float MIN_STEP = 0.02;  // view-space meters — floor so nearby geometry still marches
const float EDGE_FADE = 0.1;  // fraction of screen width/height to fade near edges
// A ray heading toward open sky never hits anything, so it always burns the
// full MAX_STEPS budget — accelerating the step size only while over sky
// (not while near candidate geometry) reaches max_distance in far fewer
// iterations without touching hit accuracy for actual reflective surfaces.
const float SKY_STEP_GROWTH = 1.5;

// Identical to ssao.frag.glsl's reconstruction — see that file for the
// V-flip/NDC-convention rationale.
vec3 viewPosFromDepth(vec2 uv, float depth) {
    vec2 ndc_xy = vec2(uv.x, 1.0 - uv.y) * 2.0 - 1.0;
    vec4 clip = vec4(ndc_xy, depth * 2.0 - 1.0, 1.0);
    vec4 view = ubo.inv_proj * clip;
    return view.xyz / view.w;
}

void main() {
    float depth = texture(prepass_depth, in_uv).r;
    if (depth >= 0.9999) { out_ssr = vec4(0.0); return; } // sky/background

    vec3 P = viewPosFromDepth(in_uv, depth);
    vec3 N = normalize(texture(prepass_normal, in_uv).xyz * 2.0 - 1.0);
    vec3 V = normalize(-P);   // view-space eye is at the origin
    vec3 R = reflect(-V, N);  // -V = incident direction (eye -> surface)

    float ndotv = max(dot(N, V), 0.0);
    float thickness = ubo.params.x;
    float step_scale = ubo.params.y;
    float max_distance = ubo.params.z;

    // Step size scales with distance from the camera so the march reaches
    // proportionally further across a tens-of-metres scene without an
    // authored per-scene tunable.
    float step_len = max(MIN_STEP, abs(P.z) * step_scale);

    vec3 ray = P;
    bool hit = false;
    vec2 hit_uv = vec2(0.0);

    int steps = clamp(int(ubo.params.w), 1, MAX_STEPS);
    for (int i = 0; i < steps; i++) {
        ray += R * step_len;
        if (length(ray - P) > max_distance) break;

        vec4 clip = ubo.proj * vec4(ray, 1.0);
        if (clip.w <= 0.0) break; // behind the camera
        vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
        uv.y = 1.0 - uv.y; // same V flip as ssao.frag.glsl's reprojection

        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;

        float scene_depth_raw = texture(prepass_depth, uv).r;
        if (scene_depth_raw >= 0.9999) { step_len *= SKY_STEP_GROWTH; continue; } // marching over open sky
        vec3 scene_pos = viewPosFromDepth(uv, scene_depth_raw);

        // View-space z grows toward zero approaching the camera (see
        // ssao.frag.glsl's occluder test). A hit is where the ray has gone
        // behind the sampled surface (farther than it), within `thickness`.
        if (ray.z < scene_pos.z && scene_pos.z - ray.z < thickness) {
            hit = true;
            hit_uv = uv;
            break;
        }
    }

    if (!hit) { out_ssr = vec4(0.0); return; }

    // Reproject the view-space ray position at the hit into last frame's
    // clip space and sample last frame's composited color there.
    vec4 prev_clip = ubo.reproject * vec4(ray, 1.0);
    if (prev_clip.w <= 0.0) { out_ssr = vec4(0.0); return; }
    vec2 prev_uv = prev_clip.xy / prev_clip.w * 0.5 + 0.5;
    prev_uv.y = 1.0 - prev_uv.y;
    if (prev_uv.x < 0.0 || prev_uv.x > 1.0 || prev_uv.y < 0.0 || prev_uv.y > 1.0) {
        out_ssr = vec4(0.0);
        return;
    }

    vec3 color = texture(history_color, prev_uv).rgb;

    // Confidence: fade near the screen edges of the *current-frame* hit (an
    // off-screen source has no reflection to show) and at grazing angles
    // (classic SSR stretching artifact).
    vec2 edge = min(hit_uv, 1.0 - hit_uv);
    float edge_fade = smoothstep(0.0, EDGE_FADE, min(edge.x, edge.y));
    float grazing_fade = smoothstep(0.0, 0.2, ndotv);

    out_ssr = vec4(color, edge_fade * grazing_fade);
}
