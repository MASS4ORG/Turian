/// glTF 2.0 / GLB loader backed by cgltf (single-header C library).
/// Loads the first mesh's first primitive: positions, normals, UVs, indices.
const std = @import("std");
const Mesh = @import("Mesh.zig").Mesh;
const Vertex = @import("Mesh.zig").Vertex;

const CgltfMeshData = extern struct {
    positions: [*]f32,
    normals: ?[*]f32,
    uvs: ?[*]f32,
    indices: [*]u32,
    vertex_count: u32,
    index_count: u32,
    has_normals: c_int,
    has_uvs: c_int,
};

extern fn cgltf_wrap_load(path: [*:0]const u8, out: *CgltfMeshData) c_int;
extern fn cgltf_wrap_free(data: *CgltfMeshData) void;

pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Mesh {
    _ = io;

    var path_buf: [512]u8 = undefined;
    if (path.len >= path_buf.len) return error.PathTooLong;
    @memcpy(path_buf[0..path.len], path);
    path_buf[path.len] = 0;

    var raw: CgltfMeshData = undefined;
    if (cgltf_wrap_load(@ptrCast(&path_buf), &raw) != 0)
        return error.GltfLoadFailed;
    defer cgltf_wrap_free(&raw);

    const vcount = raw.vertex_count;
    const icount = raw.index_count;

    var verts = try allocator.alloc(Vertex, vcount);
    errdefer allocator.free(verts);

    for (0..vcount) |i| {
        const p = raw.positions;
        var nx: f32 = 0;
        var ny: f32 = 1;
        var nz: f32 = 0;
        if (raw.has_normals != 0) {
            const n = raw.normals.?;
            nx = n[i * 3 + 0];
            ny = n[i * 3 + 1];
            nz = n[i * 3 + 2];
        }
        var u: f32 = 0;
        var v: f32 = 0;
        if (raw.has_uvs != 0) {
            const uv = raw.uvs.?;
            u = uv[i * 2 + 0];
            v = uv[i * 2 + 1];
        }
        verts[i] = .{
            .px = p[i * 3 + 0],
            .py = p[i * 3 + 1],
            .pz = p[i * 3 + 2],
            .nx = nx,
            .ny = ny,
            .nz = nz,
            .u = u,
            .v = v,
        };
    }

    const indices = try allocator.alloc(u32, icount);
    errdefer allocator.free(indices);
    @memcpy(indices, raw.indices[0..icount]);

    if (raw.has_normals == 0) computeFlatNormals(verts, indices);

    var mesh = Mesh{ .vertices = verts, .indices = indices, .allocator = allocator };
    mesh.computeBounds();
    return mesh;
}

fn computeFlatNormals(verts: []Vertex, idxs: []const u32) void {
    var i: usize = 0;
    while (i + 2 < idxs.len) : (i += 3) {
        const a = idxs[i];
        const b = idxs[i + 1];
        const c = idxs[i + 2];
        if (a >= verts.len or b >= verts.len or c >= verts.len) continue;
        const va = verts[a];
        const vb = verts[b];
        const vc = verts[c];
        const e1 = [3]f32{ vb.px - va.px, vb.py - va.py, vb.pz - va.pz };
        const e2 = [3]f32{ vc.px - va.px, vc.py - va.py, vc.pz - va.pz };
        const nx = e1[1] * e2[2] - e1[2] * e2[1];
        const ny = e1[2] * e2[0] - e1[0] * e2[2];
        const nz = e1[0] * e2[1] - e1[1] * e2[0];
        const len = @sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-9) continue;
        const nn = [3]f32{ nx / len, ny / len, nz / len };
        verts[a].nx = nn[0];
        verts[a].ny = nn[1];
        verts[a].nz = nn[2];
        verts[b].nx = nn[0];
        verts[b].ny = nn[1];
        verts[b].nz = nn[2];
        verts[c].nx = nn[0];
        verts[c].ny = nn[1];
        verts[c].nz = nn[2];
    }
}
