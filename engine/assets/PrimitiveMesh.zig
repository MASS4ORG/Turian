//! Procedurally-generated built-in meshes (cube, sphere) used by the asset
//! preview system to display materials on a stand-in shape without requiring a
//! model asset. Reserved stable GUIDs let the renderer's GUID→bytes mesh source
//! recognise and serve them exactly like any cooked mesh artifact.
const std = @import("std");
const Mesh = @import("Mesh.zig").Mesh;
const Vertex = @import("Mesh.zig").Vertex;

/// Same UUID namespace as the built-in material presets (…0001xx / …0002xx).
pub const cube_guid = "00000000-0000-4000-8000-000000000200";
pub const sphere_guid = "00000000-0000-4000-8000-000000000201";

/// True if `guid` names one of the built-in primitive meshes.
pub fn isBuiltin(guid: []const u8) bool {
    return std.mem.eql(u8, guid, cube_guid) or std.mem.eql(u8, guid, sphere_guid);
}

/// Canonical cooked-mesh bytes (see `Mesh.encode`) for a built-in primitive GUID,
/// or null if `guid` doesn't name one. Caller owns the returned bytes.
pub fn builtinBytes(allocator: std.mem.Allocator, guid: []const u8) !?[]u8 {
    if (std.mem.eql(u8, guid, cube_guid)) {
        var m = try cube(allocator);
        defer m.deinit();
        return try m.encode(allocator);
    }
    if (std.mem.eql(u8, guid, sphere_guid)) {
        var m = try sphere(allocator);
        defer m.deinit();
        return try m.encode(allocator);
    }
    return null;
}

/// Unit cube (±0.5), 24 vertices (4 per face so each face gets its own normal
/// and UVs), 36 indices. Mirrors the convention used by the example project's
/// `cube.obj`.
pub fn cube(allocator: std.mem.Allocator) !Mesh {
    const faces = [6]struct { n: [3]f32, verts: [4][3]f32 }{
        .{ .n = .{ 0, 0, 1 }, .verts = .{ .{ -0.5, -0.5, 0.5 }, .{ 0.5, -0.5, 0.5 }, .{ 0.5, 0.5, 0.5 }, .{ -0.5, 0.5, 0.5 } } }, // +Z
        .{ .n = .{ 0, 0, -1 }, .verts = .{ .{ 0.5, -0.5, -0.5 }, .{ -0.5, -0.5, -0.5 }, .{ -0.5, 0.5, -0.5 }, .{ 0.5, 0.5, -0.5 } } }, // -Z
        .{ .n = .{ 1, 0, 0 }, .verts = .{ .{ 0.5, -0.5, 0.5 }, .{ 0.5, -0.5, -0.5 }, .{ 0.5, 0.5, -0.5 }, .{ 0.5, 0.5, 0.5 } } }, // +X
        .{ .n = .{ -1, 0, 0 }, .verts = .{ .{ -0.5, -0.5, -0.5 }, .{ -0.5, -0.5, 0.5 }, .{ -0.5, 0.5, 0.5 }, .{ -0.5, 0.5, -0.5 } } }, // -X
        .{ .n = .{ 0, 1, 0 }, .verts = .{ .{ -0.5, 0.5, 0.5 }, .{ 0.5, 0.5, 0.5 }, .{ 0.5, 0.5, -0.5 }, .{ -0.5, 0.5, -0.5 } } }, // +Y
        .{ .n = .{ 0, -1, 0 }, .verts = .{ .{ -0.5, -0.5, -0.5 }, .{ 0.5, -0.5, -0.5 }, .{ 0.5, -0.5, 0.5 }, .{ -0.5, -0.5, 0.5 } } }, // -Y
    };
    const uvs = [4][2]f32{ .{ 0, 0 }, .{ 1, 0 }, .{ 1, 1 }, .{ 0, 1 } };

    var verts = try allocator.alloc(Vertex, 24);
    errdefer allocator.free(verts);
    var idx = try allocator.alloc(u32, 36);
    errdefer allocator.free(idx);

    for (faces, 0..) |f, fi| {
        for (0..4) |vi| {
            const p = f.verts[vi];
            verts[fi * 4 + vi] = .{
                .px = p[0],
                .py = p[1],
                .pz = p[2],
                .nx = f.n[0],
                .ny = f.n[1],
                .nz = f.n[2],
                .u = uvs[vi][0],
                .v = uvs[vi][1],
            };
        }
        const base: u32 = @intCast(fi * 4);
        idx[fi * 6 + 0] = base + 0;
        idx[fi * 6 + 1] = base + 1;
        idx[fi * 6 + 2] = base + 2;
        idx[fi * 6 + 3] = base + 0;
        idx[fi * 6 + 4] = base + 2;
        idx[fi * 6 + 5] = base + 3;
    }

    var mesh = Mesh{ .vertices = verts, .indices = idx, .allocator = allocator };
    mesh.computeBounds();
    return mesh;
}

/// UV sphere, radius 0.5 (matching `cube`'s extent), fixed 24×16 (lon×lat)
/// segmentation — detailed enough for a material preview, small enough to
/// upload instantly.
pub fn sphere(allocator: std.mem.Allocator) !Mesh {
    const lon_segs: u32 = 24;
    const lat_segs: u32 = 16;
    const radius: f32 = 0.5;

    const vcount: usize = (lat_segs + 1) * (lon_segs + 1);
    var verts = try allocator.alloc(Vertex, vcount);
    errdefer allocator.free(verts);

    var vi: usize = 0;
    for (0..lat_segs + 1) |lat| {
        const v = @as(f32, @floatFromInt(lat)) / @as(f32, @floatFromInt(lat_segs));
        const theta = v * std.math.pi; // 0..pi (top to bottom)
        const sin_t = @sin(theta);
        const cos_t = @cos(theta);
        for (0..lon_segs + 1) |lon| {
            const u = @as(f32, @floatFromInt(lon)) / @as(f32, @floatFromInt(lon_segs));
            const phi = u * std.math.pi * 2.0;
            const sin_p = @sin(phi);
            const cos_p = @cos(phi);
            const nx = sin_t * cos_p;
            const ny = cos_t;
            const nz = sin_t * sin_p;
            verts[vi] = .{
                .px = radius * nx,
                .py = radius * ny,
                .pz = radius * nz,
                .nx = nx,
                .ny = ny,
                .nz = nz,
                .u = u,
                .v = v,
            };
            vi += 1;
        }
    }

    const icount: usize = lat_segs * lon_segs * 6;
    var idx = try allocator.alloc(u32, icount);
    errdefer allocator.free(idx);

    var ii: usize = 0;
    const row: u32 = lon_segs + 1;
    for (0..lat_segs) |lat| {
        for (0..lon_segs) |lon| {
            const a: u32 = @as(u32, @intCast(lat)) * row + @as(u32, @intCast(lon));
            const b: u32 = a + row;
            idx[ii + 0] = a;
            idx[ii + 1] = b;
            idx[ii + 2] = a + 1;
            idx[ii + 3] = a + 1;
            idx[ii + 4] = b;
            idx[ii + 5] = b + 1;
            ii += 6;
        }
    }

    var mesh = Mesh{ .vertices = verts, .indices = idx, .allocator = allocator };
    mesh.computeBounds();
    return mesh;
}

test "cube is a well-formed 24-vertex box" {
    var m = try cube(std.testing.allocator);
    defer m.deinit();
    try std.testing.expectEqual(@as(usize, 24), m.vertices.len);
    try std.testing.expectEqual(@as(usize, 36), m.indices.len);
    try std.testing.expectEqual(@as(f32, -0.5), m.min[0]);
    try std.testing.expectEqual(@as(f32, 0.5), m.max[0]);
    for (m.indices) |i| try std.testing.expect(i < m.vertices.len);
}

test "sphere is a well-formed manifold-ish mesh" {
    var m = try sphere(std.testing.allocator);
    defer m.deinit();
    try std.testing.expect(m.vertices.len > 0);
    try std.testing.expectEqual(@as(usize, 0), m.indices.len % 3);
    for (m.indices) |i| try std.testing.expect(i < m.vertices.len);
    // Every vertex should sit on the radius-0.5 sphere.
    for (m.vertices) |v| {
        const r = @sqrt(v.px * v.px + v.py * v.py + v.pz * v.pz);
        try std.testing.expectApproxEqAbs(@as(f32, 0.5), r, 0.001);
    }
}

test "builtinBytes round-trips through the canonical mesh format" {
    const bytes = (try builtinBytes(std.testing.allocator, cube_guid)).?;
    defer std.testing.allocator.free(bytes);
    try std.testing.expect(Mesh.isCanonical(bytes));
    var m = try Mesh.fromBytes(std.testing.allocator, bytes);
    defer m.deinit();
    try std.testing.expectEqual(@as(usize, 24), m.vertices.len);

    try std.testing.expect((try builtinBytes(std.testing.allocator, "not-a-guid")) == null);
}

test "isBuiltin recognizes reserved GUIDs only" {
    try std.testing.expect(isBuiltin(cube_guid));
    try std.testing.expect(isBuiltin(sphere_guid));
    try std.testing.expect(!isBuiltin("00000000-0000-4000-8000-000000000100"));
}
