const std = @import("std");
const Mesh = @import("Mesh.zig").Mesh;
const Vertex = @import("Mesh.zig").Vertex;

/// Load a Wavefront OBJ mesh from a file on disk.
pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Mesh {
    var file = try std.Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = try reader.interface.allocRemainingAlignedSentinel(allocator, .unlimited, .@"1", 0);
    defer allocator.free(content);

    return parseObj(allocator, content);
}

/// Parse an OBJ mesh from an in-memory byte buffer (e.g. asset-package bytes).
pub fn parse(allocator: std.mem.Allocator, src: []const u8) !Mesh {
    return parseObj(allocator, src);
}

fn parseObj(allocator: std.mem.Allocator, src: []const u8) !Mesh {
    var pos_count: usize = 0;
    var norm_count: usize = 0;
    var uv_count: usize = 0;
    var face_count: usize = 0;

    var lines = std.mem.splitScalar(u8, src, '\n');
    while (lines.next()) |line| {
        const t = std.mem.trim(u8, line, " \t\r");
        if (std.mem.startsWith(u8, t, "v ")) pos_count += 1 else if (std.mem.startsWith(u8, t, "vn ")) norm_count += 1 else if (std.mem.startsWith(u8, t, "vt ")) uv_count += 1 else if (std.mem.startsWith(u8, t, "f ")) face_count += 1;
    }

    const positions = try allocator.alloc([3]f32, pos_count);
    defer allocator.free(positions);
    const normals = try allocator.alloc([3]f32, @max(norm_count, 1));
    defer allocator.free(normals);
    const texcoords = try allocator.alloc([2]f32, @max(uv_count, 1));
    defer allocator.free(texcoords);
    normals[0] = .{ 0, 1, 0 };
    texcoords[0] = .{ 0, 0 };

    var pi: usize = 0;
    var ni: usize = 0;
    var ti: usize = 0;
    lines = std.mem.splitScalar(u8, src, '\n');
    while (lines.next()) |line| {
        const t = std.mem.trim(u8, line, " \t\r");
        if (std.mem.startsWith(u8, t, "vn ")) {
            const f = parseFloats3(t[3..]) catch continue;
            normals[ni] = f;
            ni += 1;
        } else if (std.mem.startsWith(u8, t, "vt ")) {
            const f = parseFloats2(t[3..]) catch continue;
            texcoords[ti] = f;
            ti += 1;
        } else if (std.mem.startsWith(u8, t, "v ")) {
            const f = parseFloats3(t[2..]) catch continue;
            positions[pi] = f;
            pi += 1;
        }
    }
    const actual_ni = ni;
    const actual_ti = ti;

    var out_verts: std.ArrayList(Vertex) = .empty;
    var out_idx: std.ArrayList(u32) = .empty;
    errdefer {
        out_verts.deinit(allocator);
        out_idx.deinit(allocator);
    }

    const HASH_CAP: usize = 65536;
    const EMPTY: u32 = 0xFFFFFFFF;
    const HEntry = struct { key: u64, val: u32 };
    var htab = try allocator.alloc(HEntry, HASH_CAP);
    defer allocator.free(htab);
    @memset(htab, .{ .key = ~@as(u64, 0), .val = EMPTY });

    lines = std.mem.splitScalar(u8, src, '\n');
    while (lines.next()) |line| {
        const t = std.mem.trim(u8, line, " \t\r");
        if (!std.mem.startsWith(u8, t, "f ")) continue;

        var face_pos: [8]u32 = undefined;
        var face_uv: [8]u32 = undefined;
        var face_nrm: [8]u32 = undefined;
        var fcount: usize = 0;

        var toks = std.mem.tokenizeAny(u8, t[2..], " \t");
        while (toks.next()) |tok| {
            if (fcount >= 8) break;
            parseFaceVertex(tok, &face_pos[fcount], &face_uv[fcount], &face_nrm[fcount]) catch continue;
            fcount += 1;
        }
        if (fcount < 3) continue;

        var fi: usize = 1;
        while (fi + 1 < fcount) : (fi += 1) {
            inline for (.{ 0, fi, fi + 1 }) |k| {
                const vi = face_pos[k];
                const ui = face_uv[k];
                const ni_idx = face_nrm[k];
                const key = (@as(u64, vi) << 40) | (@as(u64, ui) << 20) | @as(u64, ni_idx);
                const slot = @as(usize, @intCast(key % HASH_CAP));
                var s = slot;
                const out_i: u32 = blk: while (true) {
                    if (htab[s].val == EMPTY) {
                        const nv: u32 = @intCast(out_verts.items.len);
                        const pos = positions[@min(vi, @as(u32, @intCast(positions.len - 1)))];
                        const nrm = if (actual_ni > 0)
                            normals[@min(ni_idx, @as(u32, @intCast(actual_ni - 1)))]
                        else
                            normals[0];
                        const uvc = if (actual_ti > 0)
                            texcoords[@min(ui, @as(u32, @intCast(actual_ti - 1)))]
                        else
                            texcoords[0];
                        try out_verts.append(allocator, .{
                            .px = pos[0],
                            .py = pos[1],
                            .pz = pos[2],
                            .nx = nrm[0],
                            .ny = nrm[1],
                            .nz = nrm[2],
                            .u = uvc[0],
                            .v = uvc[1],
                        });
                        htab[s] = .{ .key = key, .val = nv };
                        break :blk nv;
                    } else if (htab[s].key == key) {
                        break :blk htab[s].val;
                    }
                    s = (s + 1) % HASH_CAP;
                    if (s == slot) {
                        const nv: u32 = @intCast(out_verts.items.len);
                        const pos = positions[@min(vi, @as(u32, @intCast(positions.len - 1)))];
                        const nrm = if (actual_ni > 0)
                            normals[@min(ni_idx, @as(u32, @intCast(actual_ni - 1)))]
                        else
                            normals[0];
                        const uvc = if (actual_ti > 0)
                            texcoords[@min(ui, @as(u32, @intCast(actual_ti - 1)))]
                        else
                            texcoords[0];
                        try out_verts.append(allocator, .{
                            .px = pos[0],
                            .py = pos[1],
                            .pz = pos[2],
                            .nx = nrm[0],
                            .ny = nrm[1],
                            .nz = nrm[2],
                            .u = uvc[0],
                            .v = uvc[1],
                        });
                        break :blk nv;
                    }
                };
                try out_idx.append(allocator, out_i);
            }
        }
    }

    if (actual_ni == 0) computeFaceNormals(out_verts.items, out_idx.items);

    var mesh = Mesh{
        .vertices = try out_verts.toOwnedSlice(allocator),
        .indices = try out_idx.toOwnedSlice(allocator),
        .allocator = allocator,
    };
    mesh.computeBounds();
    return mesh;
}

fn parseFaceVertex(tok: []const u8, out_pos: *u32, out_uv: *u32, out_nrm: *u32) !void {
    out_pos.* = 0;
    out_uv.* = 0;
    out_nrm.* = 0;
    var parts = std.mem.splitScalar(u8, tok, '/');
    const p_str = parts.next() orelse return error.BadFace;
    const vi = std.fmt.parseInt(i32, p_str, 10) catch return error.BadFace;
    out_pos.* = @intCast(@max(0, vi - 1));
    if (parts.next()) |uv_str| {
        if (uv_str.len > 0) {
            const ui = std.fmt.parseInt(i32, uv_str, 10) catch 1;
            out_uv.* = @intCast(@max(0, ui - 1));
        }
    }
    if (parts.next()) |n_str| {
        if (n_str.len > 0) {
            const ni = std.fmt.parseInt(i32, n_str, 10) catch 1;
            out_nrm.* = @intCast(@max(0, ni - 1));
        }
    }
}

fn parseFloats3(s: []const u8) ![3]f32 {
    var r = [3]f32{ 0, 0, 0 };
    var it = std.mem.tokenizeAny(u8, s, " \t");
    for (&r) |*x|
        x.* = std.fmt.parseFloat(f32, it.next() orelse return error.NotEnough) catch return error.BadFloat;
    return r;
}

fn parseFloats2(s: []const u8) ![2]f32 {
    var r = [2]f32{ 0, 0 };
    var it = std.mem.tokenizeAny(u8, s, " \t");
    for (&r) |*x|
        x.* = std.fmt.parseFloat(f32, it.next() orelse return error.NotEnough) catch return error.BadFloat;
    return r;
}

fn computeFaceNormals(verts: []Vertex, idxs: []const u32) void {
    var i: usize = 0;
    while (i + 2 < idxs.len) : (i += 3) {
        const a = idxs[i];
        const b = idxs[i + 1];
        const c = idxs[i + 2];
        if (a >= verts.len or b >= verts.len or c >= verts.len) continue;
        const va = verts[a];
        const vb = verts[b];
        const vc = verts[c];
        const nx = (vb.py - va.py) * (vc.pz - va.pz) - (vb.pz - va.pz) * (vc.py - va.py);
        const ny = (vb.pz - va.pz) * (vc.px - va.px) - (vb.px - va.px) * (vc.pz - va.pz);
        const nz = (vb.px - va.px) * (vc.py - va.py) - (vb.py - va.py) * (vc.px - va.px);
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
