//! Local-space bounding boxes for cooked meshes, cached by GUID.
//! Click-to-select ray-tests against these so picking matches the
//! actual mesh extent instead of assuming every mesh is a unit cube.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("../../services/EditorState.zig");

const Vector3 = engine.Vector3;
const page = std.heap.page_allocator;

pub const Bounds = struct { min: Vector3, max: Vector3 };

const Entry = struct {
    key: [40]u8 = undefined,
    key_len: usize = 0,
    bounds: Bounds,
};
var cache: [128]Entry = undefined;
var count: usize = 0;

fn lookup(guid: []const u8) ?Bounds {
    for (cache[0..count]) |*e|
        if (std.mem.eql(u8, e.key[0..e.key_len], guid)) return e.bounds;
    return null;
}

/// The local-space AABB of the cooked mesh with this GUID, or null if it cannot
/// be resolved/loaded yet. Successful results are cached for the session.
pub fn local(guid: []const u8) ?Bounds {
    if (guid.len == 0 or guid.len > 40) return null;
    if (lookup(guid)) |b| return b;

    const b = compute(guid) orelse return null;
    if (count < cache.len) {
        var e = &cache[count];
        @memcpy(e.key[0..guid.len], guid);
        e.key_len = guid.len;
        e.bounds = b;
        count += 1;
    }
    return b;
}

fn compute(guid: []const u8) ?Bounds {
    const proj = EditorState.project_path orelse return null;
    const g = editor.Guid.parse(guid) catch return null;
    var buf: [1024]u8 = undefined;
    const path = editor.asset_cache.artifactPath(proj, g, .model, &buf) orelse return null;
    const bytes = std.Io.Dir.cwd().readFileAlloc(gui.io, path, page, .unlimited) catch return null;
    defer page.free(bytes);

    var mesh = engine.assets.loadMeshFromMemory(page, bytes, "") catch return null;
    defer mesh.deinit();
    if (mesh.vertices.len == 0) return null;

    var mn = Vector3{ .x = 1e30, .y = 1e30, .z = 1e30 };
    var mx = Vector3{ .x = -1e30, .y = -1e30, .z = -1e30 };
    for (mesh.vertices) |v| {
        mn = .{ .x = @min(mn.x, v.px), .y = @min(mn.y, v.py), .z = @min(mn.z, v.pz) };
        mx = .{ .x = @max(mx.x, v.px), .y = @max(mx.y, v.py), .z = @max(mx.z, v.pz) };
    }
    return .{ .min = mn, .max = mx };
}

/// Drop all cached bounds (e.g. after a project switch or reimport).
pub fn clear() void {
    count = 0;
}
