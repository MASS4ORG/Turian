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
    material_index: c_int,
};

extern fn cgltf_wrap_load(path: [*:0]const u8, out: *CgltfMeshData) c_int;
extern fn cgltf_wrap_free(data: *CgltfMeshData) void;

/// Load the first mesh primitive from a glTF 2.0 / GLB file.
/// Allocates vertex and index storage via `allocator`; the caller owns the Mesh.
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

/// Index of the material the first mesh/primitive references, or null if the
/// model assigns no material. Used by the importer to bind an imported mesh to
/// its generated material.
pub fn firstMaterialIndex(path: []const u8) ?u32 {
    var path_buf: [512]u8 = undefined;
    if (path.len >= path_buf.len) return null;
    @memcpy(path_buf[0..path.len], path);
    path_buf[path.len] = 0;

    var raw: CgltfMeshData = undefined;
    if (cgltf_wrap_load(@ptrCast(&path_buf), &raw) != 0) return null;
    defer cgltf_wrap_free(&raw);
    if (raw.material_index < 0) return null;
    return @intCast(raw.material_index);
}

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

/// glTF alpha rendering mode (matches the metallic-roughness model).
pub const AlphaMode = enum { @"opaque", mask, blend };

/// A reference from a material slot to one of the model's images.
pub const TexRef = struct {
    /// Index into `ModelInfo.images`, or null when this slot binds no texture.
    image_index: ?u32 = null,
    /// Texcoord set (TEXCOORD_n) the slot samples; usually 0.
    uv_set: u32 = 0,
};

/// A glTF material flattened to the metallic-roughness model. Strings/refs are
/// owned by the parent `ModelInfo`.
pub const MaterialInfo = struct {
    name: []const u8,
    base_color: [4]f32,
    metallic: f32,
    roughness: f32,
    emissive: [3]f32,
    emissive_strength: f32,
    normal_scale: f32,
    occlusion_strength: f32,
    alpha_mode: AlphaMode,
    alpha_cutoff: f32,
    double_sided: bool,
    albedo: TexRef,
    metallic_roughness: TexRef,
    normal: TexRef,
    /// Emissive texture (distinct from the `emissive` colour factor above).
    emissive_map: TexRef,
    occlusion: TexRef,
};

/// A glTF image: either an external file (`uri` non-empty) or embedded bytes
/// (`data` non-empty). Owned by the parent `ModelInfo`.
pub const ImageInfo = struct {
    name: []const u8,
    /// External relative path; empty when the image is embedded.
    uri: []const u8,
    /// MIME type (e.g. "image/png"); set for embedded images.
    mime_type: []const u8,
    /// Embedded image bytes; empty when the image is external.
    data: []const u8,

    /// True when the image is embedded (carries its own bytes).
    pub fn isEmbedded(self: ImageInfo) bool {
        return self.data.len > 0;
    }
};

/// A glTF model's materials and images. All strings and byte buffers are owned
/// by `arena`; call `deinit` to release them.
pub const ModelInfo = struct {
    materials: []MaterialInfo,
    images: []ImageInfo,
    arena: std.heap.ArenaAllocator,

    pub fn deinit(self: *ModelInfo) void {
        self.arena.deinit();
    }
};

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
