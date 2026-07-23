#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

struct SubmeshBounds {
    vec4 min_pad;  // xyz local-space min, w unused
    vec4 max_pad;  // xyz local-space max, w unused
    uvec4 range;   // x = first_index, y = num_indices, zw unused
};

struct IndirectCmd {
    uint num_indices;
    uint num_instances;
    uint first_index;
    int vertex_offset;
    uint first_instance;
};

// SDL3GPU SPIR-V compute resource sets: 0 = read-only storage buffers,
// 1 = read-write storage buffers, 2 = uniform buffers.
layout(std430, set = 0, binding = 0) readonly buffer BoundsBuf {
    SubmeshBounds bounds[];
} bounds_in;

layout(std430, set = 1, binding = 0) writeonly buffer IndirectBuf {
    IndirectCmd cmds[];
} indirect_out;

layout(set = 2, binding = 0) uniform CullUB {
    mat4 model;
    vec4 planes[6]; // each: xyz = normal, w = d; positive-side test
    uvec4 submesh_count; // x = count
} ub;

void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= ub.submesh_count.x) return;

    vec3 lmin = bounds_in.bounds[i].min_pad.xyz;
    vec3 lmax = bounds_in.bounds[i].max_pad.xyz;
    vec3 center_l = (lmin + lmax) * 0.5;
    vec3 extent_l = (lmax - lmin) * 0.5;

    vec3 center = (ub.model * vec4(center_l, 1.0)).xyz;
    mat3 m3 = mat3(ub.model);
    mat3 abs_m3 = mat3(abs(m3[0]), abs(m3[1]), abs(m3[2]));
    vec3 extent = abs_m3 * extent_l;

    bool visible = true;
    for (int p = 0; p < 6; ++p) {
        vec4 pl = ub.planes[p];
        float dist = dot(pl.xyz, center) + pl.w;
        float radius = dot(abs(pl.xyz), extent);
        if (dist + radius < 0.0) {
            visible = false;
            break;
        }
    }

    indirect_out.cmds[i].num_indices = bounds_in.bounds[i].range.y;
    indirect_out.cmds[i].num_instances = visible ? 1u : 0u;
    indirect_out.cmds[i].first_index = bounds_in.bounds[i].range.x;
    indirect_out.cmds[i].vertex_offset = 0;
    indirect_out.cmds[i].first_instance = 0;
}
