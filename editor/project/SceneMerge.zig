//! Three-way semantic merge for scene/prefab JSON, keyed by per-object GUID
//! instead of array position.

const std = @import("std");
const serde = @import("serde");
const engine = @import("engine");
const SceneFile = @import("../types/SceneFile.zig").SceneFile;
const SceneObject = @import("../types/SceneObject.zig").SceneObject;
const CURRENT_VERSION = @import("../types/SceneFile.zig").CURRENT_VERSION;

/// A human-readable description of a field that could not be auto-merged.
pub const Conflict = struct {
    /// Object display name at the time of conflict (may be stale if the name
    /// itself is the conflicting field).
    name: []const u8,
    guid: []const u8,
    detail: []const u8,
};

pub const MergeOutcome = struct {
    /// Best-effort merged scene, always valid JSON — even when `conflicts`
    /// is non-empty (git still needs file content to show the user).
    json: []u8,
    conflicts: []Conflict,

    pub fn deinit(self: *MergeOutcome, allocator: std.mem.Allocator) void {
        allocator.free(self.json);
        for (self.conflicts) |c| {
            allocator.free(c.name);
            allocator.free(c.guid);
            allocator.free(c.detail);
        }
        allocator.free(self.conflicts);
    }
};

/// One side's view of an object, with `parent` resolved from an array index
/// (meaningful only within its own file) to a GUID (stable across files).
const View = struct {
    obj: SceneObject,
    parent_guid: []const u8,
    components_json: []const u8,
    linkage_json: []const u8,
};

const SideMap = std.StringHashMap(View);

fn buildSideMap(arena: std.mem.Allocator, bytes: []const u8) !SideMap {
    var map = SideMap.init(arena);
    const parsed = serde.json.fromSlice(SceneFile, arena, bytes) catch return map;
    for (parsed.objects) |scene_obj| {
        const parent_guid: []const u8 = blk: {
            if (scene_obj.parent < 0) break :blk "";
            const pidx: usize = @intCast(scene_obj.parent);
            if (pidx >= parsed.objects.len) break :blk "";
            break :blk parsed.objects[pidx].guid;
        };
        const components_json = try serde.json.toSliceWith(arena, scene_obj.components, .{});
        const linkage_json = try std.fmt.allocPrint(arena, "{s}\x00{s}\x00{s}", .{
            scene_obj.prefab_source,
            scene_obj.prefab_node,
            (try serde.json.toSliceWith(arena, scene_obj.overrides, .{})),
        });
        if (scene_obj.guid.len == 0) continue; // un-GUID'd objects can't be merged by identity
        try map.put(scene_obj.guid, .{ .obj = scene_obj, .parent_guid = parent_guid, .components_json = components_json, .linkage_json = linkage_json });
    }
    return map;
}

/// Ordered union of every GUID seen across the three sides, base-first so
/// merge order stays stable across runs.
fn collectGuidOrder(arena: std.mem.Allocator, base: *const SideMap, ours: *const SideMap, theirs: *const SideMap) ![][]const u8 {
    var seen = std.StringHashMap(void).init(arena);
    var order: std.ArrayList([]const u8) = .empty;
    inline for (.{ base, ours, theirs }) |side| {
        var it = side.iterator();
        while (it.next()) |entry| {
            if (seen.contains(entry.key_ptr.*)) continue;
            try seen.put(entry.key_ptr.*, {});
            try order.append(arena, entry.key_ptr.*);
        }
    }
    return order.toOwnedSlice(arena);
}

/// Three-way pick for a single field: unchanged/one-sided changes resolve
/// silently; both-sides-different-from-base is a conflict (falls back to `ours`).
fn pick(comptime T: type, base: ?T, ours: T, theirs: T, eq: fn (T, T) bool) struct { value: T, conflict: bool } {
    const b = base orelse ours;
    const ours_changed = base == null or !eq(b, ours);
    const theirs_changed = base == null or !eq(b, theirs);
    if (!ours_changed and !theirs_changed) return .{ .value = b, .conflict = false };
    if (!ours_changed) return .{ .value = theirs, .conflict = false };
    if (!theirs_changed) return .{ .value = ours, .conflict = false };
    if (eq(ours, theirs)) return .{ .value = ours, .conflict = false };
    return .{ .value = ours, .conflict = true };
}

fn eqStr(a: []const u8, b: []const u8) bool {
    return std.mem.eql(u8, a, b);
}
fn eqBool(a: bool, b: bool) bool {
    return a == b;
}
fn eqTransform(a: engine.Transform, b: engine.Transform) bool {
    return std.meta.eql(a, b);
}

const Merged = struct {
    obj: SceneObject,
    parent_guid: []const u8,
};

/// Merge `base`/`ours`/`theirs` scene JSON. All three bear the "merge base
/// / ours / theirs" convention Git uses for 3-way merge drivers.
pub fn merge(allocator: std.mem.Allocator, base_bytes: []const u8, ours_bytes: []const u8, theirs_bytes: []const u8) !MergeOutcome {
    var arena_state = std.heap.ArenaAllocator.init(allocator);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const base = try buildSideMap(arena, base_bytes);
    const ours = try buildSideMap(arena, ours_bytes);
    const theirs = try buildSideMap(arena, theirs_bytes);

    const guid_order = try collectGuidOrder(arena, &base, &ours, &theirs);

    var conflicts: std.ArrayList(Conflict) = .empty;
    var merged: std.ArrayList(Merged) = .empty;
    var survivors = std.StringHashMap(void).init(arena);

    for (guid_order) |guid| {
        const b = base.get(guid);
        const o = ours.get(guid);
        const t = theirs.get(guid);

        if (o == null and t == null) continue; // deleted on both sides
        if (b != null and o == null and t != null) {
            if (namesEqualView(t.?, b.?)) continue; // ours deleted, theirs untouched
            try conflicts.append(arena, .{ .name = t.?.obj.name, .guid = guid, .detail = "deleted in ours, modified in theirs — kept theirs' version" });
            try survivors.put(guid, {});
            try merged.append(arena, .{ .obj = t.?.obj, .parent_guid = t.?.parent_guid });
            continue;
        }
        if (b != null and t == null and o != null) {
            if (namesEqualView(o.?, b.?)) continue; // theirs deleted, ours untouched
            try conflicts.append(arena, .{ .name = o.?.obj.name, .guid = guid, .detail = "deleted in theirs, modified in ours — kept ours' version" });
            try survivors.put(guid, {});
            try merged.append(arena, .{ .obj = o.?.obj, .parent_guid = o.?.parent_guid });
            continue;
        }
        if (b == null and o != null and t == null) {
            try survivors.put(guid, {});
            try merged.append(arena, .{ .obj = o.?.obj, .parent_guid = o.?.parent_guid });
            continue;
        }
        if (b == null and t != null and o == null) {
            try survivors.put(guid, {});
            try merged.append(arena, .{ .obj = t.?.obj, .parent_guid = t.?.parent_guid });
            continue;
        }
        if (b == null and o != null and t != null) {
            if (std.mem.eql(u8, o.?.components_json, t.?.components_json) and namesEqualView(o.?, t.?)) {
                try survivors.put(guid, {});
                try merged.append(arena, .{ .obj = o.?.obj, .parent_guid = o.?.parent_guid });
                continue;
            }
            try conflicts.append(arena, .{ .name = o.?.obj.name, .guid = guid, .detail = "added independently on both sides with different content — kept ours' version" });
            try survivors.put(guid, {});
            try merged.append(arena, .{ .obj = o.?.obj, .parent_guid = o.?.parent_guid });
            continue;
        }

        // Present on all three sides: merge field-by-field.
        const bv = b.?;
        const ov = o.?;
        const tv = t.?;

        const name_r = pick([]const u8, bv.obj.name, ov.obj.name, tv.obj.name, eqStr);
        const active_r = pick(bool, bv.obj.active, ov.obj.active, tv.obj.active, eqBool);
        const transform_r = pick(engine.Transform, bv.obj.transform, ov.obj.transform, tv.obj.transform, eqTransform);
        const parent_r = pick([]const u8, bv.parent_guid, ov.parent_guid, tv.parent_guid, eqStr);
        const comp_r = pick([]const u8, bv.components_json, ov.components_json, tv.components_json, eqStr);
        const link_r = pick([]const u8, bv.linkage_json, ov.linkage_json, tv.linkage_json, eqStr);

        inline for (.{
            .{ name_r, "name" },
            .{ active_r, "active" },
            .{ transform_r, "transform" },
            .{ parent_r, "parent" },
            .{ comp_r, "components" },
            .{ link_r, "prefab linkage" },
        }) |pair| {
            if (pair[0].conflict) {
                const detail = try std.fmt.allocPrint(arena, "{s} changed in both branches", .{pair[1]});
                try conflicts.append(arena, .{ .name = name_r.value, .guid = guid, .detail = detail });
            }
        }

        // Pick whichever side's raw object matches the winning components/linkage
        // JSON so we keep the real (typed) component/override slices, not the
        // canonicalized strings used only for comparison.
        const comp_src = if (std.mem.eql(u8, comp_r.value, ov.components_json)) ov.obj else tv.obj;
        const link_src = if (std.mem.eql(u8, link_r.value, ov.linkage_json)) ov.obj else tv.obj;

        var final_obj = SceneObject{
            .name = name_r.value,
            .guid = guid,
            .active = active_r.value,
            .transform = transform_r.value,
            .components = comp_src.components,
            .prefab_source = link_src.prefab_source,
            .prefab_node = link_src.prefab_node,
            .overrides = link_src.overrides,
        };
        _ = &final_obj;

        try survivors.put(guid, {});
        try merged.append(arena, .{ .obj = final_obj, .parent_guid = parent_r.value });
    }

    // Reparent orphans (parent guid didn't survive) to root, flagging a conflict.
    for (merged.items) |*m| {
        if (m.parent_guid.len == 0) continue;
        if (survivors.contains(m.parent_guid)) continue;
        const detail = try std.fmt.allocPrint(arena, "parent object was deleted — reattached to scene root", .{});
        try conflicts.append(arena, .{ .name = m.obj.name, .guid = m.obj.guid, .detail = detail });
        m.parent_guid = "";
    }

    const ordered = try topoSort(arena, merged.items);
    const final_objects = try arena.alloc(SceneObject, ordered.len);
    var index_of = std.StringHashMap(i32).init(arena);
    for (ordered, 0..) |m, i| try index_of.put(m.obj.guid, @intCast(i));
    for (ordered, 0..) |m, i| {
        var final_obj = m.obj;
        final_obj.parent = if (m.parent_guid.len == 0) -1 else (index_of.get(m.parent_guid) orelse -1);
        final_objects[i] = final_obj;
    }

    const version = @max(sceneVersion(base_bytes), @max(sceneVersion(ours_bytes), sceneVersion(theirs_bytes)));
    const scene_file = SceneFile{ .version = @max(version, CURRENT_VERSION), .objects = final_objects };
    const json = try serde.json.toSliceWith(allocator, scene_file, .{ .pretty = true });

    const out_conflicts = try allocator.alloc(Conflict, conflicts.items.len);
    for (conflicts.items, 0..) |c, i| {
        out_conflicts[i] = .{
            .name = try allocator.dupe(u8, c.name),
            .guid = try allocator.dupe(u8, c.guid),
            .detail = try allocator.dupe(u8, c.detail),
        };
    }

    return .{ .json = json, .conflicts = out_conflicts };
}

fn namesEqualView(a: View, b: View) bool {
    return std.mem.eql(u8, a.components_json, b.components_json) and
        std.mem.eql(u8, a.linkage_json, b.linkage_json) and
        eqStr(a.obj.name, b.obj.name) and
        eqBool(a.obj.active, b.obj.active) and
        eqTransform(a.obj.transform, b.obj.transform) and
        eqStr(a.parent_guid, b.parent_guid);
}

fn sceneVersion(bytes: []const u8) u32 {
    const key = "\"version\":";
    const at = std.mem.indexOf(u8, bytes, key) orelse return 1;
    var i = at + key.len;
    while (i < bytes.len and (bytes[i] == ' ' or bytes[i] == '\t')) i += 1;
    var end = i;
    while (end < bytes.len and bytes[end] >= '0' and bytes[end] <= '9') end += 1;
    return std.fmt.parseInt(u32, bytes[i..end], 10) catch 1;
}

/// Stable topological sort (parents before children) over guid-parented
/// objects, breaking any cycle by detaching the closing edge to root.
fn topoSort(arena: std.mem.Allocator, items: []const Merged) ![]const Merged {
    var out: std.ArrayList(Merged) = .empty;
    try out.ensureTotalCapacity(arena, items.len);
    var emitted = std.StringHashMap(void).init(arena);
    var visiting = std.StringHashMap(void).init(arena);
    var by_guid = std.StringHashMap(Merged).init(arena);
    for (items) |m| try by_guid.put(m.obj.guid, m);

    var i: usize = 0;
    while (i < items.len) : (i += 1) {
        try emit(items[i], &by_guid, &emitted, &visiting, &out, arena);
    }
    return out.items;
}

fn emit(
    m: Merged,
    by_guid: *std.StringHashMap(Merged),
    emitted: *std.StringHashMap(void),
    visiting: *std.StringHashMap(void),
    out: *std.ArrayList(Merged),
    arena: std.mem.Allocator,
) !void {
    if (emitted.contains(m.obj.guid)) return;
    var node = m;
    if (node.parent_guid.len != 0) {
        if (visiting.contains(node.parent_guid)) {
            node.parent_guid = ""; // cycle across branches — break it at root
        } else if (by_guid.get(node.parent_guid)) |parent| {
            try visiting.put(m.obj.guid, {});
            try emit(parent, by_guid, emitted, visiting, out, arena);
            _ = visiting.remove(m.obj.guid);
        } else {
            node.parent_guid = ""; // parent not in the merged set
        }
    }
    try emitted.put(node.obj.guid, {});
    try out.append(arena, node);
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

const testing = std.testing;

fn obj(name: []const u8, guid: []const u8, parent: i32) SceneObject {
    return .{ .name = name, .guid = guid, .parent = parent };
}

fn sceneBytes(a: std.mem.Allocator, objects: []const SceneObject) ![]u8 {
    return serde.json.toSliceWith(a, SceneFile{ .version = CURRENT_VERSION, .objects = objects }, .{});
}

test "parallel additions merge cleanly" {
    const a = testing.allocator;
    const base = try sceneBytes(a, &.{obj("Root", "g0", -1)});
    defer a.free(base);
    const ours = try sceneBytes(a, &.{ obj("Root", "g0", -1), obj("Player", "g1", -1) });
    defer a.free(ours);
    const theirs = try sceneBytes(a, &.{ obj("Root", "g0", -1), obj("Enemy", "g2", -1) });
    defer a.free(theirs);

    var result = try merge(a, base, ours, theirs);
    defer result.deinit(a);

    try testing.expectEqual(@as(usize, 0), result.conflicts.len);
    try testing.expect(std.mem.indexOf(u8, result.json, "\"Player\"") != null);
    try testing.expect(std.mem.indexOf(u8, result.json, "\"Enemy\"") != null);
}

test "rename vs move on the same object merges without conflict" {
    const a = testing.allocator;
    var base_obj = obj("Player", "g1", -1);
    var ours_obj = obj("PlayerOne", "g1", -1);
    var theirs_obj = obj("Player", "g1", -1);
    theirs_obj.transform.position.x = 5;
    _ = &base_obj;
    _ = &ours_obj;

    const base = try sceneBytes(a, &.{base_obj});
    defer a.free(base);
    const ours = try sceneBytes(a, &.{ours_obj});
    defer a.free(ours);
    const theirs = try sceneBytes(a, &.{theirs_obj});
    defer a.free(theirs);

    var result = try merge(a, base, ours, theirs);
    defer result.deinit(a);

    try testing.expectEqual(@as(usize, 0), result.conflicts.len);
    try testing.expect(std.mem.indexOf(u8, result.json, "\"PlayerOne\"") != null);

    var arena = std.heap.ArenaAllocator.init(a);
    defer arena.deinit();
    const parsed = try serde.json.fromSlice(SceneFile, arena.allocator(), result.json);
    try testing.expectEqual(@as(f32, 5), parsed.objects[0].transform.position.x);
}

test "same field changed differently is a real conflict" {
    const a = testing.allocator;
    const base = try sceneBytes(a, &.{obj("Player", "g1", -1)});
    defer a.free(base);
    var ours_obj = obj("PlayerOne", "g1", -1);
    var theirs_obj = obj("PlayerTwo", "g1", -1);
    _ = &ours_obj;
    _ = &theirs_obj;
    const ours = try sceneBytes(a, &.{ours_obj});
    defer a.free(ours);
    const theirs = try sceneBytes(a, &.{theirs_obj});
    defer a.free(theirs);

    var result = try merge(a, base, ours, theirs);
    defer result.deinit(a);

    try testing.expectEqual(@as(usize, 1), result.conflicts.len);
    try testing.expect(std.mem.indexOf(u8, result.conflicts[0].detail, "name changed in both branches") != null);
}

test "delete vs modify is a conflict that keeps the modified side" {
    const a = testing.allocator;
    const base = try sceneBytes(a, &.{obj("Player", "g1", -1)});
    defer a.free(base);
    const ours = try sceneBytes(a, &.{}); // ours deletes it
    defer a.free(ours);
    var theirs_obj = obj("Player", "g1", -1);
    theirs_obj.transform.position.y = 3;
    const theirs = try sceneBytes(a, &.{theirs_obj});
    defer a.free(theirs);

    var result = try merge(a, base, ours, theirs);
    defer result.deinit(a);

    try testing.expectEqual(@as(usize, 1), result.conflicts.len);
    try testing.expect(std.mem.indexOf(u8, result.json, "\"Player\"") != null);
}

test "unrelated deletion on both sides drops cleanly" {
    const a = testing.allocator;
    const base = try sceneBytes(a, &.{ obj("Root", "g0", -1), obj("Temp", "g1", -1) });
    defer a.free(base);
    const ours = try sceneBytes(a, &.{obj("Root", "g0", -1)});
    defer a.free(ours);
    const theirs = try sceneBytes(a, &.{obj("Root", "g0", -1)});
    defer a.free(theirs);

    var result = try merge(a, base, ours, theirs);
    defer result.deinit(a);

    try testing.expectEqual(@as(usize, 0), result.conflicts.len);
    try testing.expect(std.mem.indexOf(u8, result.json, "\"Temp\"") == null);
}

test "hierarchy is preserved and parent indices are remapped" {
    const a = testing.allocator;
    const base = try sceneBytes(a, &.{ obj("Root", "g0", -1), obj("Child", "g1", 0) });
    defer a.free(base);
    // ours adds a sibling before Root in file order.
    const ours = try sceneBytes(a, &.{ obj("New", "g2", -1), obj("Root", "g0", -1), obj("Child", "g1", 1) });
    defer a.free(ours);
    const theirs = try sceneBytes(a, &.{ obj("Root", "g0", -1), obj("Child", "g1", 0) });
    defer a.free(theirs);

    var result = try merge(a, base, ours, theirs);
    defer result.deinit(a);

    try testing.expectEqual(@as(usize, 0), result.conflicts.len);
    var arena = std.heap.ArenaAllocator.init(a);
    defer arena.deinit();
    const parsed = try serde.json.fromSlice(SceneFile, arena.allocator(), result.json);

    var child_idx: ?usize = null;
    var root_idx: ?usize = null;
    for (parsed.objects, 0..) |o, i| {
        if (std.mem.eql(u8, o.guid, "g1")) child_idx = i;
        if (std.mem.eql(u8, o.guid, "g0")) root_idx = i;
    }
    try testing.expectEqual(@as(i32, @intCast(root_idx.?)), parsed.objects[child_idx.?].parent);
}
