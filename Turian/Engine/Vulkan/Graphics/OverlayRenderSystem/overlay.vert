#version 450

// Fullscreen triangle from the vertex index alone — no vertex buffer bound.
// Verts (-1,-1), (3,-1), (-1,3) cover the screen; uv (0,0),(2,0),(0,2) covers [0,1]^2.
// Vulkan NDC y points down and framebuffer row 0 is at y = -1, so uv.y maps straight
// onto the source texture's top-left-origin rows with no flip.

layout(location = 0) out vec2 vUv;

void main()
{
    vUv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(vUv * 2.0 - 1.0, 0.0, 1.0);
}
