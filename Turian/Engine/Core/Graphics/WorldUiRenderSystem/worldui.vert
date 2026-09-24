#version 460

// A world-space UI panel: a unit quad on the local XY plane (z = 0), centred at the origin,
// spanning [-0.5, 0.5] on each axis. The model matrix (push constant) places and sizes it in
// the world; the global UBO supplies the camera. UVs have origin top-left to match the
// straight-alpha RGBA texture the CPU-Skia backend uploads.

layout(location = 0) out vec2 vUv;

layout(set = 0, binding = 0) uniform GlobalUbo {
    mat4 projection;
    mat4 view;
};

layout(push_constant) uniform PushConsts {
    mat4 model;
} push;

// Two triangles, 6 vertices, from gl_VertexIndex alone.
const vec2 kCorners[6] = vec2[6](
    vec2(-0.5, -0.5), vec2( 0.5, -0.5), vec2( 0.5,  0.5),
    vec2(-0.5, -0.5), vec2( 0.5,  0.5), vec2(-0.5,  0.5)
);

void main()
{
    vec2 corner = kCorners[gl_VertexIndex];

    // Panel-local axes: +x right, +y up. UV origin is top-left, so v = 0 at +y.
    vUv = vec2(corner.x + 0.5, corner.y + 0.5);

    gl_Position = projection * view * push.model * vec4(corner, 0.0, 1.0);
}
