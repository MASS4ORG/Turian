/// Per-FBX-mesh geometry grouping and node-hierarchy import, backed by the
/// same ufbx wrapper as `FbxLoader.zig`. Unlike `FbxLoader.load` (which bakes
/// every node INSTANCE's geometry into world space and flattens the whole
/// file into one combined mesh), `loadMeshes` cooks one `Mesh` per *unique*
/// FBX mesh datablock in local space, and `loadHierarchy` walks the file's
/// flat node array so an importer can rebuild the source node graph
/// (parent/child, local transform, mesh reference) as a GameObject tree
/// instead of a single flattened blob — mirrors `GltfHierarchy.zig`.
const std = @import("std");
const Mesh = @import("Mesh.zig").Mesh;
const Vertex = @import("Mesh.zig").Vertex;
const Submesh = @import("Mesh.zig").Submesh;
const quatToEulerDeg = @import("QuatEuler.zig").quatToEulerDeg;

// ── Per-mesh geometry grouping ──────────────────────────────────────────────

pub const MeshGroup = struct {
    /// The FBX mesh's name, or empty if unnamed. Owned by the group's arena.
    name: []const u8,
    mesh: Mesh,
};

/// Mirrors `FbxMeshData` in `fbx_wrap.h`, plus the trailing `mesh_index` that
/// makes `fbx_wrap_load_meshes`'s local-space-per-mesh chunks groupable —
/// the extra field is why this isn't the same extern struct `FbxLoader.zig`
/// uses for the world-space flatten path.
const FbxMeshLocalData = extern struct {
    positions: [*]f32,
    normals: ?[*]f32,
    uvs: ?[*]f32,
    indices: [*]u32,
    vertex_count: u32,
    index_count: u32,
    has_normals: c_int,
    has_uvs: c_int,
    material_index: c_int,
    mesh_index: c_int,
};

const FbxMeshName = extern struct {
    name: [128]u8,
};

const FbxMultiMeshLocalData = extern struct {
    primitives: ?[*]FbxMeshLocalData,
    primitive_count: u32,
    mesh_names: ?[*]FbxMeshName,
    mesh_count: u32,
};

extern fn fbx_wrap_load_meshes(path: [*:0]const u8, out: *FbxMultiMeshLocalData) c_int;
extern fn fbx_wrap_free_meshes(out: *FbxMultiMeshLocalData) void;

fn cStr(buf: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, buf, 0) orelse buf.len;
    return buf[0..end];
}

/// Assembles one unique mesh's material-part chunks (already welded by
/// `fbx_wrap_load_meshes`) into a `Mesh` with a submesh table — same shape as
/// `FbxLoader.load`'s per-chunk assembly, scoped to one mesh instead of the
/// whole file.
fn assembleChunks(allocator: std.mem.Allocator, chunks: []const FbxMeshLocalData) !Mesh {
    var total_v: usize = 0;
    var total_i: usize = 0;
    for (chunks) |c| {
        total_v += c.vertex_count;
        total_i += c.index_count;
    }

    var verts = try allocator.alloc(Vertex, total_v);
    errdefer allocator.free(verts);
    var indices = try allocator.alloc(u32, total_i);
    errdefer allocator.free(indices);
    var subs = try allocator.alloc(Submesh, chunks.len);
    errdefer allocator.free(subs);

    var vbase: usize = 0;
    var ibase: usize = 0;
    for (chunks, 0..) |c, si| {
        const vcount = c.vertex_count;
        const icount = c.index_count;

        for (0..vcount) |i| {
            const pos = c.positions;
            var nx: f32 = 0;
            var ny: f32 = 1;
            var nz: f32 = 0;
            if (c.has_normals != 0) {
                const n = c.normals.?;
                nx = n[i * 3 + 0];
                ny = n[i * 3 + 1];
                nz = n[i * 3 + 2];
            }
            var u: f32 = 0;
            var v: f32 = 0;
            if (c.has_uvs != 0) {
                const uv = c.uvs.?;
                u = uv[i * 2 + 0];
                v = uv[i * 2 + 1];
            }
            verts[vbase + i] = .{
                .px = pos[i * 3 + 0],
                .py = pos[i * 3 + 1],
                .pz = pos[i * 3 + 2],
                .nx = nx,
                .ny = ny,
                .nz = nz,
                .u = u,
                .v = v,
            };
        }

        const chunk_verts = verts[vbase .. vbase + vcount];
        const local_indices = c.indices[0..icount];
        if (c.has_normals == 0) Mesh.computeFlatNormals(chunk_verts, local_indices);

        const chunk_indices = indices[ibase .. ibase + icount];
        for (0..icount) |i| chunk_indices[i] = local_indices[i] + @as(u32, @intCast(vbase));

        subs[si] = .{
            .index_offset = @intCast(ibase),
            .index_count = icount,
            .material_slot = c.material_index,
        };

        vbase += vcount;
        ibase += icount;
    }

    var mesh = Mesh{ .vertices = verts, .indices = indices, .submeshes = subs, .allocator = allocator };
    mesh.computeBounds();
    return mesh;
}

/// Load every unique FBX mesh datablock in a file as its own `Mesh`, each
/// with a submesh table scoped to that mesh's own material parts (an FBX
/// mesh instanced by multiple nodes yields one shared `MeshGroup`, matched by
/// index). Free each group's `mesh` with `Mesh.deinit` and `name`/the
/// returned slice with `allocator`.
pub fn loadMeshes(allocator: std.mem.Allocator, path: []const u8) ![]MeshGroup {
    var path_buf: [512]u8 = undefined;
    if (path.len >= path_buf.len) return error.PathTooLong;
    @memcpy(path_buf[0..path.len], path);
    path_buf[path.len] = 0;

    var raw: FbxMultiMeshLocalData = undefined;
    if (fbx_wrap_load_meshes(@ptrCast(&path_buf), &raw) != 0)
        return error.FbxLoadFailed;
    defer fbx_wrap_free_meshes(&raw);

    if (raw.mesh_count == 0) return &.{};
    const chunks = raw.primitives.?[0..raw.primitive_count];
    const names = raw.mesh_names.?[0..raw.mesh_count];

    // `fbx_wrap_load_meshes` emits chunks in mesh-major order (all of mesh
    // 0's material parts, then mesh 1's, ...), so grouping is a single linear
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
    while (start < chunks.len) {
        const mi = chunks[start].mesh_index;
        var end = start + 1;
        while (end < chunks.len and chunks[end].mesh_index == mi) end += 1;

        var mesh = try assembleChunks(allocator, chunks[start..end]);
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

const FbxNodeData = extern struct {
    name: [128]u8,
    parent_index: i32,
    mesh_index: i32,
    translation: [3]f32,
    rotation: [4]f32,
    scale: [3]f32,
    light_type: i32,
    light_color: [3]f32,
    light_intensity: f32,
    light_inner_deg: f32,
    light_outer_deg: f32,
    light_cast_shadows: i32,
};

const FbxNodeHierarchy = extern struct {
    nodes: ?[*]FbxNodeData,
    node_count: u32,
};

extern fn fbx_wrap_load_hierarchy(path: [*:0]const u8, out: *FbxNodeHierarchy) c_int;
extern fn fbx_wrap_free_hierarchy(out: *FbxNodeHierarchy) void;

/// One node of an FBX file's flat node array (including ufbx's synthetic
/// root, which becomes this hierarchy's single tree root), with its local
/// transform already converted to the engine's convention (Euler degrees).
pub const FbxNode = struct {
    name: []const u8,
    /// Index into the hierarchy's `nodes`, or -1 for the root.
    parent: i32,
    /// Index into the sibling `loadMeshes` result, or -1 (no mesh).
    mesh_index: i32,
    position: [3]f32,
    rotation_euler_deg: [3]f32,
    scale: [3]f32,
    /// Light on this node: -1 none, 0 point, 1 directional, 2 spot. When set,
    /// the aim is already baked into `rotation_euler_deg` (node +Z = light aim).
    light_type: i32 = -1,
    light_color: [3]f32 = .{ 1, 1, 1 },
    light_intensity: f32 = 1,
    light_inner_deg: f32 = 0,
    light_outer_deg: f32 = 35,
    light_cast_shadows: bool = false,
};

/// Owns the memory backing a `loadHierarchy` result.
pub const FbxHierarchy = struct {
    nodes: []FbxNode,
    arena: std.heap.ArenaAllocator,

    pub fn deinit(self: *@This()) void {
        self.arena.deinit();
    }
};

/// Load every node in an FBX file's flat node array, independent of geometry
/// (`load`/`loadMeshes`).
pub fn loadHierarchy(allocator: std.mem.Allocator, path: []const u8) !FbxHierarchy {
    var path_buf: [512]u8 = undefined;
    if (path.len >= path_buf.len) return error.PathTooLong;
    @memcpy(path_buf[0..path.len], path);
    path_buf[path.len] = 0;

    var raw: FbxNodeHierarchy = undefined;
    if (fbx_wrap_load_hierarchy(@ptrCast(&path_buf), &raw) != 0)
        return error.FbxLoadFailed;
    defer fbx_wrap_free_hierarchy(&raw);

    var arena = std.heap.ArenaAllocator.init(allocator);
    errdefer arena.deinit();
    const a = arena.allocator();

    const src = raw.nodes.?[0..raw.node_count];
    const nodes = try a.alloc(FbxNode, src.len);
    for (src, 0..) |n, i| {
        nodes[i] = .{
            .name = try a.dupe(u8, cStr(&n.name)),
            .parent = n.parent_index,
            .mesh_index = n.mesh_index,
            .position = n.translation,
            .rotation_euler_deg = quatToEulerDeg(n.rotation),
            .scale = n.scale,
            .light_type = n.light_type,
            .light_color = n.light_color,
            .light_intensity = n.light_intensity,
            .light_inner_deg = n.light_inner_deg,
            .light_outer_deg = n.light_outer_deg,
            .light_cast_shadows = n.light_cast_shadows != 0,
        };
    }

    return .{ .nodes = nodes, .arena = arena };
}

// ── Tests ─────────────────────────────────────────────────────────────────────

const FbxLoader = @import("FbxLoader.zig");

fn writeTmpFbx(tmp: *std.testing.TmpDir, name: []const u8, data: []const u8) ![]u8 {
    try tmp.dir.writeFile(std.testing.io, .{ .sub_path = name, .data = data });
    var buf: [256]u8 = undefined;
    return std.fmt.bufPrint(&buf, ".zig-cache/tmp/{s}/{s}", .{ tmp.sub_path, name });
}

test "loadMeshes cooks a single-mesh FBX in local space" {
    const a = std.testing.allocator;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    const path = try writeTmpFbx(&tmp, "m.fbx", FbxLoader.test_fbx);

    const groups = try loadMeshes(a, path);
    defer {
        for (groups) |*g| {
            var mesh = g.mesh;
            mesh.deinit();
            a.free(g.name);
        }
        a.free(groups);
    }

    try std.testing.expectEqual(@as(usize, 1), groups.len);
    try std.testing.expectEqualStrings("Tri", groups[0].name);
    try std.testing.expectEqual(@as(usize, 3), groups[0].mesh.vertices.len);
    try std.testing.expectEqual(@as(usize, 1), groups[0].mesh.submeshes.len);
    try std.testing.expectApproxEqAbs(@as(f32, 0.01), groups[0].mesh.vertices[1].px, 1e-5);
}

test "loadHierarchy walks parent/child indices and TRS transform" {
    const a = std.testing.allocator;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    const path = try writeTmpFbx(&tmp, "h.fbx", FbxLoader.test_fbx);

    var hier = try loadHierarchy(a, path);
    defer hier.deinit();

    // ufbx's synthetic root, plus the one authored "Tri" model node.
    try std.testing.expectEqual(@as(usize, 2), hier.nodes.len);

    var found_tri = false;
    for (hier.nodes) |n| {
        if (!std.mem.eql(u8, n.name, "Tri")) continue;
        found_tri = true;
        try std.testing.expect(n.parent >= 0);
        try std.testing.expectEqual(@as(i32, 0), n.mesh_index);
    }
    try std.testing.expect(found_tri);
}

// Two Model nodes ("InstanceA"/"InstanceB") both connected to the *same*
// Geometry datablock -- the instance-dedup case: `loadMeshes` must return one
// shared `MeshGroup` (not bake/duplicate the geometry per node instance),
// while `loadHierarchy` still reports both nodes, each pointing at that one
// mesh index.
const test_fbx_shared_mesh =
    \\; FBX 7.3.0 project file
    \\FBXHeaderExtension:  {
    \\  FBXHeaderVersion: 1003
    \\  FBXVersion: 7300
    \\}
    \\GlobalSettings:  {
    \\  Version: 1000
    \\  Properties70:  {
    \\    P: "UpAxis", "int", "Integer", "",1
    \\    P: "UpAxisSign", "int", "Integer", "",1
    \\    P: "FrontAxis", "int", "Integer", "",2
    \\    P: "FrontAxisSign", "int", "Integer", "",1
    \\    P: "CoordAxis", "int", "Integer", "",0
    \\    P: "CoordAxisSign", "int", "Integer", "",1
    \\    P: "OriginalUpAxis", "int", "Integer", "",1
    \\    P: "OriginalUpAxisSign", "int", "Integer", "",1
    \\    P: "UnitScaleFactor", "double", "Number", "",1
    \\  }
    \\}
    \\Objects:  {
    \\  Geometry: 1000000, "Geometry::Tri", "Mesh" {
    \\    Vertices: *9 {
    \\      a: 0,0,0,1,0,0,0,1,0
    \\    }
    \\    PolygonVertexIndex: *3 {
    \\      a: 0,1,-3
    \\    }
    \\    GeometryVersion: 124
    \\    LayerElementNormal: 0 {
    \\      Version: 101
    \\      Name: ""
    \\      MappingInformationType: "ByPolygonVertex"
    \\      ReferenceInformationType: "Direct"
    \\      Normals: *9 {
    \\        a: 0,0,1,0,0,1,0,0,1
    \\      }
    \\    }
    \\    LayerElementUV: 0 {
    \\      Version: 101
    \\      Name: "UVMap"
    \\      MappingInformationType: "ByPolygonVertex"
    \\      ReferenceInformationType: "IndexToDirect"
    \\      UV: *6 {
    \\        a: 0,0,1,0,0,1
    \\      }
    \\      UVIndex: *3 {
    \\        a: 0,1,2
    \\      }
    \\    }
    \\    LayerElementMaterial: 0 {
    \\      Version: 101
    \\      Name: ""
    \\      MappingInformationType: "AllSame"
    \\      ReferenceInformationType: "IndexToDirect"
    \\      Materials: *1 {
    \\        a: 0
    \\      }
    \\    }
    \\    Layer: 0 {
    \\      Version: 100
    \\      LayerElement:  {
    \\        Type: "LayerElementNormal"
    \\        TypedIndex: 0
    \\      }
    \\      LayerElement:  {
    \\        Type: "LayerElementUV"
    \\        TypedIndex: 0
    \\      }
    \\      LayerElement:  {
    \\        Type: "LayerElementMaterial"
    \\        TypedIndex: 0
    \\      }
    \\    }
    \\  }
    \\  Model: 2000000, "Model::InstanceA", "Mesh" {
    \\    Version: 232
    \\    Properties70:  {
    \\      P: "Lcl Translation", "Lcl Translation", "", "A",1,0,0
    \\    }
    \\    Shading: T
    \\    Culling: "CullingOff"
    \\  }
    \\  Model: 2000001, "Model::InstanceB", "Mesh" {
    \\    Version: 232
    \\    Properties70:  {
    \\      P: "Lcl Translation", "Lcl Translation", "", "A",5,0,0
    \\    }
    \\    Shading: T
    \\    Culling: "CullingOff"
    \\  }
    \\  Material: 3000000, "Material::TestMat", "" {
    \\    Version: 102
    \\    ShadingModel: "Phong"
    \\    MultiLayer: 0
    \\    Properties70:  {
    \\      P: "DiffuseColor", "Color", "", "A",0.5,0.25,0.1
    \\      P: "SpecularColor", "Color", "", "A",0,0,0
    \\      P: "ShininessExponent", "Number", "", "A",10
    \\    }
    \\  }
    \\}
    \\Connections:  {
    \\  C: "OO",1000000,2000000
    \\  C: "OO",1000000,2000001
    \\  C: "OO",3000000,2000000
    \\  C: "OO",3000000,2000001
    \\}
    \\
;

test "loadMeshes dedupes a mesh shared by two node instances into one MeshGroup" {
    const a = std.testing.allocator;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    const path = try writeTmpFbx(&tmp, "shared.fbx", test_fbx_shared_mesh);

    const groups = try loadMeshes(a, path);
    defer {
        for (groups) |*g| {
            var mesh = g.mesh;
            mesh.deinit();
            a.free(g.name);
        }
        a.free(groups);
    }
    try std.testing.expectEqual(@as(usize, 1), groups.len);

    var hier = try loadHierarchy(a, path);
    defer hier.deinit();

    var instance_count: usize = 0;
    for (hier.nodes) |n| {
        if (!std.mem.eql(u8, n.name, "InstanceA") and !std.mem.eql(u8, n.name, "InstanceB")) continue;
        instance_count += 1;
        try std.testing.expectEqual(@as(i32, 0), n.mesh_index);
    }
    try std.testing.expectEqual(@as(usize, 2), instance_count);
}
