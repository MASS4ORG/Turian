#version 460

const int uboLights = 10;

// Vertex inputs — must match Vertex.GetAttributeDescriptions in Vertex.cs.
layout(location = 0) in vec3 position;
layout(location = 1) in vec3 color;
layout(location = 2) in vec3 normal;
layout(location = 3) in vec2 uv;
layout(location = 4) in vec4 tangent;   // glTF: xyz = tangent, w = handedness sign

// Outputs to fragment shader.
layout(location = 0) out vec3 fragColor;
layout(location = 1) out vec3 fragPosWorld;
layout(location = 2) out vec3 fragNormalWorld;
layout(location = 3) out vec2 fragUv;
layout(location = 4) out vec3 fragTangentWorld;
layout(location = 5) out vec3 fragBitangentWorld;
layout(location = 6) flat out vec4 fragInstanceColor;
layout(location = 7) flat out vec4 fragCustomData;
layout(location = 8) flat out uint fragLayerMask;

struct PointLight {
    vec4 position;
    vec4 color;
};

layout(set = 0, binding = 0) uniform GlobalUbo {
    mat4 projection;
    mat4 view;
    vec4 front;
    vec4 ambientColor;
    PointLight pointLights[uboLights];
} ubo;

// Per-instance data — must match InstanceData.cs. normal[3][3] carries the render layer's mask bits.
struct InstanceData {
    mat4 model;
    mat4 normal;
    vec4 color;
    vec4 custom;
};

layout(std430, set = 2, binding = 0) readonly buffer Instances {
    InstanceData instances[];
};

void main() {
    // gl_InstanceIndex already includes the draw's firstInstance.
    mat4 model = instances[gl_InstanceIndex].model;
    mat4 normalMatrix = instances[gl_InstanceIndex].normal;

    vec4 positionWorld = model * vec4(position, 1.0);
    gl_Position = ubo.projection * ubo.view * positionWorld;

    mat3 nm = mat3(normalMatrix);
    fragNormalWorld = normalize(nm * normal);

    // Geometry without tangents carries a zero tangent; normalizing it would produce NaN
    // and poison every value derived from the tangent frame.
    vec3 tangentWorld = nm * tangent.xyz;
    float tangentLength = length(tangentWorld);
    fragTangentWorld = tangentLength > 1e-6 ? tangentWorld / tangentLength : vec3(0.0);
    fragBitangentWorld = cross(fragNormalWorld, fragTangentWorld) * tangent.w;

    fragPosWorld = positionWorld.xyz;
    fragUv = uv;
    fragColor = color;
    fragInstanceColor = instances[gl_InstanceIndex].color;
    fragCustomData = instances[gl_InstanceIndex].custom;
    fragLayerMask = floatBitsToUint(normalMatrix[3][3]);
}
