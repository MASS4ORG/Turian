const std = @import("std");
const engine = @import("engine");
const editor = @import("editor");
const build_options = @import("turian_build_options");

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

// ── Undo / Redo Command System ─────────────────────────────────────────────────

pub const MAX_UNDO = 50;

var undo_alloc: std.mem.Allocator = std.heap.page_allocator;

pub fn initUndo(allocator: std.mem.Allocator) void {
    undo_alloc = allocator;
}

const Snapshot = struct {
    objects: []SceneNode, // owned by undo_alloc
    object_count: usize,
    selected_object: ?usize,
};

fn captureSnapshot() Snapshot {
    const buf = undo_alloc.alloc(SceneNode, object_count) catch return .{
        .objects = &[_]SceneNode{},
        .object_count = 0,
        .selected_object = null,
    };
    @memcpy(buf, objects[0..object_count]);
    return .{
        .objects = buf,
        .object_count = object_count,
        .selected_object = selected_object,
    };
}

fn restoreSnapshot(s: Snapshot) void {
    if (s.object_count > MAX_OBJECTS) return;
    @memcpy(objects[0..s.object_count], s.objects);
    object_count = s.object_count;
    selected_object = s.selected_object;
}

fn freeSnapshot(s: Snapshot) void {
    undo_alloc.free(s.objects);
}

pub const UndoCommand = union(enum) {
    delete_object: struct {
        before: Snapshot,
        after: Snapshot,
    },
    reparent_object: struct {
        before: Snapshot,
        after: Snapshot,
    },
    modify_object: struct {
        idx: usize,
        before: SceneNode,
        after: SceneNode,
    },
    rename_object: struct {
        idx: usize,
        old_name: [NAME_MAX]u8,
        old_len: usize,
        new_name: [NAME_MAX]u8,
        new_len: usize,
    },
    duplicate_object: struct {
        before: Snapshot,
        after: Snapshot,
    },
    add_component: struct {
        obj_idx: usize,
        comp: Component,
        ins_idx: usize,
    },
    remove_component: struct {
        obj_idx: usize,
        comp: Component,
        rem_idx: usize,
    },
    add_object: struct {
        before: Snapshot,
        after: Snapshot,
    },
    group: struct {
        items: []UndoCommand, // owned by undo_alloc
    },

    pub fn label(self: *const UndoCommand) []const u8 {
        return switch (self.*) {
            .delete_object => "Delete Object",
            .reparent_object => "Reparent Object",
            .modify_object => "Edit",
            .rename_object => "Rename",
            .duplicate_object => "Duplicate Object",
            .add_component => "Add Component",
            .remove_component => "Remove Component",
            .add_object => "Add Object",
            .group => "Group",
        };
    }

    pub fn deinit(self: *UndoCommand) void {
        switch (self.*) {
            .delete_object => |cmd| {
                freeSnapshot(cmd.before);
                freeSnapshot(cmd.after);
            },
            .reparent_object => |cmd| {
                freeSnapshot(cmd.before);
                freeSnapshot(cmd.after);
            },
            .duplicate_object => |cmd| {
                freeSnapshot(cmd.before);
                freeSnapshot(cmd.after);
            },
            .add_object => |cmd| {
                freeSnapshot(cmd.before);
                freeSnapshot(cmd.after);
            },
            .group => |cmd| {
                for (cmd.items) |*item| item.deinit();
                undo_alloc.free(cmd.items);
            },
            else => {},
        }
    }

    pub fn undo(self: *const UndoCommand) void {
        switch (self.*) {
            .delete_object => |cmd| {
                restoreSnapshot(cmd.before);
            },
            .reparent_object => |cmd| {
                restoreSnapshot(cmd.before);
            },
            .modify_object => |cmd| {
                objects[cmd.idx] = cmd.before;
            },
            .rename_object => |cmd| {
                const obj = &objects[cmd.idx];
                obj.name_len = cmd.old_len;
                @memcpy(obj.name_buf[0..cmd.old_len], cmd.old_name[0..cmd.old_len]);
            },
            .duplicate_object => |cmd| {
                restoreSnapshot(cmd.before);
            },
            .add_component => |cmd| {
                objects[cmd.obj_idx].removeComponent(cmd.ins_idx);
            },
            .remove_component => |cmd| {
                const obj = &objects[cmd.obj_idx];
                if (obj.component_count > cmd.rem_idx) {
                    var ci = obj.component_count;
                    while (ci > cmd.rem_idx) : (ci -= 1) {
                        obj.components[ci] = obj.components[ci - 1];
                    }
                    obj.components[cmd.rem_idx] = cmd.comp;
                    obj.component_count += 1;
                }
            },
            .add_object => |cmd| {
                restoreSnapshot(cmd.before);
            },
            .group => |cmd| {
                var i = cmd.items.len;
                while (i > 0) {
                    i -= 1;
                    cmd.items[i].undo();
                }
            },
        }
        scene_dirty = true;
    }

    pub fn execute(self: *const UndoCommand) void {
        switch (self.*) {
            .delete_object => |cmd| {
                restoreSnapshot(cmd.after);
            },
            .reparent_object => |cmd| {
                restoreSnapshot(cmd.after);
            },
            .modify_object => |cmd| {
                objects[cmd.idx] = cmd.after;
            },
            .rename_object => |cmd| {
                const obj = &objects[cmd.idx];
                obj.name_len = cmd.new_len;
                @memcpy(obj.name_buf[0..cmd.new_len], cmd.new_name[0..cmd.new_len]);
            },
            .duplicate_object => |cmd| {
                restoreSnapshot(cmd.after);
            },
            .add_component => |cmd| {
                _ = objects[cmd.obj_idx].addComponent(cmd.comp);
            },
            .remove_component => |cmd| {
                objects[cmd.obj_idx].removeComponent(cmd.rem_idx);
            },
            .add_object => |cmd| {
                restoreSnapshot(cmd.after);
            },
            .group => |cmd| {
                for (cmd.items) |*item| item.execute();
            },
        }
        scene_dirty = true;
    }
};

/// Try to merge `new_cmd` into `last_cmd` for coalescing.
/// Returns true if `last_cmd` was updated (merged) and `new_cmd` should be discarded.
fn tryMergeCommands(last_cmd: *UndoCommand, new_cmd: *const UndoCommand) bool {
    if (last_cmd.* == .modify_object and new_cmd.* == .modify_object) {
        if (last_cmd.modify_object.idx == new_cmd.modify_object.idx) {
            last_cmd.modify_object.after = new_cmd.modify_object.after;
            return true;
        }
    }
    if (last_cmd.* == .rename_object and new_cmd.* == .rename_object) {
        if (last_cmd.rename_object.idx == new_cmd.rename_object.idx) {
            last_cmd.rename_object.new_len = new_cmd.rename_object.new_len;
            @memcpy(last_cmd.rename_object.new_name[0..new_cmd.rename_object.new_len], new_cmd.rename_object.new_name[0..new_cmd.rename_object.new_len]);
            return true;
        }
    }
    return false;
}

var undo_stack: [MAX_UNDO]UndoCommand = undefined;
var redo_stack: [MAX_UNDO]UndoCommand = undefined;
var undo_len: usize = 0;
var redo_len: usize = 0;

var last_push_ns: i128 = 0;
var last_modified_idx: ?usize = null;
var group_buffer: ?std.ArrayList(UndoCommand) = null;

pub fn beginGroup() void {
    if (group_buffer != null) return;
    group_buffer = .{ .items = &.{}, .capacity = 0 };
}

pub fn endGroup(now: i128) void {
    var gbo = group_buffer orelse return;
    group_buffer = null; // null before push so pushCommand doesn't re-buffer
    if (gbo.items.len == 0) {
        gbo.deinit(undo_alloc);
        return;
    }
    const items = gbo.toOwnedSlice(undo_alloc) catch {
        for (gbo.items) |*item| item.deinit();
        gbo.deinit(undo_alloc);
        return;
    };
    pushCommand(now, &.{ .group = .{ .items = items } });
}

pub fn pushCommand(now: i128, cmd: *const UndoCommand) void {
    if (group_buffer) |*gb| {
        gb.append(undo_alloc, cmd.*) catch {};
        return;
    }

    // Try to merge with the last command (coalescing)

    if (undo_len > 0) {
        const can_merge = now - last_push_ns < 500 * std.time.ns_per_ms;
        if (can_merge and tryMergeCommands(&undo_stack[undo_len - 1], cmd)) {
            // Discard the new_cmd since it was merged.
            var mutable_cmd = cmd.*;
            mutable_cmd.deinit();

            // Clear redo stack on new command
            for (0..redo_len) |i| redo_stack[i].deinit();
            redo_len = 0;
            return;
        }
    }

    // Clear redo stack on new command
    for (0..redo_len) |i| redo_stack[i].deinit();
    redo_len = 0;

    // If the saved state was in the redo stack, it's now unreachable
    if (saved_undo_depth) |saved| {
        if (saved > undo_len) {
            saved_undo_depth = null;
        }
    }

    if (undo_len >= MAX_UNDO) {
        undo_stack[0].deinit();
        for (0..MAX_UNDO - 1) |i| undo_stack[i] = undo_stack[i + 1];
        undo_len = MAX_UNDO - 1;

        if (saved_undo_depth) |saved| {
            if (saved == 0) {
                saved_undo_depth = null;
            } else {
                saved_undo_depth = saved - 1;
            }
        }
    }
    undo_stack[undo_len] = cmd.*;
    undo_len += 1;
    last_push_ns = now;

    switch (cmd.*) {
        .modify_object => |m| last_modified_idx = m.idx,
        .rename_object => |r| last_modified_idx = r.idx,
        else => last_modified_idx = null,
    }

    scene_dirty = (saved_undo_depth == null or undo_len != saved_undo_depth.?);
}

pub fn undo() void {
    if (undo_len == 0) return;
    undo_len -= 1;

    if (redo_len >= MAX_UNDO) {
        redo_stack[0].deinit();
        for (0..MAX_UNDO - 1) |i| redo_stack[i] = redo_stack[i + 1];
        redo_len = MAX_UNDO - 1;
    }
    redo_stack[redo_len] = undo_stack[undo_len];
    redo_len += 1;

    undo_stack[undo_len].undo();
    scene_dirty = (saved_undo_depth == null or undo_len != saved_undo_depth.?);
}

pub fn redo() void {
    if (redo_len == 0) return;
    redo_len -= 1;

    if (undo_len >= MAX_UNDO) {
        undo_stack[0].deinit();
        for (0..MAX_UNDO - 1) |i| undo_stack[i] = undo_stack[i + 1];
        undo_len = MAX_UNDO - 1;

        if (saved_undo_depth) |saved| {
            if (saved == 0) {
                saved_undo_depth = null;
            } else {
                saved_undo_depth = saved - 1;
            }
        }
    }
    undo_stack[undo_len] = redo_stack[redo_len];
    undo_len += 1;

    redo_stack[redo_len].execute();
    scene_dirty = (saved_undo_depth == null or undo_len != saved_undo_depth.?);
}

pub fn canUndo() bool {
    return undo_len > 0;
}

pub fn canRedo() bool {
    return redo_len > 0;
}

pub fn undoLabel() ?[]const u8 {
    if (undo_len == 0) return null;
    return undo_stack[undo_len - 1].label();
}

pub fn redoLabel() ?[]const u8 {
    if (redo_len == 0) return null;
    return redo_stack[redo_len - 1].label();
}

pub fn clearUndoStack() void {
    for (0..undo_len) |i| undo_stack[i].deinit();
    for (0..redo_len) |i| redo_stack[i].deinit();
    undo_len = 0;
    redo_len = 0;
    saved_undo_depth = 0;
    scene_dirty = false;
}

// ── Multi-selection ────────────────────────────────────────────────────────────

/// Bit set for multi-selection in the scene tree.
/// selected_object remains the primary (last-clicked) object for the Inspector.
pub var selected_set: [MAX_OBJECTS]bool = .{false} ** MAX_OBJECTS;
pub var last_select_idx: ?usize = null;

pub fn isObjectSelected(idx: usize) bool {
    if (idx >= MAX_OBJECTS) return false;
    return selected_set[idx];
}

pub fn selectObject(idx: usize) void {
    if (idx >= MAX_OBJECTS) return;
    selected_set[idx] = true;
    last_select_idx = idx;
}

pub fn deselectObject(idx: usize) void {
    if (idx >= MAX_OBJECTS) return;
    selected_set[idx] = false;
    if (last_select_idx == idx) last_select_idx = null;
}

pub fn toggleSelectObject(idx: usize) void {
    if (idx >= MAX_OBJECTS) return;
    selected_set[idx] = !selected_set[idx];
    if (selected_set[idx]) last_select_idx = idx;
}

pub fn clearSelectedObjects() void {
    @memset(selected_set[0..MAX_OBJECTS], false);
    last_select_idx = null;
}

pub fn selectObjectRange(from: usize, to: usize) void {
    const start = @min(from, to);
    const end = @max(from, to);
    for (start..end + 1) |i| {
        if (i < MAX_OBJECTS) selected_set[i] = true;
    }
    last_select_idx = to;
}

/// Count how many objects are selected.
pub fn selectedCount() usize {
    var count: usize = 0;
    for (selected_set[0..MAX_OBJECTS]) |s| {
        if (s) count += 1;
    }
    return count;
}

pub fn deleteSelectedObjects(now: i128) void {
    if (selectedCount() == 0) return;

    // Single-select fast path (avoids snapshot overhead)
    if (selectedCount() == 1) {
        if (selected_object) |sel| {
            deleteObject(now, sel);
            return;
        }
    }

    const before = captureSnapshot();

    // Mark selected objects and all their descendants
    var to_remove_set = [_]bool{false} ** MAX_OBJECTS;
    for (0..object_count) |i| {
        if (isObjectSelected(i)) to_remove_set[i] = true;
    }
    // Expand to descendants (forward pass; parents always precede children)
    for (0..object_count) |i| {
        if (!to_remove_set[i] and objects[i].parent >= 0) {
            if (to_remove_set[@intCast(objects[i].parent)]) to_remove_set[i] = true;
        }
    }

    // Build a new→old index map and reconstruct in a temp buffer
    var index_map: [MAX_OBJECTS]i32 = undefined;
    var next_idx: usize = 0;
    for (0..object_count) |i| {
        index_map[i] = if (to_remove_set[i]) -1 else blk: {
            const j: i32 = @intCast(next_idx);
            next_idx += 1;
            break :blk j;
        };
    }

    var new_objects: [MAX_OBJECTS]SceneNode = undefined;
    for (0..object_count) |i| {
        const ni = index_map[i];
        if (ni != -1) {
            var obj = objects[i];
            if (obj.parent >= 0) obj.parent = index_map[@intCast(obj.parent)];
            new_objects[@intCast(ni)] = obj;
        }
    }
    @memcpy(objects[0..next_idx], new_objects[0..next_idx]);
    object_count = next_idx;
    selected_object = null;
    clearSelectedObjects();
    scene_dirty = true;

    const after = captureSnapshot();
    pushCommand(now, &.{ .delete_object = .{ .before = before, .after = after } });
}

pub fn duplicateSelectedObjects(now: i128, io: std.Io) void {
    const count = selectedCount();
    if (count == 0) return;

    beginGroup();
    // Use a temp list of indices since duplication clears/changes selection
    var selected_indices = std.ArrayList(usize){ .items = &.{}, .capacity = 0 };
    defer selected_indices.deinit(undo_alloc);
    for (0..object_count) |i| {
        if (isObjectSelected(i)) {
            selected_indices.append(undo_alloc, i) catch {};
        }
    }

    // Duplicate in descending order to keep indices stable
    var i = selected_indices.items.len;
    while (i > 0) {
        i -= 1;
        duplicateObject(now, io, selected_indices.items[i]);
    }
    endGroup(now);
}

// ── Scene object deletion ──────────────────────────────────────────────────────

pub fn deleteObject(now: i128, idx: usize) void {
    if (idx >= object_count) return;

    const before = captureSnapshot();

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

        for (0..object_count) |child_idx| {
            if (objects[child_idx].parent == @as(i32, @intCast(cur))) {
                scan_stack[stack_len] = child_idx;
                stack_len += 1;
            }
        }
    }

    // 2. Build index mapping
    var index_map: [MAX_OBJECTS]i32 = undefined;
    var next_new_idx: usize = 0;
    for (0..object_count) |i| {
        if (to_remove_set[i]) {
            index_map[i] = -1;
        } else {
            index_map[i] = @intCast(next_new_idx);
            next_new_idx += 1;
        }
    }

    // 3. Rebuild objects array in-place (or into temp)
    // We'll use a temp array to avoid corruption while reading/writing
    var new_objects: [MAX_OBJECTS]SceneNode = undefined;
    for (0..object_count) |i| {
        const new_idx = index_map[i];
        if (new_idx != -1) {
            var obj = objects[i];
            if (obj.parent >= 0) {
                obj.parent = index_map[@as(usize, @intCast(obj.parent))];
            }
            new_objects[@as(usize, @intCast(new_idx))] = obj;
        }
    }

    @memcpy(objects[0..next_new_idx], new_objects[0..next_new_idx]);
    object_count = next_new_idx;

    selected_object = null;
    clearSelectedObjects();
    scene_dirty = true;

    const after = captureSnapshot();
    pushCommand(now, &.{ .delete_object = .{ .before = before, .after = after } });
}

// ── Rename editing state ───────────────────────────────────────────────────────

pub const RenameTarget = enum { none, scene_object, asset };

pub const RenameState = struct {
    target: RenameTarget = .none,
    idx: usize = 0,
    buf: [NAME_MAX]u8 = undefined,
    len: usize = 0,
    asset_path_buf: [1024]u8 = undefined,
    asset_path_len: usize = 0,
};

pub var g_rename: RenameState = .{};

/// Start renaming a scene object.
pub fn startRenameObject(idx: usize) void {
    if (idx >= object_count) return;
    const name = objects[idx].nameSlice();
    g_rename = .{ .target = .scene_object, .idx = idx };
    const n = @min(name.len, g_rename.buf.len);
    @memcpy(g_rename.buf[0..n], name[0..n]);
    if (n < g_rename.buf.len) g_rename.buf[n] = 0;
    g_rename.len = n;
}

/// Start renaming an asset (file).
pub fn startRenameAsset(path: []const u8) void {
    const file_name = if (std.mem.lastIndexOfScalar(u8, path, '/')) |sep|
        path[sep + 1 ..]
    else
        path;
    g_rename = .{ .target = .asset, .idx = 0 };
    const n = @min(file_name.len, g_rename.buf.len);
    @memcpy(g_rename.buf[0..n], file_name[0..n]);
    if (n < g_rename.buf.len) g_rename.buf[n] = 0;
    g_rename.len = n;
    const pn = @min(path.len, g_rename.asset_path_buf.len);
    @memcpy(g_rename.asset_path_buf[0..pn], path[0..pn]);
    g_rename.asset_path_len = pn;
}

/// Commit the current rename operation.
pub fn commitRename(now: i128, io: std.Io) void {
    switch (g_rename.target) {
        .none => {},
        .scene_object => {
            const idx = g_rename.idx;
            if (idx < object_count) {
                const new_name = g_rename.buf[0..g_rename.len];
                // Only push command if name actually changed
                if (!std.mem.eql(u8, objects[idx].nameSlice(), new_name)) {
                    var old_name: [NAME_MAX]u8 = undefined;
                    const old_len = objects[idx].name_len;
                    @memcpy(old_name[0..old_len], objects[idx].name_buf[0..old_len]);

                    objects[idx].setName(new_name);
                    scene_dirty = true;

                    var cmd: UndoCommand = .{ .rename_object = .{
                        .idx = idx,
                        .old_name = old_name,
                        .old_len = old_len,
                        .new_name = undefined,
                        .new_len = new_name.len,
                    } };
                    @memcpy(cmd.rename_object.new_name[0..new_name.len], new_name[0..new_name.len]);
                    pushCommand(now, &cmd);
                }
            }
        },

        .asset => {
            const old_path = g_rename.asset_path_buf[0..g_rename.asset_path_len];
            const old_dir = if (std.mem.lastIndexOfScalar(u8, old_path, '/')) |sep|
                old_path[0..sep]
            else
                "";
            var new_path_buf: [1024]u8 = undefined;
            const new_path = if (old_dir.len > 0)
                std.fmt.bufPrint(&new_path_buf, "{s}/{s}", .{ old_dir, g_rename.buf[0..g_rename.len] }) catch ""
            else
                g_rename.buf[0..g_rename.len];
            if (new_path.len > 0 and !std.mem.eql(u8, old_path, new_path)) {
                std.Io.Dir.rename(std.Io.Dir.cwd(), old_path, std.Io.Dir.cwd(), new_path, io) catch {};
                var old_meta_buf: [1024 + 5]u8 = undefined;
                var new_meta_buf: [1024 + 5]u8 = undefined;
                const old_meta = std.fmt.bufPrint(&old_meta_buf, "{s}.meta", .{old_path}) catch "";
                const new_meta = std.fmt.bufPrint(&new_meta_buf, "{s}.meta", .{new_path}) catch "";
                if (old_meta.len > 0 and new_meta.len > 0) {
                    std.Io.Dir.rename(std.Io.Dir.cwd(), old_meta, std.Io.Dir.cwd(), new_meta, io) catch {};
                }
                selectAsset(new_path);
                refreshComponents(io, std.heap.page_allocator);
            }
        },
    }
    cancelRename();
}

/// Cancel the current rename.
pub fn cancelRename() void {
    g_rename = .{};
}

pub fn isRenaming() bool {
    return g_rename.target != .none;
}

// ── Focus / Frame object ───────────────────────────────────────────────────────

/// Frame the camera on a specific object by updating the first camera's transform.
pub fn focusOnObject(idx: usize) void {
    if (idx >= object_count) return;
    const target_pos = objects[idx].transform.position;

    for (0..object_count) |i| {
        for (objects[i].components[0..objects[i].component_count]) |*comp| {
            if (comp.* == .camera) {
                const dist: f32 = 5.0;
                const cam_pos = objects[i].transform.position;
                const to_target = target_pos.subtract(cam_pos);
                const cur_dist = to_target.length();
                if (cur_dist > 0.001) {
                    const dir = to_target.normalize();
                    objects[i].transform.position = target_pos.subtract(dir.scale(dist));
                } else {
                    objects[i].transform.position = .{ .x = target_pos.x, .y = target_pos.y + dist, .z = target_pos.z + dist };
                }

                const new_to_target = target_pos.subtract(objects[i].transform.position);
                if (new_to_target.length() > 0.001) {
                    const fwd = new_to_target.normalize();
                    const pitch = -std.math.asin(fwd.y);
                    const yaw = std.math.atan2(fwd.x, fwd.z);
                    objects[i].transform.rotation = .{ .x = pitch * 180.0 / std.math.pi, .y = yaw * 180.0 / std.math.pi, .z = 0 };
                }
                scene_dirty = true;
                return;
            }
        }
    }
}

// ── Process context ──────────────────────────────────────────────────────────

/// General-purpose allocator set once from main() before the event loop.
pub var gpa: std.mem.Allocator = std.heap.page_allocator;
/// Environment map set once from main() before the event loop.
pub var environ_map: *const std.process.Environ.Map = undefined;

// ── Settings ──────────────────────────────────────────────────────────────────

pub var settings: editor.Settings = undefined;
var settings_initialized: bool = false;

pub fn initSettings(io: std.Io, allocator: std.mem.Allocator, global_dir: []const u8) !void {
    settings = try editor.Settings.init(allocator, global_dir, null);
    settings.load(io);
    settings_initialized = true;
}

pub fn deinitSettings(io: std.Io) void {
    if (!settings_initialized) return;
    settings.save(io);
    settings.deinit();
    settings_initialized = false;
}

pub fn settingsReady() bool {
    return settings_initialized;
}

// ── Asset Database ────────────────────────────────────────────────────────────

pub var asset_db: editor.AssetDatabase = undefined;
var asset_db_initialized: bool = false;

pub fn assetDbReady() bool {
    return asset_db_initialized;
}

// ── Scene state ───────────────────────────────────────────────────────────────

pub var objects: [MAX_OBJECTS]SceneNode = undefined;
pub var object_count: usize = 0;
pub var selected_object: ?usize = null;

pub var project_path_buf: [1024]u8 = undefined;
pub var project_path: ?[]const u8 = null;
pub var current_project: ?Project = null;

pub var discovered_components: [MAX_DISCOVERED]ComponentDef = undefined;
pub var discovered_count: usize = 0;

pub var scene_dirty: bool = false;
pub var saved_undo_depth: ?usize = 0;

pub fn markSceneSaved() void {
    saved_undo_depth = undo_len;
    scene_dirty = false;
}

var current_scene_path_buf: [1024]u8 = undefined;
pub var current_scene_path: ?[]const u8 = null;

// ── Object Clipboard (copy / cut / paste) ────────────────────────────────────

var object_clipboard: [MAX_OBJECTS]SceneNode = undefined;
var clipboard_count: usize = 0;

pub fn hasClipboard() bool {
    return clipboard_count > 0;
}

/// Copy selected objects (plus their descendants) into the in-app clipboard.
pub fn copySelectedObjects() void {
    if (selectedCount() == 0) return;

    // Mark selected and expand to descendants (forward pass)
    var in_copy = [_]bool{false} ** MAX_OBJECTS;
    for (0..object_count) |i| {
        if (isObjectSelected(i)) in_copy[i] = true;
    }
    for (0..object_count) |i| {
        if (!in_copy[i] and objects[i].parent >= 0) {
            if (in_copy[@intCast(objects[i].parent)]) in_copy[i] = true;
        }
    }

    // Build orig→clipboard index map
    var orig_to_clip: [MAX_OBJECTS]i32 = undefined;
    @memset(&orig_to_clip, -1);
    var ci: usize = 0;
    for (0..object_count) |i| {
        if (in_copy[i]) {
            orig_to_clip[i] = @intCast(ci);
            ci += 1;
        }
    }
    clipboard_count = ci;

    // Copy nodes, remapping parent indices relative to clipboard
    ci = 0;
    for (0..object_count) |i| {
        if (!in_copy[i]) continue;
        var node = objects[i];
        // Remap parent: -1 if parent is outside the selection (becomes a clipboard root)
        node.parent = if (node.parent >= 0) orig_to_clip[@intCast(node.parent)] else -1;
        object_clipboard[ci] = node;
        ci += 1;
    }
}

/// Paste clipboard objects into the scene, assigning new GUIDs.
/// Root clipboard nodes become children of `selected_object` (or scene root if none).
pub fn pasteObjects(now: i128, io: std.Io) void {
    if (clipboard_count == 0) return;
    if (object_count + clipboard_count > MAX_OBJECTS) return;

    const before = captureSnapshot();

    const insert_at = object_count;
    const offset: i32 = @intCast(insert_at);
    const paste_parent: i32 = if (selected_object) |sel| @intCast(sel) else -1;

    for (0..clipboard_count) |ci| {
        var node = object_clipboard[ci];
        var guid_buf: [36]u8 = undefined;
        node.setGuidStr(editor.Guid.v4(io).toString(&guid_buf));
        // Clipboard roots (parent == -1) attach to paste_parent; inner nodes shift by offset
        node.parent = if (node.parent < 0) paste_parent else node.parent + offset;
        objects[insert_at + ci] = node;
    }
    object_count += clipboard_count;
    scene_dirty = true;

    clearSelectedObjects();
    for (insert_at..object_count) |i| selectObject(i);
    selected_object = insert_at;

    const after = captureSnapshot();
    pushCommand(now, &.{ .add_object = .{ .before = before, .after = after } });
}

// ── Selected asset ───────────────────────────────────────────────────────────

var selected_asset_path_buf: [1024]u8 = undefined;
var selected_asset_path_len: usize = 0;
pub var selected_asset_path: ?[]const u8 = null;

pub fn selectAsset(path: []const u8) void {
    selected_object = null;
    const len = @min(path.len, selected_asset_path_buf.len);
    @memcpy(selected_asset_path_buf[0..len], path[0..len]);
    selected_asset_path_len = len;
    selected_asset_path = selected_asset_path_buf[0..len];
}

pub fn clearSelectedAsset() void {
    selected_asset_path = null;
    selected_asset_path_len = 0;
}

// ── Drag state ──────────────────────────────────────────────────────────────

pub const DragKind = enum { none, game_object, asset };

pub var drag_kind: DragKind = .none;
pub var drag_object_idx: usize = 0;
var drag_asset_path_buf: [512]u8 = undefined;
var drag_asset_path_len: usize = 0;

pub fn dragAssetPath() []const u8 {
    return drag_asset_path_buf[0..drag_asset_path_len];
}

pub fn startDragObject(idx: usize) void {
    drag_kind = .game_object;
    drag_object_idx = idx;
}

pub fn startDragAsset(path: []const u8) void {
    drag_kind = .asset;
    const len = @min(path.len, drag_asset_path_buf.len);
    @memcpy(drag_asset_path_buf[0..len], path[0..len]);
    drag_asset_path_len = len;
}

pub fn clearDrag() void {
    drag_kind = .none;
    drag_asset_path_len = 0;
}

pub fn endFrameDrag(mouse_left_held: bool) void {
    if (drag_kind != .none and !mouse_left_held) clearDrag();
}

pub fn setCurrentScenePath(path: []const u8) void {
    const len = @min(path.len, current_scene_path_buf.len);
    @memcpy(current_scene_path_buf[0..len], path[0..len]);
    current_scene_path = current_scene_path_buf[0..len];
}

pub fn clearScene() void {
    object_count = 0;
    selected_object = null;
    clearSelectedObjects();
    scene_dirty = false;
    current_scene_path = null;
    clearUndoStack();
    saved_undo_depth = 0;
}

pub fn initDefaultScene(io: std.Io) void {
    object_count = 0;
    selected_object = null;
    clearSelectedObjects();
    scene_dirty = false;
    current_scene_path = null;
    clearUndoStack();

    const env = addObject(io, "Environment", -1);

    const ground = addObject(io, "Ground", @intCast(env));
    _ = objects[ground].addComponent(.{ .mesh_renderer = .{} });
    objects[ground].transform.scale = .{ .x = 10, .y = 0.1, .z = 10 };

    _ = addObject(io, "Props", @intCast(env));

    const cam = addObject(io, "Main Camera", -1);
    _ = objects[cam].addComponent(.{ .camera = .{} });
    objects[cam].transform.position = .{ .x = 0, .y = 2, .z = -5 };

    const dir_light = addObject(io, "Directional Light", -1);
    _ = objects[dir_light].addComponent(.{ .light = .{} });
    objects[dir_light].transform.rotation = .{ .x = 50, .y = -30, .z = 0 };
}

pub fn moveObjectBefore(now: i128, src_idx: usize, before_idx: usize) void {
    if (src_idx == before_idx) return;

    const before = captureSnapshot();

    var src_obj = objects[src_idx];
    src_obj.parent = if (before_idx < object_count) objects[before_idx].parent else -1;

    const si = @as(i32, @intCast(src_idx));
    const bi = @as(i32, @intCast(before_idx));

    if (src_idx < before_idx) {
        const new_src_idx = before_idx - 1;
        const new_si = @as(i32, @intCast(new_src_idx));

        for (objects[0..object_count]) |*o| {
            if (o.parent == si) {
                o.parent = new_si;
            } else if (o.parent > si and o.parent < bi) {
                o.parent -= 1;
            }
        }

        for (src_idx..new_src_idx) |i| objects[i] = objects[i + 1];
        objects[new_src_idx] = src_obj;

        if (selected_object) |sel| {
            if (sel == src_idx) selected_object = new_src_idx else if (sel > src_idx and sel < before_idx) selected_object = sel - 1;
        }

        // Update multi-select set
        if (selected_set[src_idx]) {
            selected_set[src_idx] = false;
            selected_set[new_src_idx] = true;
        }
        for (src_idx..new_src_idx) |i| {
            if (selected_set[i + 1]) {
                selected_set[i] = true;
                selected_set[i + 1] = false;
            }
        }
    } else {
        for (objects[0..object_count]) |*o| {
            if (o.parent == si) {
                o.parent = bi;
            } else if (o.parent >= bi and o.parent < si) {
                o.parent += 1;
            }
        }

        var i: usize = src_idx;
        while (i > before_idx) : (i -= 1) objects[i] = objects[i - 1];
        objects[before_idx] = src_obj;

        if (selected_object) |sel| {
            if (sel == src_idx) selected_object = before_idx else if (sel >= before_idx and sel < src_idx) selected_object = sel + 1;
        }

        // Update multi-select set
        if (selected_set[src_idx]) {
            selected_set[src_idx] = false;
            selected_set[before_idx] = true;
        }
        var j: usize = src_idx;
        while (j > before_idx) : (j -= 1) {
            if (selected_set[j - 1]) {
                selected_set[j] = true;
                selected_set[j - 1] = false;
            }
        }
    }

    scene_dirty = true;
    const after = captureSnapshot();
    pushCommand(now, &.{ .reparent_object = .{ .before = before, .after = after } });
}

pub fn addObject(io: std.Io, n: []const u8, parent: i32) usize {
    const idx = object_count;
    object_count += 1;
    objects[idx] = .{};
    objects[idx].setName(n);
    objects[idx].parent = parent;
    var guid_buf: [36]u8 = undefined;
    objects[idx].setGuidStr(editor.Guid.v4(io).toString(&guid_buf));
    return idx;
}

pub fn addObjectWithUndo(now: i128, io: std.Io, n: []const u8, parent: i32) usize {
    const before = captureSnapshot();
    const idx = addObject(io, n, parent);
    const after = captureSnapshot();
    pushCommand(now, &.{ .add_object = .{ .before = before, .after = after } });
    scene_dirty = true;
    return idx;
}

pub fn duplicateObject(now: i128, io: std.Io, idx: usize) void {
    if (idx >= object_count) return;
    if (object_count >= MAX_OBJECTS) return;

    const before = captureSnapshot();

    var insert_at = idx + 1;
    var scan_pos = idx + 1;
    while (scan_pos < object_count) : (scan_pos += 1) {
        if (objects[scan_pos].parent >= 0) {
            var p = objects[scan_pos].parent;
            var is_child = false;
            while (p >= 0) {
                if (p == @as(i32, @intCast(idx))) {
                    is_child = true;
                    break;
                }
                p = objects[@intCast(p)].parent;
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

    if (insert_at < object_count) {
        var i = object_count;
        while (i > insert_at) : (i -= 1) {
            objects[i] = objects[i - 1];
        }
    }
    object_count += 1;

    var new_obj = objects[idx];
    var guid_buf: [36]u8 = undefined;
    new_obj.setGuidStr(editor.Guid.v4(io).toString(&guid_buf));
    {
        var name_buf: [NAME_MAX]u8 = undefined;
        const orig_name = objects[idx].nameSlice();
        const copy_name = std.fmt.bufPrint(&name_buf, "{s} (copy)", .{orig_name}) catch orig_name;
        new_obj.setName(copy_name);
    }
    objects[insert_at] = new_obj;

    selected_object = insert_at;
    clearSelectedObjects();
    selectObject(insert_at);
    scene_dirty = true;

    const after = captureSnapshot();
    pushCommand(now, &.{ .duplicate_object = .{ .before = before, .after = after } });
}

pub fn resolveAssetGuid(guid_str: []const u8) ?[]const u8 {
    if (guid_str.len == 0 or !assetDbReady()) return null;
    const guid = editor.Guid.parse(guid_str) catch return null;
    return if (asset_db.findByGuid(guid)) |info| info.path else null;
}

pub fn resolveObjectGuid(guid_str: []const u8) ?[]const u8 {
    if (guid_str.len == 0) return null;
    for (objects[0..object_count]) |*obj| {
        if (std.mem.eql(u8, obj.guidSlice(), guid_str)) return obj.nameSlice();
    }
    return null;
}

pub fn dragAssetGuidStr(buf: *[36]u8) ?[]const u8 {
    const path = dragAssetPath();
    if (path.len == 0 or !assetDbReady()) return null;
    if (asset_db.findByPath(path)) |info| return info.guid.toString(buf);
    return null;
}

pub fn setProjectPath(path: []const u8) void {
    var trimmed = path;
    while (trimmed.len > 0 and (trimmed[trimmed.len - 1] == '/' or trimmed[trimmed.len - 1] == '\\')) {
        trimmed = trimmed[0 .. trimmed.len - 1];
    }
    const len = @min(trimmed.len, project_path_buf.len);
    @memcpy(project_path_buf[0..len], trimmed[0..len]);
    project_path = project_path_buf[0..len];
}

pub fn refreshComponents(io: std.Io, allocator: std.mem.Allocator) void {
    discovered_count = 0;
    editor.scanner.populateBuiltins(&discovered_components, &discovered_count);

    if (project_path) |p| {
        var path_buf: [1024]u8 = undefined;
        const assets = std.fmt.bufPrint(&path_buf, "{s}/assets", .{p}) catch return;
        editor.scanner.scanAssetsDir(io, allocator, assets, &discovered_components, &discovered_count);

        if (asset_db_initialized) asset_db.deinit();
        asset_db = editor.AssetDatabase.init(std.heap.page_allocator);
        asset_db_initialized = true;
        asset_db.scan(io, assets);

        editor.asset_importer.importAll(io, std.heap.page_allocator, p, &asset_db, editor.Progress.none);

        // Resolve from the per-frame arena: config strings are only needed for
        // the loadFieldInfo call below and are freed when the frame ends.
        const config = editor.sdk_layout.resolveReflectionConfig(
            io,
            allocator,
            build_options.reflection_zig_path,
            build_options.engine_root_path,
        );
        editor.user_reflection.loadFieldInfo(io, &discovered_components, discovered_count, config);
    }
    // Re-sync any loaded scene so new/renamed fields appear immediately on hot-reload.
    if (object_count > 0) syncSceneWithDefinitions();
}

pub fn makeComponent(def: *const ComponentDef) ?Component {
    if (def.is_builtin) {
        return Component.fromTypeName(def.typeName());
    }
    var c = Component{ .user_script = .{} };
    c.user_script.setTypeName(def.typeName());
    c.user_script.setSourceFile(def.sourceFile());
    for (def.fields[0..def.field_count]) |*fd| {
        var fv = engine.ScriptFieldValue{};
        fv.setName(fd.nameSlice());
        fv.kind = fd.kind;
        fv.asset_filter = fd.asset_filter;
        fv.as_f32 = fd.default_f32;
        fv.as_f64 = fd.default_f64;
        fv.as_i32 = fd.default_i32;
        fv.as_i64 = fd.default_i64;
        fv.as_u32 = fd.default_u32;
        fv.as_bool = fd.default_bool;
        fv.as_vec2_x = fd.default_vec2_x;
        fv.as_vec2_y = fd.default_vec2_y;
        fv.as_vec3_x = fd.default_vec3_x;
        fv.as_vec3_y = fd.default_vec3_y;
        fv.as_vec3_z = fd.default_vec3_z;
        fv.as_vec4_x = fd.default_vec4_x;
        fv.as_vec4_y = fd.default_vec4_y;
        fv.as_vec4_z = fd.default_vec4_z;
        fv.as_vec4_w = fd.default_vec4_w;
        c.user_script.addField(fv);
    }
    return c;
}

/// Merge each loaded user_script component's fields with the current component
/// definition. The result matches definition order exactly:
///   - Fields present in both: existing saved value is kept.
///   - Fields only in definition: inserted with default values.
///   - Fields only in scene (stale): dropped silently.
/// Call after loadScene and after refreshComponents (hot-reload) to keep the
/// inspector consistent with the source.
pub fn syncSceneWithDefinitions() void {
    for (objects[0..object_count]) |*obj| {
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .user_script) continue;
            const s = &comp.user_script;
            const type_name = s.typeName();

            const def = blk: {
                for (discovered_components[0..discovered_count]) |*d| {
                    if (std.mem.eql(u8, d.typeName(), type_name)) break :blk d;
                }
                break :blk null;
            } orelse continue;

            // Rebuild field list in definition order.
            var ordered: [s.field_values.len]engine.ScriptFieldValue = undefined;
            var ordered_count: usize = 0;

            for (def.fields[0..def.field_count]) |*fd| {
                if (ordered_count >= ordered.len) break;
                const fname = fd.nameSlice();

                // Prefer an existing value with matching name AND kind.
                const existing: ?engine.ScriptFieldValue = blk: {
                    for (s.field_values[0..s.field_count]) |*fv| {
                        if (fv.kind == fd.kind and std.mem.eql(u8, fv.nameSlice(), fname))
                            break :blk fv.*;
                    }
                    break :blk null;
                };

                if (existing) |ev| {
                    ordered[ordered_count] = ev;
                } else {
                    var fv = engine.ScriptFieldValue{};
                    fv.setName(fname);
                    fv.kind = fd.kind;
                    fv.as_f32 = fd.default_f32;
                    fv.as_f64 = fd.default_f64;
                    fv.as_i32 = fd.default_i32;
                    fv.as_i64 = fd.default_i64;
                    fv.as_u32 = fd.default_u32;
                    fv.as_bool = fd.default_bool;
                    fv.as_vec2_x = fd.default_vec2_x;
                    fv.as_vec2_y = fd.default_vec2_y;
                    fv.as_vec3_x = fd.default_vec3_x;
                    fv.as_vec3_y = fd.default_vec3_y;
                    fv.as_vec3_z = fd.default_vec3_z;
                    fv.as_vec4_x = fd.default_vec4_x;
                    fv.as_vec4_y = fd.default_vec4_y;
                    fv.as_vec4_z = fd.default_vec4_z;
                    fv.as_vec4_w = fd.default_vec4_w;
                    ordered[ordered_count] = fv;
                }
                // The filter is a static property of the script type (not
                // persisted), so always refresh it from the live definition.
                ordered[ordered_count].asset_filter = fd.asset_filter;
                ordered_count += 1;
            }

            @memcpy(s.field_values[0..ordered_count], ordered[0..ordered_count]);
            s.field_count = ordered_count;
        }
    }
}

test "deleteObject subtree reindexing" {
    const allocator = std.testing.allocator;
    initUndo(allocator);
    defer clearUndoStack();

    object_count = 0;
    const io = std.Io.Threaded.global_single_threaded.io();

    // Build hierarchy to trigger double-decrement:
    // 0: Root
    // 1: A (to be deleted)
    // 2: Other (stays)
    // 3: B (to be deleted)
    // 4: Parent (stays, should move to 2)
    // 5: Child (stays, parent is 4, should move to 3, parent should become 2)

    _ = addObject(io, "Root", -1); // 0
    _ = addObject(io, "A", 0); // 1
    _ = addObject(io, "Other", 0); // 2
    _ = addObject(io, "B", 0); // 3
    _ = addObject(io, "Parent", 0); // 4
    _ = addObject(io, "Child", 4); // 5

    try std.testing.expectEqual(@as(usize, 6), object_count);

    // Delete "A" (1). This should NOT delete "B", "Parent", "Child" because they are siblings.
    // Wait, the current deleteObject deletes subtree.
    // I'll delete A (1) and B (3).

    // I'll use a group later, but for now I'll just delete them one by one or delete a parent of both.
    // Actually, I can just call deleteObject(1) then deleteObject(2) (where B moved).
    // But the bug is in a single call to deleteObject if it deletes multiple nodes.
    // A single call to deleteObject(idx) deletes idx and all its children.

    // Let's make A a parent of something else to have multiple nodes in one call.
    object_count = 0;
    _ = addObject(io, "Root", -1); // 0
    _ = addObject(io, "ParentDel", 0); // 1
    _ = addObject(io, "ChildDel", 1); // 2
    _ = addObject(io, "Other", 0); // 3
    _ = addObject(io, "TargetParent", 0); // 4
    _ = addObject(io, "TargetChild", 4); // 5

    // Delete "ParentDel" (1). Should delete 1 and 2.
    // to_remove = {2, 1}.
    deleteObject(0, 1);

    try std.testing.expectEqual(@as(usize, 4), object_count);
    // 0: Root
    // 1: Other (was 3)
    // 2: TargetParent (was 4)
    // 3: TargetChild (was 5)

    try std.testing.expectEqualStrings("Root", objects[0].nameSlice());
    try std.testing.expectEqualStrings("Other", objects[1].nameSlice());
    try std.testing.expectEqualStrings("TargetParent", objects[2].nameSlice());
    try std.testing.expectEqualStrings("TargetChild", objects[3].nameSlice());

    try std.testing.expectEqual(@as(i32, -1), objects[0].parent);
    try std.testing.expectEqual(@as(i32, 0), objects[1].parent);
    try std.testing.expectEqual(@as(i32, 0), objects[2].parent);
    try std.testing.expectEqual(@as(i32, 2), objects[3].parent);
}
