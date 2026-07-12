const std = @import("std");
const State = @import("State.zig");
const EditorState = @import("EditorState.zig");

pub fn isObjectSelected(idx: usize) bool {
    if (idx >= State.MAX_OBJECTS) return false;
    return EditorState.selected_set[idx];
}

pub fn selectObject(idx: usize) void {
    if (idx >= State.MAX_OBJECTS) return;
    EditorState.selected_set[idx] = true;
    EditorState.last_select_idx = idx;
}

pub fn deselectObject(idx: usize) void {
    if (idx >= State.MAX_OBJECTS) return;
    EditorState.selected_set[idx] = false;
    if (EditorState.last_select_idx == idx) EditorState.last_select_idx = null;
}

pub fn toggleSelectObject(idx: usize) void {
    if (idx >= State.MAX_OBJECTS) return;
    EditorState.selected_set[idx] = !EditorState.selected_set[idx];
    if (EditorState.selected_set[idx]) EditorState.last_select_idx = idx;
}

pub fn clearSelectedObjects() void {
    @memset(EditorState.selected_set[0..State.MAX_OBJECTS], false);
    EditorState.last_select_idx = null;
}

pub fn selectObjectRange(from: usize, to: usize) void {
    const start = @min(from, to);
    const end = @max(from, to);
    for (start..end + 1) |i| {
        if (i < State.MAX_OBJECTS) EditorState.selected_set[i] = true;
    }
    EditorState.last_select_idx = to;
}

pub fn selectedCount() usize {
    var count: usize = 0;
    for (EditorState.selected_set[0..State.MAX_OBJECTS]) |s| {
        if (s) count += 1;
    }
    return count;
}
