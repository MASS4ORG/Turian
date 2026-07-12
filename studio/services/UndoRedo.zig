const std = @import("std");
const State = @import("State.zig");
const EditorState = @import("EditorState.zig");

pub const MAX_UNDO = 50;

pub var undo_alloc: std.mem.Allocator = std.heap.page_allocator;

pub fn initUndo(allocator: std.mem.Allocator) void {
    undo_alloc = allocator;
}

pub const Snapshot = struct {
    objects: []State.SceneNode,
    object_count: usize,
    selected_object: ?usize,
};

pub fn captureSnapshot() Snapshot {
    const buf = undo_alloc.alloc(State.SceneNode, EditorState.object_count) catch return .{
        .objects = &[_]State.SceneNode{},
        .object_count = 0,
        .selected_object = null,
    };
    @memcpy(buf, EditorState.objects[0..EditorState.object_count]);
    return .{
        .objects = buf,
        .object_count = EditorState.object_count,
        .selected_object = EditorState.selected_object,
    };
}

pub fn restoreSnapshot(s: Snapshot) void {
    if (s.object_count > State.MAX_OBJECTS) return;
    @memcpy(EditorState.objects[0..s.object_count], s.objects);
    EditorState.object_count = s.object_count;
    EditorState.selected_object = s.selected_object;
}

pub fn freeSnapshot(s: Snapshot) void {
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
        before: State.SceneNode,
        after: State.SceneNode,
    },
    rename_object: struct {
        idx: usize,
        old_name: [State.NAME_MAX]u8,
        old_len: usize,
        new_name: [State.NAME_MAX]u8,
        new_len: usize,
    },
    duplicate_object: struct {
        before: Snapshot,
        after: Snapshot,
    },
    add_component: struct {
        obj_idx: usize,
        comp: State.Component,
        ins_idx: usize,
    },
    remove_component: struct {
        obj_idx: usize,
        comp: State.Component,
        rem_idx: usize,
    },
    add_object: struct {
        before: Snapshot,
        after: Snapshot,
    },
    prefab_op: struct {
        before: Snapshot,
        after: Snapshot,
    },
    group: struct {
        items: []UndoCommand,
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
            .prefab_op => "Prefab",
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
            .prefab_op => |cmd| {
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
                EditorState.objects[cmd.idx] = cmd.before;
            },
            .rename_object => |cmd| {
                const obj = &EditorState.objects[cmd.idx];
                obj.name_len = cmd.old_len;
                @memcpy(obj.name_buf[0..cmd.old_len], cmd.old_name[0..cmd.old_len]);
            },
            .duplicate_object => |cmd| {
                restoreSnapshot(cmd.before);
            },
            .add_component => |cmd| {
                EditorState.objects[cmd.obj_idx].removeComponent(cmd.ins_idx);
            },
            .remove_component => |cmd| {
                const obj = &EditorState.objects[cmd.obj_idx];
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
            .prefab_op => |cmd| {
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
        EditorState.scene_dirty = true;
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
                EditorState.objects[cmd.idx] = cmd.after;
            },
            .rename_object => |cmd| {
                const obj = &EditorState.objects[cmd.idx];
                obj.name_len = cmd.new_len;
                @memcpy(obj.name_buf[0..cmd.new_len], cmd.new_name[0..cmd.new_len]);
            },
            .duplicate_object => |cmd| {
                restoreSnapshot(cmd.after);
            },
            .add_component => |cmd| {
                _ = EditorState.objects[cmd.obj_idx].addComponent(cmd.comp);
            },
            .remove_component => |cmd| {
                EditorState.objects[cmd.obj_idx].removeComponent(cmd.rem_idx);
            },
            .add_object => |cmd| {
                restoreSnapshot(cmd.after);
            },
            .prefab_op => |cmd| {
                restoreSnapshot(cmd.after);
            },
            .group => |cmd| {
                for (cmd.items) |*item| item.execute();
            },
        }
        EditorState.scene_dirty = true;
    }
};

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
pub var undo_len: usize = 0;
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
    group_buffer = null;
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

    if (undo_len > 0) {
        const can_merge = now - last_push_ns < 500 * std.time.ns_per_ms;
        if (can_merge and tryMergeCommands(&undo_stack[undo_len - 1], cmd)) {
            var mutable_cmd = cmd.*;
            mutable_cmd.deinit();

            for (0..redo_len) |i| redo_stack[i].deinit();
            redo_len = 0;
            return;
        }
    }

    for (0..redo_len) |i| redo_stack[i].deinit();
    redo_len = 0;

    if (EditorState.saved_undo_depth) |saved| {
        if (saved > undo_len) {
            EditorState.saved_undo_depth = null;
        }
    }

    if (undo_len >= MAX_UNDO) {
        undo_stack[0].deinit();
        for (0..MAX_UNDO - 1) |i| undo_stack[i] = undo_stack[i + 1];
        undo_len = MAX_UNDO - 1;

        if (EditorState.saved_undo_depth) |saved| {
            if (saved == 0) {
                EditorState.saved_undo_depth = null;
            } else {
                EditorState.saved_undo_depth = saved - 1;
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

    EditorState.scene_dirty = (EditorState.saved_undo_depth == null or undo_len != EditorState.saved_undo_depth.?);
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
    EditorState.scene_dirty = (EditorState.saved_undo_depth == null or undo_len != EditorState.saved_undo_depth.?);
}

pub fn redo() void {
    if (redo_len == 0) return;
    redo_len -= 1;

    if (undo_len >= MAX_UNDO) {
        undo_stack[0].deinit();
        for (0..MAX_UNDO - 1) |i| undo_stack[i] = undo_stack[i + 1];
        undo_len = MAX_UNDO - 1;

        if (EditorState.saved_undo_depth) |saved| {
            if (saved == 0) {
                EditorState.saved_undo_depth = null;
            } else {
                EditorState.saved_undo_depth = saved - 1;
            }
        }
    }
    undo_stack[undo_len] = redo_stack[redo_len];
    undo_len += 1;

    redo_stack[redo_len].execute();
    EditorState.scene_dirty = (EditorState.saved_undo_depth == null or undo_len != EditorState.saved_undo_depth.?);
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
    last_modified_idx = null;
    if (group_buffer) |*gb| {
        for (gb.items) |*cmd| cmd.deinit();
        gb.deinit(undo_alloc);
        group_buffer = null;
    }
    EditorState.saved_undo_depth = 0;
    EditorState.scene_dirty = false;
}
