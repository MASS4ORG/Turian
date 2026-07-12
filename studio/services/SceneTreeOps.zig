const std = @import("std");
const engine = @import("engine");
const editor = @import("editor");

const State = @import("State.zig");
const EditorState = @import("EditorState.zig");
const UndoRedo = @import("UndoRedo.zig");
const Selection = @import("Selection.zig");

pub const Vector3 = engine.Vector3;
pub const Transform = engine.Transform;
pub const Component = engine.Component;
pub const UserScriptRef = engine.UserScriptRef;
pub const SceneNode = engine.SceneNode;
pub const Project = engine.Project;
pub const MAX_OBJECTS = engine.scene.MAX_OBJECTS;
pub const NAME_MAX = engine.scene.NAME_MAX;
pub const ComponentDef = editor.ComponentDef;

pub const MAX_DISCOVERED = editor.scanner.MAX_COMPONENTS;

pub fn deleteSelectedObjects(now: i128) void {
    if (Selection.selectedCount() == 0) return;

    // Single-select fast path (avoids snapshot overhead)
    if (Selection.selectedCount() == 1) {
        if (EditorState.selected_object) |sel| {
            deleteObject(now, sel);
            return;
        }
    }

    const before = UndoRedo.captureSnapshot();

    // Mark selected objects and all their descendants
    var to_remove_set = [_]bool{false} ** MAX_OBJECTS;
    for (0..EditorState.object_count) |i| {
        if (Selection.isObjectSelected(i)) to_remove_set[i] = true;
    }
    // Expand to descendants (forward pass; parents always precede children)
    for (0..EditorState.object_count) |i| {
        if (!to_remove_set[i] and EditorState.objects[i].parent >= 0) {
            if (to_remove_set[@intCast(EditorState.objects[i].parent)]) to_remove_set[i] = true;
        }
    }

    var index_map: [MAX_OBJECTS]i32 = undefined;
    var next_idx: usize = 0;
    for (0..EditorState.object_count) |i| {
        index_map[i] = if (to_remove_set[i]) -1 else blk: {
            const j: i32 = @intCast(next_idx);
            next_idx += 1;
            break :blk j;
        };
    }

    // Compact in place — no `[MAX_OBJECTS]SceneNode` scratch buffer (that
    // stack allocation overflowed the thread stack; same class of bug as the
    // Spawner.zig fix). Safe without a temp buffer because `index_map[i] <=
    // i` always (compaction only ever shifts entries toward index 0), so
    // scanning `i` forward and writing `objects[index_map[i]] = objects[i]`
    // never clobbers a source index the loop hasn't read yet.
    for (0..EditorState.object_count) |i| {
        const ni = index_map[i];
        if (ni != -1) {
            var obj = EditorState.objects[i];
            if (obj.parent >= 0) obj.parent = index_map[@intCast(obj.parent)];
            EditorState.objects[@intCast(ni)] = obj;
        }
    }
    EditorState.object_count = next_idx;
    EditorState.selected_object = null;
    Selection.clearSelectedObjects();
    EditorState.scene_dirty = true;

    const after = UndoRedo.captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .delete_object = .{ .before = before, .after = after } });
}

pub fn duplicateSelectedObjects(now: i128, io: std.Io) void {
    const count = Selection.selectedCount();
    if (count == 0) return;

    UndoRedo.beginGroup();
    // Use a temp list of indices since duplication clears/changes selection
    var selected_indices = std.ArrayList(usize){ .items = &.{}, .capacity = 0 };
    defer selected_indices.deinit(UndoRedo.undo_alloc);
    for (0..EditorState.object_count) |i| {
        if (Selection.isObjectSelected(i)) {
            selected_indices.append(UndoRedo.undo_alloc, i) catch {};
        }
    }

    // Duplicate in descending order to keep indices stable
    var i = selected_indices.items.len;
    while (i > 0) {
        i -= 1;
        duplicateObject(now, io, selected_indices.items[i]);
    }
    UndoRedo.endGroup(now);
}

pub fn deleteObject(now: i128, idx: usize) void {
    if (idx >= EditorState.object_count) return;

    const before = UndoRedo.captureSnapshot();

    // 1. Identify subtree to remove
    var to_remove_set = [_]bool{false} ** MAX_OBJECTS;
    var remove_count: usize = 0;
    var scan_stack: [MAX_OBJECTS]usize = undefined;
    var stack_len: usize = 0;
    scan_stack[stack_len] = idx;
    stack_len += 1;

    while (stack_len > 0) {
        stack_len -= 1;
        const cur = scan_stack[stack_len];
        if (to_remove_set[cur]) continue;
        to_remove_set[cur] = true;
        remove_count += 1;

        for (0..EditorState.object_count) |child_idx| {
            if (EditorState.objects[child_idx].parent == @as(i32, @intCast(cur))) {
                scan_stack[stack_len] = child_idx;
                stack_len += 1;
            }
        }
    }

    // 2. Build index mapping
    var index_map: [MAX_OBJECTS]i32 = undefined;
    var next_new_idx: usize = 0;
    for (0..EditorState.object_count) |i| {
        if (to_remove_set[i]) {
            index_map[i] = -1;
        } else {
            index_map[i] = @intCast(next_new_idx);
            next_new_idx += 1;
        }
    }

    // 3. Rebuild the objects array in place — no `[MAX_OBJECTS]SceneNode`
    // scratch buffer (that stack allocation overflowed the thread stack;
    // same class of bug as the Spawner.zig fix). Safe without a temp buffer
    // because `index_map[i] <= i` always (compaction only ever shifts
    // entries toward index 0), so scanning `i` forward and writing
    // `objects[index_map[i]] = objects[i]` never clobbers a source index the
    // loop hasn't read yet.
    for (0..EditorState.object_count) |i| {
        const new_idx = index_map[i];
        if (new_idx != -1) {
            var obj = EditorState.objects[i];
            if (obj.parent >= 0) {
                obj.parent = index_map[@as(usize, @intCast(obj.parent))];
            }
            EditorState.objects[@as(usize, @intCast(new_idx))] = obj;
        }
    }

    EditorState.object_count = next_new_idx;

    EditorState.selected_object = null;
    Selection.clearSelectedObjects();
    EditorState.scene_dirty = true;

    const after = UndoRedo.captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .delete_object = .{ .before = before, .after = after } });
}

pub fn duplicateObject(now: i128, io: std.Io, idx: usize) void {
    if (idx >= EditorState.object_count) return;
    if (EditorState.object_count >= MAX_OBJECTS) return;

    const before = UndoRedo.captureSnapshot();

    var insert_at = idx + 1;
    var scan_pos = idx + 1;
    while (scan_pos < EditorState.object_count) : (scan_pos += 1) {
        if (EditorState.objects[scan_pos].parent >= 0) {
            var p = EditorState.objects[scan_pos].parent;
            var is_child = false;
            while (p >= 0) {
                if (p == @as(i32, @intCast(idx))) {
                    is_child = true;
                    break;
                }
                p = EditorState.objects[@intCast(p)].parent;
            }
            if (is_child) {
                insert_at = scan_pos + 1;
            } else {
                break;
            }
        } else {
            break;
        }
    }

    if (insert_at < EditorState.object_count) {
        var i = EditorState.object_count;
        while (i > insert_at) : (i -= 1) {
            EditorState.objects[i] = EditorState.objects[i - 1];
        }
    }
    EditorState.object_count += 1;

    var new_obj = EditorState.objects[idx];
    var guid_buf: [36]u8 = undefined;
    new_obj.setGuidStr(editor.Guid.v4(io).toString(&guid_buf));
    {
        var name_buf: [NAME_MAX]u8 = undefined;
        const orig_name = EditorState.objects[idx].nameSlice();
        const copy_name = std.fmt.bufPrint(&name_buf, "{s} (copy)", .{orig_name}) catch orig_name;
        new_obj.setName(copy_name);
    }
    EditorState.objects[insert_at] = new_obj;

    EditorState.selected_object = insert_at;
    Selection.clearSelectedObjects();
    Selection.selectObject(insert_at);
    EditorState.scene_dirty = true;

    const after = UndoRedo.captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .duplicate_object = .{ .before = before, .after = after } });
}

pub fn moveObjectBefore(now: i128, src_idx: usize, before_idx: usize) void {
    if (src_idx == before_idx) return;

    const before = UndoRedo.captureSnapshot();

    var src_obj = EditorState.objects[src_idx];
    src_obj.parent = if (before_idx < EditorState.object_count) EditorState.objects[before_idx].parent else -1;

    const si = @as(i32, @intCast(src_idx));
    const bi = @as(i32, @intCast(before_idx));

    if (src_idx < before_idx) {
        const new_src_idx = before_idx - 1;
        const new_si = @as(i32, @intCast(new_src_idx));

        for (EditorState.objects[0..EditorState.object_count]) |*o| {
            if (o.parent == si) {
                o.parent = new_si;
            } else if (o.parent > si and o.parent < bi) {
                o.parent -= 1;
            }
        }

        for (src_idx..new_src_idx) |i| EditorState.objects[i] = EditorState.objects[i + 1];
        EditorState.objects[new_src_idx] = src_obj;

        if (EditorState.selected_object) |sel| {
            if (sel == src_idx) EditorState.selected_object = new_src_idx else if (sel > src_idx and sel < before_idx) EditorState.selected_object = sel - 1;
        }

        // Update multi-select set
        if (Selection.isObjectSelected(src_idx)) {
            Selection.deselectObject(src_idx);
            Selection.selectObject(new_src_idx);
        }
        for (src_idx..new_src_idx) |i| {
            if (Selection.isObjectSelected(i + 1)) {
                Selection.selectObject(i);
                Selection.deselectObject(i + 1);
            }
        }
    } else {
        for (EditorState.objects[0..EditorState.object_count]) |*o| {
            if (o.parent == si) {
                o.parent = bi;
            } else if (o.parent >= bi and o.parent < si) {
                o.parent += 1;
            }
        }

        var i: usize = src_idx;
        while (i > before_idx) : (i -= 1) EditorState.objects[i] = EditorState.objects[i - 1];
        EditorState.objects[before_idx] = src_obj;

        if (EditorState.selected_object) |sel| {
            if (sel == src_idx) EditorState.selected_object = before_idx else if (sel >= before_idx and sel < src_idx) EditorState.selected_object = sel + 1;
        }

        // Update multi-select set
        if (Selection.isObjectSelected(src_idx)) {
            Selection.deselectObject(src_idx);
            Selection.selectObject(before_idx);
        }
        var j: usize = src_idx;
        while (j > before_idx) : (j -= 1) {
            if (Selection.isObjectSelected(j - 1)) {
                Selection.selectObject(j);
                Selection.deselectObject(j - 1);
            }
        }
    }

    EditorState.scene_dirty = true;
    const after = UndoRedo.captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .reparent_object = .{ .before = before, .after = after } });
}

/// True when `maybe_ancestor` is `node` or one of its ancestors.
pub fn isAncestorOrSelf(node: usize, maybe_ancestor: i32) bool {
    var p: i32 = @intCast(node);
    while (p >= 0) {
        if (p == maybe_ancestor) return true;
        p = EditorState.objects[@intCast(p)].parent;
    }
    return false;
}

/// Move `drag` (and its whole subtree) to become a child of `new_parent`
/// (-1 = scene root), inserted immediately before `before_sibling` among
/// `new_parent`'s children, or appended last when `before_sibling` is -1.
/// Indices are the *current* array indices. No-op if it would create a cycle.
/// Rebuilds the array via a tree walk, so the parent-precedes-child invariant
/// is preserved and all parent indices stay correct.
pub fn reparentObject(now: i128, drag: usize, new_parent: i32, before_sibling: i32) void {
    if (drag >= EditorState.object_count) return;
    if (new_parent == @as(i32, @intCast(drag))) return;
    // Reparenting under one's own descendant would orphan the subtree.
    if (new_parent >= 0 and isAncestorOrSelf(@intCast(new_parent), @intCast(drag))) return;

    // Children lists keyed by (parent + 1) so root (-1) lives at index 0.
    const KEYS = MAX_OBJECTS + 1;
    var children: [KEYS][MAX_OBJECTS]i32 = undefined;
    var child_count: [KEYS]usize = .{0} ** KEYS;
    for (0..EditorState.object_count) |i| {
        if (i == drag) continue; // re-inserted at the target below
        const key: usize = @intCast(EditorState.objects[i].parent + 1);
        children[key][child_count[key]] = @intCast(i);
        child_count[key] += 1;
    }

    // Insert `drag` into the new parent's child list before `before_sibling`.
    const pkey: usize = @intCast(new_parent + 1);
    var pos: usize = child_count[pkey];
    if (before_sibling >= 0) {
        for (0..child_count[pkey]) |k| {
            if (children[pkey][k] == before_sibling) {
                pos = k;
                break;
            }
        }
    }
    var k = child_count[pkey];
    while (k > pos) : (k -= 1) children[pkey][k] = children[pkey][k - 1];
    children[pkey][pos] = @intCast(drag);
    child_count[pkey] += 1;

    const before = UndoRedo.captureSnapshot();

    // DFS from the root to produce the new linear order of old indices.
    var order: [MAX_OBJECTS]usize = undefined;
    var order_n: usize = 0;
    var stack: [MAX_OBJECTS]i32 = undefined; // old indices pending emit (parent already emitted)
    var sp: usize = 0;
    // Seed with root children in reverse (so they pop in order).
    var ri = child_count[0];
    while (ri > 0) : (ri -= 1) {
        stack[sp] = children[0][ri - 1];
        sp += 1;
    }
    while (sp > 0) {
        sp -= 1;
        const old: usize = @intCast(stack[sp]);
        order[order_n] = old;
        order_n += 1;
        const ckey: usize = @intCast(old + 1);
        var ci = child_count[ckey];
        while (ci > 0) : (ci -= 1) {
            stack[sp] = children[ckey][ci - 1];
            sp += 1;
        }
    }
    if (order_n != EditorState.object_count) return; // safety: malformed tree, abort

    // old index -> new index
    var new_of_old: [MAX_OBJECTS]i32 = undefined;
    for (order[0..order_n], 0..) |old, ni| new_of_old[old] = @intCast(ni);

    // Heap, not `[MAX_OBJECTS]SceneNode` on the stack — that overflowed the
    // thread stack (same class of bug as the Spawner.zig fix). Unlike the
    // delete-path compaction above, this reorder is an arbitrary DFS-order
    // permutation (`new_of_old[old]` isn't monotonic in `old`), so it can't
    // be proven safe to apply in place — a real scratch buffer is needed.
    const rebuilt = EditorState.gpa.alloc(SceneNode, order_n) catch return;
    defer EditorState.gpa.free(rebuilt);
    for (order[0..order_n], 0..) |old, ni| {
        var node = EditorState.objects[old];
        if (old == drag) {
            node.parent = if (new_parent < 0) -1 else new_of_old[@intCast(new_parent)];
        } else {
            node.parent = if (node.parent < 0) -1 else new_of_old[@intCast(node.parent)];
        }
        rebuilt[ni] = node;
    }
    @memcpy(EditorState.objects[0..order_n], rebuilt[0..order_n]);

    // Remap selection.
    if (EditorState.selected_object) |s| EditorState.selected_object = @intCast(new_of_old[s]);
    var new_set: [MAX_OBJECTS]bool = .{false} ** MAX_OBJECTS;
    for (0..order_n) |old| {
        if (Selection.isObjectSelected(old)) new_set[@intCast(new_of_old[old])] = true;
    }
    for (0..MAX_OBJECTS) |i| {
        if (new_set[i]) Selection.selectObject(i) else Selection.deselectObject(i);
    }
    if (EditorState.last_select_idx) |l| EditorState.last_select_idx = @intCast(new_of_old[l]);

    EditorState.scene_dirty = true;
    const after = UndoRedo.captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .reparent_object = .{ .before = before, .after = after } });
}

pub fn addObject(io: std.Io, n: []const u8, parent: i32) usize {
    const idx = EditorState.object_count;
    EditorState.object_count += 1;
    EditorState.objects[idx] = .{};
    EditorState.objects[idx].setName(n);
    EditorState.objects[idx].parent = parent;
    var guid_buf: [36]u8 = undefined;
    EditorState.objects[idx].setGuidStr(editor.Guid.v4(io).toString(&guid_buf));
    return idx;
}

pub fn addObjectWithUndo(now: i128, io: std.Io, n: []const u8, parent: i32) usize {
    const before = UndoRedo.captureSnapshot();
    const idx = addObject(io, n, parent);
    const after = UndoRedo.captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .add_object = .{ .before = before, .after = after } });
    EditorState.scene_dirty = true;
    return idx;
}

/// Frame the camera on a specific object by updating the first camera's transform.
pub fn focusOnObject(idx: usize) void {
    if (idx >= EditorState.object_count) return;
    const target_pos = EditorState.objects[idx].transform.position;

    for (0..EditorState.object_count) |i| {
        for (EditorState.objects[i].components[0..EditorState.objects[i].component_count]) |*comp| {
            if (comp.* == .camera) {
                const dist: f32 = 5.0;
                const cam_pos = EditorState.objects[i].transform.position;
                const to_target = target_pos.subtract(cam_pos);
                const cur_dist = to_target.length();
                if (cur_dist > 0.001) {
                    const dir = to_target.normalize();
                    EditorState.objects[i].transform.position = target_pos.subtract(dir.scale(dist));
                } else {
                    EditorState.objects[i].transform.position = .{ .x = target_pos.x, .y = target_pos.y + dist, .z = target_pos.z + dist };
                }

                const new_to_target = target_pos.subtract(EditorState.objects[i].transform.position);
                if (new_to_target.length() > 0.001) {
                    const fwd = new_to_target.normalize();
                    const pitch = -std.math.asin(fwd.y);
                    const yaw = std.math.atan2(fwd.x, fwd.z);
                    EditorState.objects[i].transform.rotation = .{ .x = pitch * 180.0 / std.math.pi, .y = yaw * 180.0 / std.math.pi, .z = 0 };
                }
                EditorState.scene_dirty = true;
                return;
            }
        }
    }
}
