#version 450
layout(location = 0) in vec3 position;
layout(set = 0, binding = 0) uniform Globals { mat4 projection; mat4 view; } globals;
layout(push_constant) uniform Push { mat4 model; } push;
void main() {
    vec4 world = push.model * vec4(position, 1.0);
    gl_Position = globals.projection * globals.view * world;
}
