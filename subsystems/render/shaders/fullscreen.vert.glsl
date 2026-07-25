#version 450

// Fullscreen triangle (no vertex buffer) covering the whole viewport in three
// vertices via the standard `gl_VertexIndex` trick. Shared by every
// post-process pass (threshold, downsample, upsample, composite) — unlike
// `skybox.vert.glsl`, this outputs a plain 0..1 UV instead of a view ray.

layout(location = 0) out vec2 out_uv;

void main() {
    vec2 pos = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    // SDL_GPU's unified NDC is Y-up (see scene.vert.glsl), implemented via an
    // internal flip against its Y-down-native backends — so a render
    // target's texel row 0 (v=0) is the *top* of what's displayed, not the
    // bottom. `pos` directly would put v=0 at the bottom, flipping every
    // pass that samples a previously-rendered target (the whole point of
    // this shader) upside down.
    out_uv = vec2(pos.x, 1.0 - pos.y);
    gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}
