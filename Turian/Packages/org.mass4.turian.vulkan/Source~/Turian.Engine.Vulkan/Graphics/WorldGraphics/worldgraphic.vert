#version 460
layout(location = 0) in vec3 position;
layout(location = 1) in vec4 color;
layout(location = 2) in vec2 uv;
layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(set = 0, binding = 0) uniform GlobalUbo { mat4 projection; mat4 view; };
void main() {
    vColor = color;
    vUv = uv;
    gl_Position = projection * view * vec4(position, 1.0);
}
