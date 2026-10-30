#version 460
layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 0) out vec4 outColor;
layout(set = 1, binding = 0) uniform sampler2D image;
layout(push_constant) uniform PushConsts { int sdf; } push;
void main() {
    vec4 sampleColor = texture(image, vUv);
    if (push.sdf != 0) {
        float edge = max(fwidth(sampleColor.a), 1.0 / 255.0);
        sampleColor = vec4(1.0, 1.0, 1.0, smoothstep(0.5 - edge, 0.5 + edge, sampleColor.a));
    }
    outColor = vColor * sampleColor;
    if (outColor.a <= 0.001) discard;
}
