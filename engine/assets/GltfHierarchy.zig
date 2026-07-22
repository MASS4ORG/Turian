/// Per-glTF-mesh geometry grouping and node-hierarchy import, backed by the
/// same cgltf wrapper as `GltfLoader.zig`. Unlike `GltfLoader.load` (which
/// flattens every primitive in the file into one combined mesh),
/// `loadMeshes` cooks one `Mesh` per glTF *mesh*, and `loadHierarchy` walks
/// the file's flat node array so an importer can rebuild the source node
/// graph (parent/child, local transform, mesh reference) as a GameObject
/// tree instead of a single flattened blob.
const std = @import("std");
const gltf_loader = @import("GltfLoader.zig");
const Mesh = @import("Mesh.zig").Mesh;
const CgltfMeshData = gltf_loader.CgltfMeshData;

// ── Per-mesh geometry grouping ──────────────────────────────────────────────

pub const MeshGroup = struct {
    /// The glTF mesh's name, or empty if unnamed. Owned by the group's arena.
    name: []const u8,
    mesh: Mesh,
};

fn cStr(buf: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, buf, 0) orelse buf.len;
    return buf[0..end];
}

/// Load every glTF mesh in a file as its own `Mesh`, each with a submesh
/// table scoped to that mesh's own primitives (glTF meshes referenced by
/// multiple nodes yield one shared `MeshGroup`, matched by index). Free each
/// group's `mesh` with `Mesh.deinit` and `name`/the returned slice with
/// `allocator`.
pub fn loadMeshes(allocator: std.mem.Allocator, path: []const u8) ![]MeshGroup {
    var raw = try gltf_loader.loadRawAll(path);
    defer gltf_loader.freeRawAll(&raw);

    if (raw.mesh_count == 0) return &.{};
    const prims = raw.primitives.?[0..raw.primitive_count];
    const names = raw.mesh_names.?[0..raw.mesh_count];

    // `cgltf_wrap_load_all` emits primitives in mesh-major order (all of mesh
    // 0's primitives, then mesh 1's, ...), so grouping is a single linear
    // scan for contiguous runs of the same `mesh_index` — no bucketing pass.
    var groups: std.ArrayList(MeshGroup) = .empty;
    errdefer {
        for (groups.items) |*g| {
            g.mesh.deinit();
            allocator.free(g.name);
        }
        groups.deinit(allocator);
    }

    var start: usize = 0;
    while (start < prims.len) {
        const mi = prims[start].mesh_index;
        var end = start + 1;
        while (end < prims.len and prims[end].mesh_index == mi) end += 1;

        var mesh = try gltf_loader.assemblePrimitives(allocator, prims[start..end]);
        errdefer mesh.deinit();
        const name = if (mi >= 0 and @as(usize, @intCast(mi)) < names.len)
            try allocator.dupe(u8, cStr(&names[@intCast(mi)].name))
        else
            try allocator.dupe(u8, "");
        try groups.append(allocator, .{ .name = name, .mesh = mesh });
        start = end;
    }

    return try groups.toOwnedSlice(allocator);
}

// ── Node hierarchy ───────────────────────────────────────────────────────────

const CgltfNodeData = extern struct {
    name: [128]u8,
    parent_index: i32,
    mesh_index: i32,
    translation: [3]f32,
    rotation: [4]f32,
    scale: [3]f32,
};

const CgltfNodeHierarchy = extern struct {
    nodes: ?[*]CgltfNodeData,
    node_count: u32,
};

extern fn cgltf_wrap_load_hierarchy(path: [*:0]const u8, out: *CgltfNodeHierarchy) c_int;
extern fn cgltf_wrap_free_hierarchy(out: *CgltfNodeHierarchy) void;

/// One node of a glTF file's flat node array, with its local transform
/// already converted to the engine's convention (Euler degrees, see
/// `quatToEulerDeg`).
pub const GltfNode = struct {
    name: []const u8,
    /// Index into the hierarchy's `nodes`, or -1 for a root.
    parent: i32,
    /// Index into the sibling `loadMeshes` result, or -1 (no mesh).
    mesh_index: i32,
    position: [3]f32,
    /// Pitch/yaw/roll in degrees, matching `Transform.rotation` /
    /// `Matrix4.rotationEuler`'s `Ry(yaw)*Rx(pitch)*Rz(roll)` convention.
    rotation_euler_deg: [3]f32,
    scale: [3]f32,
};

/// Owns the memory backing a `loadHierarchy` result.
pub const GltfHierarchy = struct {
    nodes: []GltfNode,
    arena: std.heap.ArenaAllocator,

    pub fn deinit(self: *@This()) void {
        self.arena.deinit();
    }
};

/// Load every node in a glTF/GLB file's flat node array (not just those
/// reachable from the default scene — matching `cgltf_wrap_load_hierarchy`),
/// independent of geometry (`load`/`loadMeshes`).
pub fn loadHierarchy(allocator: std.mem.Allocator, path: []const u8) !GltfHierarchy {
    var path_buf: [512]u8 = undefined;
    if (path.len >= path_buf.len) return error.PathTooLong;
    @memcpy(path_buf[0..path.len], path);
    path_buf[path.len] = 0;

    var raw: CgltfNodeHierarchy = undefined;
    if (cgltf_wrap_load_hierarchy(@ptrCast(&path_buf), &raw) != 0)
        return error.GltfLoadFailed;
    defer cgltf_wrap_free_hierarchy(&raw);

    var arena = std.heap.ArenaAllocator.init(allocator);
    errdefer arena.deinit();
    const a = arena.allocator();

    const src = raw.nodes.?[0..raw.node_count];
    const nodes = try a.alloc(GltfNode, src.len);
    for (src, 0..) |n, i| {
        nodes[i] = .{
            .name = try a.dupe(u8, cStr(&n.name)),
            .parent = n.parent_index,
            .mesh_index = n.mesh_index,
            .position = n.translation,
            .rotation_euler_deg = quatToEulerDeg(n.rotation),
            .scale = n.scale,
        };
    }

    return .{ .nodes = nodes, .arena = arena };
}

const quatToEulerDeg = @import("QuatEuler.zig").quatToEulerDeg;

// ── Tests ─────────────────────────────────────────────────────────────────────

fn writeTmpGltf(tmp: *std.testing.TmpDir, name: []const u8, json: []const u8) ![]u8 {
    try tmp.dir.writeFile(std.testing.io, .{ .sub_path = name, .data = json });
    var buf: [256]u8 = undefined;
    return std.fmt.bufPrint(&buf, ".zig-cache/tmp/{s}/{s}", .{ tmp.sub_path, name });
}

test "loadMeshes groups primitives by glTF mesh, not flattened" {
    const a = std.testing.allocator;

    // 3 vertices (VEC3 f32 positions) + 3 indices (u16), one shared buffer.
    var bytes: [42]u8 = undefined;
    const positions = [_]f32{ 0, 0, 0, 1, 0, 0, 0, 1, 0 };
    @memcpy(bytes[0..36], std.mem.sliceAsBytes(&positions));
    const idx = [_]u16{ 0, 1, 2 };
    @memcpy(bytes[36..42], std.mem.sliceAsBytes(&idx));
    var b64buf: [64]u8 = undefined;
    const b64 = std.base64.standard.Encoder.encode(&b64buf, &bytes);

    const gltf = try std.fmt.allocPrint(a,
        \\{{
        \\  "asset": {{"version": "2.0"}},
        \\  "buffers": [{{"uri": "data:application/octet-stream;base64,{s}", "byteLength": 42}}],
        \\  "bufferViews": [
        \\    {{"buffer": 0, "byteOffset": 0, "byteLength": 36}},
        \\    {{"buffer": 0, "byteOffset": 36, "byteLength": 6}}
        \\  ],
        \\  "accessors": [
        \\    {{"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3"}},
        \\    {{"bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR"}}
        \\  ],
        \\  "meshes": [
        \\    {{"name": "MeshA", "primitives": [{{"attributes": {{"POSITION": 0}}, "indices": 1}}]}},
        \\    {{"name": "MeshB", "primitives": [{{"attributes": {{"POSITION": 0}}, "indices": 1}}]}}
        \\  ],
        \\  "nodes": [{{"name": "NodeA", "mesh": 0}}, {{"name": "NodeB", "mesh": 1}}],
        \\  "scenes": [{{"nodes": [0, 1]}}],
        \\  "scene": 0
        \\}}
    , .{b64});
    defer a.free(gltf);

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    const path = try writeTmpGltf(&tmp, "m.gltf", gltf);

    const groups = try loadMeshes(std.testing.allocator, path);
    defer {
        for (groups) |*g| {
            var mesh = g.mesh;
            mesh.deinit();
            std.testing.allocator.free(g.name);
        }
        std.testing.allocator.free(groups);
    }

    try std.testing.expectEqual(@as(usize, 2), groups.len);
    try std.testing.expectEqualStrings("MeshA", groups[0].name);
    try std.testing.expectEqualStrings("MeshB", groups[1].name);
    try std.testing.expectEqual(@as(usize, 3), groups[0].mesh.vertices.len);
    try std.testing.expectEqual(@as(usize, 1), groups[0].mesh.submeshes.len);
}

test "loadHierarchy walks parent/child indices and TRS transform" {
    const gltf =
        \\{
        \\  "asset": {"version": "2.0"},
        \\  "nodes": [
        \\    {"name": "Root", "children": [1], "translation": [1, 2, 3]},
        \\    {"name": "Child", "rotation": [0, 0.7071068, 0, 0.7071068], "scale": [2, 2, 2]}
        \\  ],
        \\  "scenes": [{"nodes": [0]}],
        \\  "scene": 0
        \\}
    ;

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    const path = try writeTmpGltf(&tmp, "h.gltf", gltf);

    var hier = try loadHierarchy(std.testing.allocator, path);
    defer hier.deinit();

    try std.testing.expectEqual(@as(usize, 2), hier.nodes.len);
    try std.testing.expectEqualStrings("Root", hier.nodes[0].name);
    try std.testing.expectEqual(@as(i32, -1), hier.nodes[0].parent);
    try std.testing.expectEqual(@as(f32, 1), hier.nodes[0].position[0]);
    try std.testing.expectEqual(@as(i32, -1), hier.nodes[0].mesh_index);

    try std.testing.expectEqualStrings("Child", hier.nodes[1].name);
    try std.testing.expectEqual(@as(i32, 0), hier.nodes[1].parent);
    try std.testing.expectApproxEqAbs(@as(f32, 90.0), hier.nodes[1].rotation_euler_deg[1], 0.01);
    try std.testing.expectEqual(@as(f32, 2), hier.nodes[1].scale[0]);
}

test "loadHierarchy decomposes a matrix-only node" {
    const gltf =
        \\{
        \\  "asset": {"version": "2.0"},
        \\  "nodes": [
        \\    {"name": "Scaled", "matrix": [2,0,0,0, 0,2,0,0, 0,0,2,0, 5,6,7,1]}
        \\  ],
        \\  "scenes": [{"nodes": [0]}],
        \\  "scene": 0
        \\}
    ;

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    const path = try writeTmpGltf(&tmp, "mat.gltf", gltf);

    var hier = try loadHierarchy(std.testing.allocator, path);
    defer hier.deinit();

    try std.testing.expectEqual(@as(usize, 1), hier.nodes.len);
    try std.testing.expectApproxEqAbs(@as(f32, 5), hier.nodes[0].position[0], 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 6), hier.nodes[0].position[1], 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 7), hier.nodes[0].position[2], 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 2), hier.nodes[0].scale[0], 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 2), hier.nodes[0].scale[1], 1e-4);
    try std.testing.expectApproxEqAbs(@as(f32, 2), hier.nodes[0].scale[2], 1e-4);
    // Identity rotation (axis-aligned scale matrix): all Euler angles ~0.
    try std.testing.expectApproxEqAbs(@as(f32, 0), hier.nodes[0].rotation_euler_deg[0], 0.1);
    try std.testing.expectApproxEqAbs(@as(f32, 0), hier.nodes[0].rotation_euler_deg[1], 0.1);
    try std.testing.expectApproxEqAbs(@as(f32, 0), hier.nodes[0].rotation_euler_deg[2], 0.1);
}
