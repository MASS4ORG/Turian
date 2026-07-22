#version 450

// Fullscreen triangle (no vertex buffer) at the far plane, covering the whole
// viewport in three vertices via the standard `gl_VertexIndex` trick.

layout(location = 0) out vec2 out_ndc;

void main() {
    vec2 pos = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    out_ndc = pos * 2.0 - 1.0;

    vec4 clip = vec4(out_ndc, 1.0, 1.0);
    // Same GL-style [-1,1] -> SDL_GPU [0,1] Z remap as scene.vert.glsl.
    clip.z = (clip.z + clip.w) * 0.5;
    gl_Position = clip;
}
