const std = @import("std");

test "canonical mesh round-trips through encode/fromBytes" {
    const a = std.testing.allocator;
    var verts = [_]Vertex{
        .{ .px = 0, .py = 0, .pz = 0, .nx = 0, .ny = 1, .nz = 0, .u = 0, .v = 0 },
        .{ .px = 1, .py = 0, .pz = 0, .nx = 0, .ny = 1, .nz = 0, .u = 1, .v = 0 },
        .{ .px = 0, .py = 1, .pz = 0, .nx = 0, .ny = 1, .nz = 0, .u = 0, .v = 1 },
    };
    var idx = [_]u32{ 0, 1, 2 };
    const src = Mesh{ .vertices = &verts, .indices = &idx, .allocator = a };

    const bytes = try src.encode(a);
    defer a.free(bytes);
    try std.testing.expect(Mesh.isCanonical(bytes));

    var mesh = try Mesh.fromBytes(a, bytes);
    defer mesh.deinit();
    try std.testing.expectEqual(@as(usize, 3), mesh.vertices.len);
    try std.testing.expectEqual(@as(usize, 3), mesh.indices.len);
    try std.testing.expectEqual(@as(f32, 1), mesh.vertices[1].px);
    try std.testing.expectEqual(@as(u32, 2), mesh.indices[2]);
    // Bounds recomputed on load.
    try std.testing.expectEqual(@as(f32, 1), mesh.max[0]);
}

test "multi-submesh mesh round-trips through encode/fromBytes" {
    const a = std.testing.allocator;
    var verts = [_]Vertex{
        .{ .px = 0, .py = 0, .pz = 0, .nx = 0, .ny = 1, .nz = 0, .u = 0, .v = 0 },
        .{ .px = 1, .py = 0, .pz = 0, .nx = 0, .ny = 1, .nz = 0, .u = 1, .v = 0 },
        .{ .px = 0, .py = 1, .pz = 0, .nx = 0, .ny = 1, .nz = 0, .u = 0, .v = 1 },
        .{ .px = 2, .py = 0, .pz = 0, .nx = 0, .ny = 1, .nz = 0, .u = 0, .v = 0 },
        .{ .px = 3, .py = 0, .pz = 0, .nx = 0, .ny = 1, .nz = 0, .u = 1, .v = 0 },
        .{ .px = 2, .py = 1, .pz = 0, .nx = 0, .ny = 1, .nz = 0, .u = 0, .v = 1 },
    };
    var idx = [_]u32{ 0, 1, 2, 3, 4, 5 };
    var subs = [_]Submesh{
        .{ .index_offset = 0, .index_count = 3, .material_slot = 0 },
        .{ .index_offset = 3, .index_count = 3, .material_slot = 1 },
    };
    const src = Mesh{ .vertices = &verts, .indices = &idx, .submeshes = &subs, .allocator = a };

    const bytes = try src.encode(a);
    defer a.free(bytes);

    var mesh = try Mesh.fromBytes(a, bytes);
    defer mesh.deinit();
    try std.testing.expectEqual(@as(usize, 2), mesh.submeshes.len);
    try std.testing.expectEqual(@as(u32, 3), mesh.submeshes[1].index_offset);
    try std.testing.expectEqual(@as(i32, 1), mesh.submeshes[1].material_slot);
    try std.testing.expectEqual(@as(f32, 3), mesh.max[0]);
}

/// A single vertex with position, normal, and UV data.
pub const Vertex = extern struct {
    px: f32,
    py: f32,
    pz: f32,
    nx: f32,
    ny: f32,
    nz: f32,
    u: f32,
    v: f32,
};

/// One drawable range of a mesh's shared index buffer, bound to a material.
/// `material_slot` is the source-format material index for this range (e.g.
/// the glTF material index), or -1 when the primitive has no material.
pub const Submesh = extern struct {
    index_offset: u32,
    index_count: u32,
    material_slot: i32,
};

/// Mesh loaded from OBJ/GLTF. Owned by the caller; call deinit() to free.
pub const Mesh = struct {
    /// Vertex array.
    vertices: []Vertex,
    /// Triangle index array.
    indices: []u32,
    /// Allocator used for vertices, indices, and submeshes.
    allocator: std.mem.Allocator,

    /// Draw ranges into `indices`, each bound to a material slot. Empty means
    /// the whole index buffer is one implicit submesh with no material of its
    /// own (procedural primitives, OBJ) — callers should treat that the same
    /// as a single submesh spanning `indices`.
    submeshes: []Submesh = &.{},

    /// Axis-aligned bounding box minimum corner.
    min: [3]f32 = .{ 0, 0, 0 },
    /// Axis-aligned bounding box maximum corner.
    max: [3]f32 = .{ 0, 0, 0 },

    /// Frees the vertex, index, and submesh arrays.
    pub fn deinit(self: *@This()) void {
        self.allocator.free(self.vertices);
        self.allocator.free(self.indices);
        self.allocator.free(self.submeshes);
    }

    /// Magic identifying the canonical cooked-mesh format written below.
    pub const magic = "TMSH";
    /// Canonical mesh format version. Bump when the layout changes.
    pub const format_version: u32 = 2;

    /// True if `bytes` is a canonical cooked mesh (vs. a source .obj/.gltf).
    pub fn isCanonical(bytes: []const u8) bool {
        return bytes.len >= magic.len and std.mem.eql(u8, bytes[0..magic.len], magic);
    }

    /// Serialize to the canonical binary mesh format: a small header followed by
    /// the raw vertex, index, and submesh arrays. This is what the importer cooks
    /// every model into, so the runtime loads geometry with one fast,
    /// format-agnostic path (no OBJ/glTF parsing at runtime). Caller owns the
    /// returned bytes.
    pub fn encode(self: @This(), allocator: std.mem.Allocator) ![]u8 {
        const header = 20; // magic(4) + version(4) + vcount(4) + icount(4) + subcount(4)
        const vbytes = self.vertices.len * @sizeOf(Vertex);
        const ibytes = self.indices.len * @sizeOf(u32);
        const sbytes = self.submeshes.len * @sizeOf(Submesh);
        const out = try allocator.alloc(u8, header + vbytes + ibytes + sbytes);
        @memcpy(out[0..4], magic);
        std.mem.writeInt(u32, out[4..8], format_version, .little);
        std.mem.writeInt(u32, out[8..12], @intCast(self.vertices.len), .little);
        std.mem.writeInt(u32, out[12..16], @intCast(self.indices.len), .little);
        std.mem.writeInt(u32, out[16..20], @intCast(self.submeshes.len), .little);
        @memcpy(out[header .. header + vbytes], std.mem.sliceAsBytes(self.vertices));
        @memcpy(out[header + vbytes .. header + vbytes + ibytes], std.mem.sliceAsBytes(self.indices));
        @memcpy(out[header + vbytes + ibytes ..], std.mem.sliceAsBytes(self.submeshes));
        return out;
    }

    /// Parse the canonical binary mesh format produced by `encode`. The returned
    /// mesh owns its arrays; free with `deinit`.
    pub fn fromBytes(allocator: std.mem.Allocator, bytes: []const u8) !Mesh {
        if (!isCanonical(bytes) or bytes.len < 20) return error.NotCanonicalMesh;
        const vcount = std.mem.readInt(u32, bytes[8..12], .little);
        const icount = std.mem.readInt(u32, bytes[12..16], .little);
        const subcount = std.mem.readInt(u32, bytes[16..20], .little);
        const vbytes = @as(usize, vcount) * @sizeOf(Vertex);
        const ibytes = @as(usize, icount) * @sizeOf(u32);
        const sbytes = @as(usize, subcount) * @sizeOf(Submesh);
        if (bytes.len < 20 + vbytes + ibytes + sbytes) return error.CorruptMesh;

        const verts = try allocator.alloc(Vertex, vcount);
        errdefer allocator.free(verts);
        @memcpy(std.mem.sliceAsBytes(verts), bytes[20 .. 20 + vbytes]);

        const idx = try allocator.alloc(u32, icount);
        errdefer allocator.free(idx);
        @memcpy(std.mem.sliceAsBytes(idx), bytes[20 + vbytes .. 20 + vbytes + ibytes]);

        const subs = try allocator.alloc(Submesh, subcount);
        errdefer allocator.free(subs);
        @memcpy(std.mem.sliceAsBytes(subs), bytes[20 + vbytes + ibytes .. 20 + vbytes + ibytes + sbytes]);

        var mesh = Mesh{ .vertices = verts, .indices = idx, .submeshes = subs, .allocator = allocator };
        mesh.computeBounds();
        return mesh;
    }

    /// Computes the axis-aligned bounding box from vertex positions.
    pub fn computeBounds(self: *@This()) void {
        if (self.vertices.len == 0) return;
        var mn = [3]f32{ self.vertices[0].px, self.vertices[0].py, self.vertices[0].pz };
        var mx = mn;
        for (self.vertices[1..]) |v| {
            mn[0] = @min(mn[0], v.px);
            mn[1] = @min(mn[1], v.py);
            mn[2] = @min(mn[2], v.pz);
            mx[0] = @max(mx[0], v.px);
            mx[1] = @max(mx[1], v.py);
            mx[2] = @max(mx[2], v.pz);
        }
        self.min = mn;
        self.max = mx;
    }
};
