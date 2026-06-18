//! Prefab system (issue #32) — reusable SceneNode subtrees with per-instance
//! overrides and source-edit propagation.
//!
//! A prefab is simply a **scene asset** reused as an instantiable template —
//! same on-disk structure (`version` + an `objects` array, root at index 0),
//! just a `.prefab` extension signalling intent. This module is the pure,
//! file-free core: it serialises a subtree into scene bytes, instantiates those
//! bytes into a scene's node array (linking each instance node back to its
//! template by GUID), and reconciles instances with their source via
//! revert / propagate. The studio layer wires these into the editor, handling
//! file IO and locating the instance subtree inside `EditorState.objects`.
//!
//! Override granularity is the **group** (`name`, `active`, `transform`,
//! `components`) — coarse enough for the fixed-size value-type SceneNode, and
//! correct for propagation: a group is inherited or overridden wholesale.

const std = @import("std");
const engine = @import("engine");
const Guid = @import("guid").Guid;
const scene_io = @import("SceneIo.zig");

const SceneNode = engine.SceneNode;
const OverrideGroup = engine.scene.OverrideGroup;
const MAX_OBJECTS = engine.scene.MAX_OBJECTS;

/// How `syncInstance` reconciles an instance with its source template.
pub const SyncMode = enum {
    /// Discard all instance changes: copy every group from the template and
    /// clear the override record.
    revert,
    /// Re-apply source edits to inherited groups, preserving overridden ones.
    propagate,
};

// ── Subtree extraction → prefab bytes ───────────────────────────────────────

/// Collect `root_idx` and all its descendants (depth-first) from `objects`.
/// Writes the original indices into `out` and returns the count, or 0 on error.
fn collectSubtree(objects: []const SceneNode, count: usize, root_idx: usize, out: []usize) usize {
    if (root_idx >= count) return 0;
    var n: usize = 0;
    out[n] = root_idx;
    n += 1;
    // Forward scan: parents always precede children in the array, so a single
    // pass that pulls in any node whose parent is already collected suffices.
    var i: usize = 0;
    while (i < count) : (i += 1) {
        if (objects[i].parent < 0) continue;
        const p: usize = @intCast(objects[i].parent);
        var included = false;
        for (out[0..n]) |c| {
            if (c == i) {
                included = true;
                break;
            }
        }
        if (included) continue;
        for (out[0..n]) |c| {
            if (c == p) {
                if (n < out.len) {
                    out[n] = i;
                    n += 1;
                }
                break;
            }
        }
    }
    return n;
}

/// Serialise the subtree rooted at `root_idx` into prefab JSON bytes owned by
/// `allocator` (caller frees), or null on failure. Parent indices are remapped
/// to be subtree-relative (root → -1) and prefab linkage is stripped so the
/// result is a clean, standalone template.
pub fn serializeSubtree(
    allocator: std.mem.Allocator,
    objects: []const SceneNode,
    count: usize,
    root_idx: usize,
) ?[]u8 {
    var indices: [MAX_OBJECTS]usize = undefined;
    const n = collectSubtree(objects, count, root_idx, &indices);
    if (n == 0) return null;

    // old scene index → new subtree-relative index
    var remap: [MAX_OBJECTS]i32 = undefined;
    @memset(remap[0..count], -1);
    for (indices[0..n], 0..) |orig, new_i| remap[orig] = @intCast(new_i);

    var tmpl: [MAX_OBJECTS]SceneNode = undefined;
    for (indices[0..n], 0..) |orig, new_i| {
        var node = objects[orig];
        node.parent = if (node.parent >= 0) remap[@intCast(node.parent)] else -1;
        node.clearPrefabLink(); // template nodes carry no instance linkage
        tmpl[new_i] = node;
    }

    return scene_io.serializeScene(allocator, &tmpl, n);
}

/// Serialise an existing prefab *instance* subtree back into template bytes,
/// preserving template-node identity. Unlike `serializeSubtree`, each node's
/// guid is set to its `prefab_node` (the original template identity) so that
/// other instances — which match by that guid — stay linked after the source
/// is rewritten. Used by "Apply" (push instance changes upstream).
pub fn serializeInstanceAsTemplate(
    allocator: std.mem.Allocator,
    objects: []const SceneNode,
    count: usize,
    root_idx: usize,
) ?[]u8 {
    var indices: [MAX_OBJECTS]usize = undefined;
    const n = collectSubtree(objects, count, root_idx, &indices);
    if (n == 0) return null;

    var remap: [MAX_OBJECTS]i32 = undefined;
    @memset(remap[0..count], -1);
    for (indices[0..n], 0..) |orig, new_i| remap[orig] = @intCast(new_i);

    var tmpl: [MAX_OBJECTS]SceneNode = undefined;
    for (indices[0..n], 0..) |orig, new_i| {
        var node = objects[orig];
        node.parent = if (node.parent >= 0) remap[@intCast(node.parent)] else -1;
        // Keep the template identity: prefab_node → guid (fall back to the
        // existing guid for nodes added to the instance with no template link).
        if (node.prefab_node_len != 0) node.setGuidStr(node.prefabNodeSlice());
        node.clearPrefabLink();
        tmpl[new_i] = node;
    }

    return scene_io.serializeScene(allocator, &tmpl, n);
}

// ── Instantiation ───────────────────────────────────────────────────────────

/// Parse `prefab_bytes` and append the resulting nodes to `out_objects`
/// (starting at `out_count.*`), reparenting the root under `parent`. Each new
/// node gets a fresh scene GUID and records the template node's GUID as its
/// `prefab_node`; the root additionally records `prefab_guid` as its
/// `prefab_source`. Returns the root's index in `out_objects`, or null if the
/// prefab is empty / would overflow the array.
pub fn instantiate(
    allocator: std.mem.Allocator,
    io: std.Io,
    prefab_bytes: []const u8,
    prefab_guid: []const u8,
    out_objects: []SceneNode,
    out_count: *usize,
    parent: i32,
) ?usize {
    var tmpl: [MAX_OBJECTS]SceneNode = undefined;
    var tn: usize = 0;
    if (!scene_io.loadSceneFromBytes(allocator, prefab_bytes, &tmpl, &tn)) return null;
    if (tn == 0) return null;

    const base = out_count.*;
    if (base + tn > out_objects.len) return null;
    const offset: i32 = @intCast(base);

    for (tmpl[0..tn], 0..) |src, i| {
        var node = src;
        // Reparent: template root (-1) attaches to `parent`; others shift by base.
        node.parent = if (src.parent < 0) parent else src.parent + offset;
        // The template's own guid is the stable template-node identity.
        node.setPrefabNode(src.guidSlice());
        var guid_buf: [36]u8 = undefined;
        node.setGuidStr(Guid.v4(io).toString(&guid_buf));
        node.clearOverrides();
        node.prefab_source_len = 0;
        out_objects[base + i] = node;
    }
    out_objects[base].setPrefabSource(prefab_guid);
    out_count.* += tn;
    return base;
}

// ── Per-node reconciliation ─────────────────────────────────────────────────

fn copyName(dst: *SceneNode, tmpl: *const SceneNode) void {
    dst.setName(tmpl.nameSlice());
}

/// Copy non-identity groups from `tmpl` into `dst`. When `respect_overrides` is
/// true, any group `dst` has marked overridden is left untouched. Identity and
/// linkage fields (guid, parent, prefab_source, prefab_node, overrides) are
/// always preserved.
pub fn applyTemplate(dst: *SceneNode, tmpl: *const SceneNode, respect_overrides: bool) void {
    if (!(respect_overrides and dst.hasOverride(.name))) copyName(dst, tmpl);
    if (!(respect_overrides and dst.hasOverride(.active))) dst.active = tmpl.active;
    if (!(respect_overrides and dst.hasOverride(.transform))) dst.transform = tmpl.transform;
    if (!(respect_overrides and dst.hasOverride(.components))) {
        dst.components = tmpl.components;
        dst.component_count = tmpl.component_count;
    }
}

/// Recompute `dst`'s override record by diffing each group against `tmpl`.
/// Replaces any existing record. Called after an instance edit so propagation
/// keeps exactly the groups the user changed.
pub fn recomputeOverrides(dst: *SceneNode, tmpl: *const SceneNode) void {
    dst.clearOverrides();
    if (!std.mem.eql(u8, dst.nameSlice(), tmpl.nameSlice())) dst.addOverride(.name);
    if (dst.active != tmpl.active) dst.addOverride(.active);
    if (!std.meta.eql(dst.transform, tmpl.transform)) dst.addOverride(.transform);
    if (!componentsEqual(dst, tmpl)) dst.addOverride(.components);
}

fn componentsEqual(a: *const SceneNode, b: *const SceneNode) bool {
    if (a.component_count != b.component_count) return false;
    for (a.components[0..a.component_count], b.components[0..b.component_count]) |*ca, *cb| {
        if (!std.meta.eql(ca.*, cb.*)) return false;
    }
    return true;
}

// ── Instance-set reconciliation ─────────────────────────────────────────────

/// Find the template node in `tmpl` whose guid matches `node`'s `prefab_node`,
/// or null. Public so callers can reconcile non-contiguous instance nodes.
pub fn findTemplate(node: *const SceneNode, tmpl: []const SceneNode) ?*const SceneNode {
    return matchTemplate(node, tmpl);
}

/// Find the template node in `tmpl` whose guid matches `node`'s `prefab_node`.
fn matchTemplate(node: *const SceneNode, tmpl: []const SceneNode) ?*const SceneNode {
    const key = node.prefabNodeSlice();
    if (key.len == 0) return null;
    for (tmpl) |*t| {
        if (std.mem.eql(u8, t.guidSlice(), key)) return t;
    }
    return null;
}

/// Reconcile each node of an instance with its template. `instance` is the set
/// of scene nodes belonging to one prefab instance (root + descendants); `tmpl`
/// is the parsed template subtree. Nodes without a matching template are left
/// as-is (e.g. children added to the instance).
pub fn syncInstance(instance: []SceneNode, tmpl: []const SceneNode, mode: SyncMode) void {
    for (instance) |*node| {
        const t = matchTemplate(node, tmpl) orelse continue;
        switch (mode) {
            .revert => {
                applyTemplate(node, t, false);
                node.clearOverrides();
            },
            .propagate => applyTemplate(node, t, true),
        }
    }
}

/// Recompute overrides for every node of an instance against its template.
pub fn recomputeInstanceOverrides(instance: []SceneNode, tmpl: []const SceneNode) void {
    for (instance) |*node| {
        const t = matchTemplate(node, tmpl) orelse continue;
        recomputeOverrides(node, t);
    }
}

// ── Parsing helper ──────────────────────────────────────────────────────────

/// Parse prefab bytes into `out` (a SceneNode buffer), returning the node count
/// or null on failure. Thin wrapper over the shared scene parser.
pub fn parse(allocator: std.mem.Allocator, bytes: []const u8, out: []SceneNode) ?usize {
    var n: usize = 0;
    if (!scene_io.loadSceneFromBytes(allocator, bytes, out, &n)) return null;
    return n;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

const testing = std.testing;

fn testNode(name: []const u8, parent: i32, io: std.Io) SceneNode {
    var n = SceneNode{};
    n.setName(name);
    n.parent = parent;
    var buf: [36]u8 = undefined;
    n.setGuidStr(Guid.v4(io).toString(&buf));
    return n;
}

test "serialize subtree then instantiate links template nodes" {
    const a = testing.allocator;
    const io = testing.io;

    // Scene: 0 Root, 1 Child(parent 0), 2 Unrelated.
    var objects: [MAX_OBJECTS]SceneNode = undefined;
    objects[0] = testNode("Root", -1, io);
    objects[0].transform.position = .{ .x = 1, .y = 2, .z = 3 };
    _ = objects[0].addComponent(.{ .light = .{} });
    objects[1] = testNode("Child", 0, io);
    objects[2] = testNode("Unrelated", -1, io);
    const count: usize = 3;

    const bytes = serializeSubtree(a, &objects, count, 0).?;
    defer a.free(bytes);

    // Instantiate into a fresh scene under no parent.
    var scene: [MAX_OBJECTS]SceneNode = undefined;
    var sc: usize = 0;
    const root = instantiate(a, io, bytes, "11111111-1111-4111-8111-111111111111", &scene, &sc, -1).?;

    try testing.expectEqual(@as(usize, 0), root);
    try testing.expectEqual(@as(usize, 2), sc); // only the subtree (Root + Child)
    try testing.expectEqualStrings("Root", scene[0].nameSlice());
    try testing.expectEqualStrings("Child", scene[1].nameSlice());
    try testing.expectEqual(@as(i32, -1), scene[0].parent);
    try testing.expectEqual(@as(i32, 0), scene[1].parent);

    // Root is an instance root; both nodes are part of the prefab.
    try testing.expect(scene[0].isPrefabInstanceRoot());
    try testing.expect(scene[1].isPartOfPrefab());
    try testing.expect(!scene[1].isPrefabInstanceRoot());
    try testing.expectEqualStrings("11111111-1111-4111-8111-111111111111", scene[0].prefabSourceSlice());

    // Fresh scene guids, but prefab_node matches the template guid.
    try testing.expectEqualStrings(objects[0].guidSlice(), scene[0].prefabNodeSlice());
    try testing.expect(!std.mem.eql(u8, scene[0].guidSlice(), scene[0].prefabNodeSlice()));

    // Component and transform carried across.
    try testing.expectEqual(@as(usize, 1), scene[0].component_count);
    try testing.expectEqual(@as(f32, 1), scene[0].transform.position.x);
}

test "two instances are independent and reparent under a host" {
    const a = testing.allocator;
    const io = testing.io;

    var objects: [MAX_OBJECTS]SceneNode = undefined;
    objects[0] = testNode("Coin", -1, io);
    const bytes = serializeSubtree(a, &objects, 1, 0).?;
    defer a.free(bytes);

    var scene: [MAX_OBJECTS]SceneNode = undefined;
    scene[0] = testNode("Host", -1, io);
    var sc: usize = 1;
    const r1 = instantiate(a, io, bytes, "22222222-2222-4222-8222-222222222222", &scene, &sc, 0).?;
    const r2 = instantiate(a, io, bytes, "22222222-2222-4222-8222-222222222222", &scene, &sc, 0).?;

    try testing.expect(r1 != r2);
    try testing.expectEqual(@as(usize, 3), sc);
    try testing.expectEqual(@as(i32, 0), scene[r1].parent); // both under Host
    try testing.expectEqual(@as(i32, 0), scene[r2].parent);
    // Distinct scene identities.
    try testing.expect(!std.mem.eql(u8, scene[r1].guidSlice(), scene[r2].guidSlice()));
}

test "recompute, propagate, and revert respect overrides" {
    const a = testing.allocator;
    const io = testing.io;

    // Template subtree from a single node.
    var objects: [MAX_OBJECTS]SceneNode = undefined;
    objects[0] = testNode("Enemy", -1, io);
    objects[0].transform.position = .{ .x = 0, .y = 0, .z = 0 };
    objects[0].active = true;
    const bytes = serializeSubtree(a, &objects, 1, 0).?;
    defer a.free(bytes);

    var scene: [MAX_OBJECTS]SceneNode = undefined;
    var sc: usize = 0;
    _ = instantiate(a, io, bytes, "33333333-3333-4333-8333-333333333333", &scene, &sc, -1).?;

    // Parse the template for reconciliation.
    var tmpl: [MAX_OBJECTS]SceneNode = undefined;
    const tn = parse(a, bytes, &tmpl).?;

    // User moves the instance → transform becomes an override.
    scene[0].transform.position = .{ .x = 5, .y = 0, .z = 0 };
    recomputeInstanceOverrides(scene[0..sc], tmpl[0..tn]);
    try testing.expect(scene[0].hasOverride(.transform));
    try testing.expect(!scene[0].hasOverride(.name));

    // Source prefab is edited: renamed + repositioned. Rebuild the template.
    var objects2: [MAX_OBJECTS]SceneNode = undefined;
    objects2[0] = objects[0];
    objects2[0].setName("Boss");
    objects2[0].transform.position = .{ .x = 0, .y = 9, .z = 0 };
    const bytes2 = serializeSubtree(a, &objects2, 1, 0).?;
    defer a.free(bytes2);
    var tmpl2: [MAX_OBJECTS]SceneNode = undefined;
    const tn2 = parse(a, bytes2, &tmpl2).?;

    // Propagate: name (inherited) updates, transform (overridden) is kept.
    syncInstance(scene[0..sc], tmpl2[0..tn2], .propagate);
    try testing.expectEqualStrings("Boss", scene[0].nameSlice());
    try testing.expectEqual(@as(f32, 5), scene[0].transform.position.x);
    try testing.expectEqual(@as(f32, 0), scene[0].transform.position.y);

    // Revert: everything snaps to the (new) template and overrides clear.
    syncInstance(scene[0..sc], tmpl2[0..tn2], .revert);
    try testing.expectEqual(@as(f32, 0), scene[0].transform.position.x);
    try testing.expectEqual(@as(f32, 9), scene[0].transform.position.y);
    try testing.expect(!scene[0].hasOverride(.transform));
    try testing.expectEqual(@as(usize, 0), scene[0].override_count);
}
