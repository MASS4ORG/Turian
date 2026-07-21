/// glTF 2.0 / GLB loader backed by cgltf (single-header C library).
/// `load` cooks every mesh/primitive in the file into one combined mesh:
/// vertices and indices from all primitives are concatenated, and each
/// primitive becomes a `Submesh` range bound to its glTF material index.
/// `loadMeshes`/`loadHierarchy` (`GltfHierarchy.zig`) instead expose the
/// file's per-mesh grouping and node graph, for hierarchy-preserving import.
const std = @import("std");
const Mesh = @import("Mesh.zig").Mesh;
const Vertex = @import("Mesh.zig").Vertex;
const Submesh = @import("Mesh.zig").Submesh;
const model_info = @import("ModelInfo.zig");
pub const ModelInfo = model_info.ModelInfo;
pub const MaterialInfo = model_info.MaterialInfo;
pub const ImageInfo = model_info.ImageInfo;
pub const TexRef = model_info.TexRef;
pub const AlphaMode = model_info.AlphaMode;

/// Mirrors `CgltfMeshData` in `cgltf_wrap.h`. Shared with `GltfHierarchy.zig`,
/// whose `loadMeshes` groups these by `mesh_index` instead of flattening them.
pub const CgltfMeshData = extern struct {
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

/// Mirrors `CgltfMeshName` in `cgltf_wrap.h`.
pub const CgltfMeshName = extern struct {
    name: [128]u8,
};

/// Mirrors `CgltfMultiMeshData` in `cgltf_wrap.h`.
pub const CgltfMultiMeshData = extern struct {
    primitives: ?[*]CgltfMeshData,
    primitive_count: u32,
    mesh_names: ?[*]CgltfMeshName,
    mesh_count: u32,
};

extern fn cgltf_wrap_load_all(path: [*:0]const u8, out: *CgltfMultiMeshData) c_int;
extern fn cgltf_wrap_free_all(data: *CgltfMultiMeshData) void;

/// Parses `path` and returns its full flattened primitive list (every
/// primitive of every mesh in the file, in mesh-major order). Shared by
/// `load` (flattens everything into one combined `Mesh`) and
/// `GltfHierarchy.loadMeshes` (groups by `mesh_index` instead). Caller must
/// call `freeRawAll`.
pub fn loadRawAll(path: []const u8) !CgltfMultiMeshData {
    var path_buf: [512]u8 = undefined;
    if (path.len >= path_buf.len) return error.PathTooLong;
    @memcpy(path_buf[0..path.len], path);
    path_buf[path.len] = 0;

    var raw: CgltfMultiMeshData = undefined;
    if (cgltf_wrap_load_all(@ptrCast(&path_buf), &raw) != 0)
        return error.GltfLoadFailed;
    return raw;
}

pub fn freeRawAll(raw: *CgltfMultiMeshData) void {
    cgltf_wrap_free_all(raw);
}

/// Assembles a combined `Mesh` from a slice of primitives: vertex/index data
/// concatenated, one `Submesh` per primitive bound to its glTF material index.
/// Allocates via `allocator`; the caller owns the returned `Mesh`.
pub fn assemblePrimitives(allocator: std.mem.Allocator, prims: []const CgltfMeshData) !Mesh {
    var total_v: usize = 0;
    var total_i: usize = 0;
    for (prims) |p| {
        total_v += p.vertex_count;
        total_i += p.index_count;
    }

    var verts = try allocator.alloc(Vertex, total_v);
    errdefer allocator.free(verts);
    var indices = try allocator.alloc(u32, total_i);
    errdefer allocator.free(indices);
    var subs = try allocator.alloc(Submesh, prims.len);
    errdefer allocator.free(subs);

    var vbase: usize = 0;
    var ibase: usize = 0;
    for (prims, 0..) |p, si| {
        const vcount = p.vertex_count;
        const icount = p.index_count;

        for (0..vcount) |i| {
            const pos = p.positions;
            var nx: f32 = 0;
            var ny: f32 = 1;
            var nz: f32 = 0;
            if (p.has_normals != 0) {
                const n = p.normals.?;
                nx = n[i * 3 + 0];
                ny = n[i * 3 + 1];
                nz = n[i * 3 + 2];
            }
            var u: f32 = 0;
            var v: f32 = 0;
            if (p.has_uvs != 0) {
                const uv = p.uvs.?;
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

        const prim_verts = verts[vbase .. vbase + vcount];
        const local_indices = p.indices[0..icount];
        if (p.has_normals == 0) Mesh.computeFlatNormals(prim_verts, local_indices);

        const prim_indices = indices[ibase .. ibase + icount];
        for (0..icount) |i| prim_indices[i] = local_indices[i] + @as(u32, @intCast(vbase));

        subs[si] = .{
            .index_offset = @intCast(ibase),
            .index_count = icount,
            .material_slot = p.material_index,
        };

        vbase += vcount;
        ibase += icount;
    }

    var mesh = Mesh{ .vertices = verts, .indices = indices, .submeshes = subs, .allocator = allocator };
    mesh.computeBounds();
    return mesh;
}

/// Load every mesh/primitive from a glTF 2.0 / GLB file into one combined
/// mesh with a submesh table. Allocates vertex, index, and submesh storage
/// via `allocator`; the caller owns the Mesh.
pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Mesh {
    _ = io;

    var raw = try loadRawAll(path);
    defer freeRawAll(&raw);

    const prims = raw.primitives.?[0..raw.primitive_count];
    return assemblePrimitives(allocator, prims);
}

/// Per-glTF-mesh (not per-primitive) geometry grouping and node-hierarchy
/// import, kept in a separate file to keep this one focused on the
/// whole-file-combined-mesh path.
const gltf_hierarchy = @import("GltfHierarchy.zig");
pub const MeshGroup = gltf_hierarchy.MeshGroup;
pub const loadMeshes = gltf_hierarchy.loadMeshes;
pub const GltfNode = gltf_hierarchy.GltfNode;
pub const GltfHierarchy = gltf_hierarchy.GltfHierarchy;
pub const loadHierarchy = gltf_hierarchy.loadHierarchy;

// ── Material / image extraction ────────────────────────────────────────────────
// A glTF file is one source that can produce many engine assets (materials,
// textures). These types expose its materials and images so an importer can
// generate them. Geometry is loaded separately via `load`.

const CgltfTexRef = extern struct {
    has_texture: c_int,
    image_index: c_int,
    uv_set: c_int,
};

const CgltfMaterial = extern struct {
    name: [128]u8,
    base_color: [4]f32,
    metallic: f32,
    roughness: f32,
    emissive: [3]f32,
    emissive_strength: f32,
    normal_scale: f32,
    occlusion_strength: f32,
    alpha_mode: c_int,
    alpha_cutoff: f32,
    double_sided: c_int,
    albedo: CgltfTexRef,
    metallic_roughness: CgltfTexRef,
    normal: CgltfTexRef,
    emissive_tex: CgltfTexRef,
    occlusion: CgltfTexRef,
};

const CgltfImage = extern struct {
    name: [128]u8,
    uri: [256]u8,
    mime_type: [32]u8,
    data: ?[*]const u8,
    data_size: u32,
};

const CgltfModelData = extern struct {
    materials: ?[*]CgltfMaterial,
    material_count: u32,
    images: ?[*]CgltfImage,
    image_count: u32,
    _data: ?*anyopaque,
};

extern fn cgltf_wrap_load_model(path: [*:0]const u8, out: *CgltfModelData) c_int;
extern fn cgltf_wrap_free_model(out: *CgltfModelData) void;

fn cStr(buf: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, buf, 0) orelse buf.len;
    return buf[0..end];
}

fn texRef(r: CgltfTexRef) TexRef {
    if (r.has_texture == 0 or r.image_index < 0) return .{};
    return .{ .image_index = @intCast(r.image_index), .uv_set = @intCast(@max(r.uv_set, 0)) };
}

/// Extract the materials and images of a `.gltf`/`.glb` file. The returned
/// `ModelInfo` owns its memory (independent of cgltf); call `deinit`.
pub fn loadModelInfo(allocator: std.mem.Allocator, path: []const u8) !ModelInfo {
    var path_buf: [512]u8 = undefined;
    if (path.len >= path_buf.len) return error.PathTooLong;
    @memcpy(path_buf[0..path.len], path);
    path_buf[path.len] = 0;

    var raw: CgltfModelData = undefined;
    if (cgltf_wrap_load_model(@ptrCast(&path_buf), &raw) != 0)
        return error.GltfLoadFailed;
    defer cgltf_wrap_free_model(&raw);

    var arena = std.heap.ArenaAllocator.init(allocator);
    errdefer arena.deinit();
    const a = arena.allocator();

    const mats = try a.alloc(MaterialInfo, raw.material_count);
    if (raw.material_count > 0) {
        const src = raw.materials.?;
        for (0..raw.material_count) |i| {
            const m = src[i];
            mats[i] = .{
                .name = try a.dupe(u8, cStr(&m.name)),
                .base_color = m.base_color,
                .metallic = m.metallic,
                .roughness = m.roughness,
                .emissive = m.emissive,
                .emissive_strength = m.emissive_strength,
                .normal_scale = m.normal_scale,
                .occlusion_strength = m.occlusion_strength,
                .alpha_mode = switch (m.alpha_mode) {
                    1 => .mask,
                    2 => .blend,
                    else => .@"opaque",
                },
                .alpha_cutoff = m.alpha_cutoff,
                .double_sided = m.double_sided != 0,
                .albedo = texRef(m.albedo),
                .metallic_roughness = texRef(m.metallic_roughness),
                .normal = texRef(m.normal),
                .emissive_map = texRef(m.emissive_tex),
                .occlusion = texRef(m.occlusion),
            };
        }
    }

    const imgs = try a.alloc(ImageInfo, raw.image_count);
    if (raw.image_count > 0) {
        const src = raw.images.?;
        for (0..raw.image_count) |i| {
            const im = src[i];
            const bytes: []const u8 = if (im.data) |d| d[0..im.data_size] else &.{};
            imgs[i] = .{
                .name = try a.dupe(u8, cStr(&im.name)),
                .uri = try a.dupe(u8, cStr(&im.uri)),
                .mime_type = try a.dupe(u8, cStr(&im.mime_type)),
                .data = try a.dupe(u8, bytes),
            };
        }
    }

    return .{ .materials = mats, .images = imgs, .arena = arena };
}

// ── Tests ─────────────────────────────────────────────────────────────────────

test "loadModelInfo extracts metallic-roughness material and external image" {
    const gltf =
        \\{
        \\  "asset": {"version": "2.0"},
        \\  "images": [{"uri": "albedo.png"}],
        \\  "samplers": [{}],
        \\  "textures": [{"source": 0, "sampler": 0}],
        \\  "materials": [{
        \\    "name": "TestMat",
        \\    "pbrMetallicRoughness": {
        \\      "baseColorFactor": [0.5, 0.25, 0.1, 1.0],
        \\      "metallicFactor": 0.3,
        \\      "roughnessFactor": 0.7,
        \\      "baseColorTexture": {"index": 0}
        \\    },
        \\    "emissiveFactor": [0.0, 1.0, 0.0],
        \\    "alphaMode": "MASK",
        \\    "alphaCutoff": 0.25
        \\  }]
        \\}
    ;

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.writeFile(std.testing.io, .{ .sub_path = "m.gltf", .data = gltf });

    var path_buf: [256]u8 = undefined;
    const path = try std.fmt.bufPrint(&path_buf, ".zig-cache/tmp/{s}/m.gltf", .{tmp.sub_path});

    var info = try loadModelInfo(std.testing.allocator, path);
    defer info.deinit();

    try std.testing.expectEqual(@as(usize, 1), info.materials.len);
    try std.testing.expectEqual(@as(usize, 1), info.images.len);

    const m = info.materials[0];
    try std.testing.expectEqualStrings("TestMat", m.name);
    try std.testing.expectApproxEqAbs(@as(f32, 0.5), m.base_color[0], 1e-5);
    try std.testing.expectApproxEqAbs(@as(f32, 0.3), m.metallic, 1e-5);
    try std.testing.expectApproxEqAbs(@as(f32, 0.7), m.roughness, 1e-5);
    try std.testing.expectApproxEqAbs(@as(f32, 1.0), m.emissive[1], 1e-5);
    try std.testing.expectEqual(AlphaMode.mask, m.alpha_mode);
    try std.testing.expectApproxEqAbs(@as(f32, 0.25), m.alpha_cutoff, 1e-5);
    try std.testing.expectEqual(@as(?u32, 0), m.albedo.image_index);
    try std.testing.expectEqual(@as(?u32, null), m.normal.image_index);

    const img = info.images[0];
    try std.testing.expectEqualStrings("albedo.png", img.uri);
    try std.testing.expect(!img.isEmbedded());
}
