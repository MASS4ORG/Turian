/// FBX (binary + ASCII) loader backed by ufbx (single-file C library).
/// Cooks every mesh instance in the file into one combined mesh: each node's
/// geometry is triangulated and baked into world space via its transform, and
/// each (node, material slot) chunk becomes a `Submesh` range bound to its
/// FBX material index — mirrors `GltfLoader`'s per-primitive submesh cooking.
const std = @import("std");
const Mesh = @import("Mesh.zig").Mesh;
const Vertex = @import("Mesh.zig").Vertex;
const Submesh = @import("Mesh.zig").Submesh;
const model_info = @import("ModelInfo.zig");
const ModelInfo = model_info.ModelInfo;
const MaterialInfo = model_info.MaterialInfo;
const ImageInfo = model_info.ImageInfo;
const TexRef = model_info.TexRef;
const AlphaMode = model_info.AlphaMode;

const FbxMeshData = extern struct {
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

const FbxMultiMeshData = extern struct {
    primitives: ?[*]FbxMeshData,
    primitive_count: u32,
};

extern fn fbx_wrap_load_all(path: [*:0]const u8, out: *FbxMultiMeshData) c_int;
extern fn fbx_wrap_free_all(data: *FbxMultiMeshData) void;

/// Load every mesh instance from an FBX (binary or ASCII) file into one
/// combined mesh with a submesh table. Allocates vertex, index, and submesh
/// storage via `allocator`; the caller owns the Mesh.
pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Mesh {
    _ = io;

    var path_buf: [512]u8 = undefined;
    if (path.len >= path_buf.len) return error.PathTooLong;
    @memcpy(path_buf[0..path.len], path);
    path_buf[path.len] = 0;

    var raw: FbxMultiMeshData = undefined;
    if (fbx_wrap_load_all(@ptrCast(&path_buf), &raw) != 0)
        return error.FbxLoadFailed;
    defer fbx_wrap_free_all(&raw);

    const chunks = raw.primitives.?[0..raw.primitive_count];

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

// ── Material / image extraction ────────────────────────────────────────────────
// An FBX file is one source that can produce many engine assets (materials,
// textures). These map onto the same `ModelInfo` shape `GltfLoader` uses so
// `AssetImporter` doesn't need to special-case the source format. Geometry is
// loaded separately via `load`.

const FbxTexRef = extern struct {
    has_texture: c_int,
    image_index: c_int,
    uv_set: c_int,
};

const FbxMaterial = extern struct {
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
    albedo: FbxTexRef,
    metallic_roughness: FbxTexRef,
    normal: FbxTexRef,
    emissive_tex: FbxTexRef,
    occlusion: FbxTexRef,
};

const FbxImage = extern struct {
    name: [128]u8,
    uri: [256]u8,
    mime_type: [32]u8,
    data: ?[*]const u8,
    data_size: u32,
};

const FbxModelData = extern struct {
    materials: ?[*]FbxMaterial,
    material_count: u32,
    images: ?[*]FbxImage,
    image_count: u32,
    _scene: ?*anyopaque,
};

extern fn fbx_wrap_load_model(path: [*:0]const u8, out: *FbxModelData) c_int;
extern fn fbx_wrap_free_model(out: *FbxModelData) void;

fn cStr(buf: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, buf, 0) orelse buf.len;
    return buf[0..end];
}

fn texRef(r: FbxTexRef) TexRef {
    if (r.has_texture == 0 or r.image_index < 0) return .{};
    return .{ .image_index = @intCast(r.image_index), .uv_set = @intCast(@max(r.uv_set, 0)) };
}

/// Extract the materials and textures of an FBX file. The returned
/// `ModelInfo` owns its memory (independent of ufbx); call `deinit`.
pub fn loadModelInfo(allocator: std.mem.Allocator, path: []const u8) !ModelInfo {
    var path_buf: [512]u8 = undefined;
    if (path.len >= path_buf.len) return error.PathTooLong;
    @memcpy(path_buf[0..path.len], path);
    path_buf[path.len] = 0;

    var raw: FbxModelData = undefined;
    if (fbx_wrap_load_model(@ptrCast(&path_buf), &raw) != 0)
        return error.FbxLoadFailed;
    defer fbx_wrap_free_model(&raw);

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
// A minimal hand-authored ASCII FBX: one triangle (positions in the FBX default
// of centimeters, so `load`'s baked axis/unit conversion halves... scales them
// by 0.01 into the engine's meters convention) with a classic Phong material
// (no explicit PBR roughness/metalness — ufbx approximates them from
// ShininessExponent, exercising the best-effort Phong→PBR mapping).
pub const test_fbx =
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
    \\  Model: 2000000, "Model::Tri", "Mesh" {
    \\    Version: 232
    \\    Properties70:  {
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
    \\  C: "OO",3000000,2000000
    \\}
    \\
;

test "load triangulates a single-triangle FBX into one submesh" {
    const io = std.testing.io;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.writeFile(io, .{ .sub_path = "m.fbx", .data = test_fbx });

    var path_buf: [256]u8 = undefined;
    const path = try std.fmt.bufPrint(&path_buf, ".zig-cache/tmp/{s}/m.fbx", .{tmp.sub_path});

    var mesh = try load(std.testing.allocator, io, path);
    defer mesh.deinit();

    try std.testing.expectEqual(@as(usize, 3), mesh.vertices.len);
    try std.testing.expectEqual(@as(usize, 3), mesh.indices.len);
    try std.testing.expectEqual(@as(usize, 1), mesh.submeshes.len);
    try std.testing.expectEqual(@as(i32, 0), mesh.submeshes[0].material_slot);

    // FBX default unit is centimeters; `load` bakes conversion to meters.
    try std.testing.expectApproxEqAbs(@as(f32, 0.01), mesh.vertices[1].px, 1e-5);
    try std.testing.expectApproxEqAbs(@as(f32, 1.0), mesh.vertices[0].nz, 1e-5);
}

test "loadModelInfo extracts a best-effort PBR material from a classic Phong FBX material" {
    const io = std.testing.io;
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.writeFile(io, .{ .sub_path = "m.fbx", .data = test_fbx });

    var path_buf: [256]u8 = undefined;
    const path = try std.fmt.bufPrint(&path_buf, ".zig-cache/tmp/{s}/m.fbx", .{tmp.sub_path});

    var info = try loadModelInfo(std.testing.allocator, path);
    defer info.deinit();

    try std.testing.expectEqual(@as(usize, 1), info.materials.len);
    const m = info.materials[0];
    try std.testing.expectEqualStrings("TestMat", m.name);
    try std.testing.expectApproxEqAbs(@as(f32, 0.5), m.base_color[0], 1e-5);
    try std.testing.expectApproxEqAbs(@as(f32, 0.25), m.base_color[1], 1e-5);
    try std.testing.expectApproxEqAbs(@as(f32, 0.1), m.base_color[2], 1e-5);
}
