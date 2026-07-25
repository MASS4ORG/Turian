#version 450

// Converts the scene's equirectangular HDR environment map into one face of
// a cubemap. Drawn once per face (6 draws) into the un-prefiltered base
// cubemap that `prefilter_specular.frag.glsl` then samples — see
// `ibl_prefilter.zig`. Paired with `fullscreen.vert.glsl`.

layout(location = 0) in vec2 in_uv;

layout(set = 2, binding = 0) uniform sampler2D env_equirect;

layout(set = 3, binding = 0) uniform FragUB {
    vec4 face; // x = face index (0..5), yzw unused
} ubo;

layout(location = 0) out vec4 out_color;

const float PI = 3.14159265359;

// Must match `dirToEquirectUv` in scene.frag.glsl/skybox.frag.glsl and the
// CPU-side SH projection convention in `subsystems/render/assets.zig`.
vec2 dirToEquirectUv(vec3 d) {
    float u = atan(d.x, -d.z) / (2.0 * PI) + 0.5;
    float v = acos(clamp(d.y, -1.0, 1.0)) / PI;
    return vec2(u, v);
}

// Standard cube-face direction basis (matches the hardware `samplerCube`
// face order every backend SDL_GPU targets uses: +X,-X,+Y,-Y,+Z,-Z), so a
// direction written here lands on the same texel a later `textureCube`
// lookup by that direction reads.
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

void main() {
    vec3 dir = normalize(faceDir(int(ubo.face.x), in_uv));
    out_color = vec4(texture(env_equirect, dirToEquirectUv(dir)).rgb, 1.0);
}
