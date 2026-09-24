#version 460

// Gizmo quad expansion shader: each line segment is submitted as 6 vertices (two triangles).
// Vertices carry both endpoints plus a per-corner (side, end) pair; the shader expands the quad
// in screen space (pixel thickness) and snaps depth to the projected segment.

layout(location = 0) in vec3 aA;
layout(location = 1) in vec3 aB;
layout(location = 2) in vec4 aColor;
layout(location = 3) in float aThickness;
layout(location = 4) in float aSide; // +1 / -1 across the segment width
layout(location = 5) in float aEnd;  // -1 anchored at A, +1 anchored at B

layout(location = 0) out vec4 fragColor;

layout(set = 0, binding = 0) uniform GizmoUbo {
    mat4 projection;
    mat4 view;
};

layout(push_constant) uniform PushConsts {
    vec2 viewportSize; // framebuffer size in pixels
    float depthOffset; // NDC depth bias applied to the final position
} push;

void main() {
    fragColor = aColor;

    vec4 p1 = projection * view * vec4(aA, 1.0);
    vec4 p2 = projection * view * vec4(aB, 1.0);

    // A vertex is at/behind the far plane in clip space when z >= w (NDC z in [0;1]).
    // Skip segments that are entirely beyond it.
    if (p1.z >= p1.w && p2.z >= p2.w) {
        gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
        return;
    }

    // Skip segments fully behind the camera (w <= 0).
    if (p1.w <= 0.0 && p2.w <= 0.0) {
        gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
        return;
    }

    // Clamp the far end of a segment straddling the far plane so the quad does not blow up.
    if (p2.z > p2.w && p1.z < p1.w) {
        float t = (p1.w - p1.z) / ((p2.z - p1.z) - (p2.w - p1.w));
        t = clamp(t, 0.0, 1.0);
        p2 = mix(p1, p2, t);
    }
    if (p1.z > p1.w && p2.z < p2.w) {
        float t = (p2.w - p2.z) / ((p1.z - p2.z) - (p1.w - p2.w));
        t = clamp(t, 0.0, 1.0);
        p1 = mix(p2, p1, t);
    }

    // NDC [-1;1] -> framebuffer pixels (NVK viewport is never Y-flipped).
    vec2 pxl1 = (p1.xy / p1.w + 1.0) * 0.5 * push.viewportSize;
    vec2 pxl2 = (p2.xy / p2.w + 1.0) * 0.5 * push.viewportSize;

    vec2 dir = pxl2 - pxl1;
    float len = length(dir);
    vec2 tangent = len > 1e-4 ? dir / len : vec2(1.0, 0.0);
    vec2 normal = vec2(-tangent.y, tangent.x);

    float halfThickness = aThickness * 0.5;
    vec2 base = aEnd < 0.0 ? pxl1 : pxl2;
    vec2 corner = base + normal * (aSide * halfThickness) + tangent * (aEnd * halfThickness);

    vec2 cornerNdc = (corner / push.viewportSize) * 2.0 - 1.0;
    float cornerZ = (aEnd < 0.0 ? p1.z / p1.w : p2.z / p2.w) + push.depthOffset;

    gl_Position = vec4(cornerNdc, cornerZ, 1.0);
}