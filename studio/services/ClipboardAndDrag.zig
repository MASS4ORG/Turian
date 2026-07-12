const std = @import("std");
const editor = @import("editor");
const State = @import("State.zig");
const EditorState = @import("EditorState.zig");
const UndoRedo = @import("UndoRedo.zig");
const Selection = @import("Selection.zig");

var object_clipboard: [State.MAX_OBJECTS]State.SceneNode = undefined;
var clipboard_count: usize = 0;

pub fn hasClipboard() bool {
    return clipboard_count > 0;
}

pub fn copySelectedObjects() void {
    if (Selection.selectedCount() == 0) return;

    var in_copy = [_]bool{false} ** State.MAX_OBJECTS;
    for (0..EditorState.object_count) |i| {
        if (Selection.isObjectSelected(i)) in_copy[i] = true;
    }
    for (0..EditorState.object_count) |i| {
        if (!in_copy[i] and EditorState.objects[i].parent >= 0) {
            if (in_copy[@intCast(EditorState.objects[i].parent)]) in_copy[i] = true;
        }
    }

    var orig_to_clip: [State.MAX_OBJECTS]i32 = undefined;
    @memset(&orig_to_clip, -1);
    var ci: usize = 0;
    for (0..EditorState.object_count) |i| {
        if (in_copy[i]) {
            orig_to_clip[i] = @intCast(ci);
            ci += 1;
        }
    }
    clipboard_count = ci;

    ci = 0;
    for (0..EditorState.object_count) |i| {
        if (!in_copy[i]) continue;
        var node = EditorState.objects[i];
        node.parent = if (node.parent >= 0) orig_to_clip[@intCast(node.parent)] else -1;
        object_clipboard[ci] = node;
        ci += 1;
    }
}

fn captureSnapshot() UndoRedo.Snapshot {
    const buf = UndoRedo.undo_alloc.alloc(State.SceneNode, EditorState.object_count) catch return .{
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

pub fn pasteObjects(now: i128, io: std.Io) void {
    if (clipboard_count == 0) return;
    if (EditorState.object_count + clipboard_count > State.MAX_OBJECTS) return;

    const before = captureSnapshot();

    const insert_at = EditorState.object_count;
    const offset: i32 = @intCast(insert_at);
    const paste_parent: i32 = if (EditorState.selected_object) |sel| @intCast(sel) else -1;

    for (0..clipboard_count) |ci| {
        var node = object_clipboard[ci];
        var guid_buf: [36]u8 = undefined;
        node.setGuidStr(editor.Guid.v4(io).toString(&guid_buf));
        node.parent = if (node.parent < 0) paste_parent else node.parent + offset;
        EditorState.objects[insert_at + ci] = node;
    }
    EditorState.object_count += clipboard_count;
    EditorState.scene_dirty = true;

    Selection.clearSelectedObjects();
    for (insert_at..EditorState.object_count) |i| Selection.selectObject(i);
    EditorState.selected_object = insert_at;

    const after = captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .add_object = .{ .before = before, .after = after } });
}

pub const DragKind = enum { none, game_object, asset };

pub fn dragAssetPath() []const u8 {
    return EditorState.drag_asset_path_buf[0..EditorState.drag_asset_path_len];
}

pub fn startDragObject(idx: usize) void {
    EditorState.drag_kind = .game_object;
    EditorState.drag_object_idx = idx;
}

pub fn startDragAsset(path: []const u8) void {
    EditorState.drag_kind = .asset;
    const len = @min(path.len, EditorState.drag_asset_path_buf.len);
    @memcpy(EditorState.drag_asset_path_buf[0..len], path[0..len]);
    EditorState.drag_asset_path_len = len;
}

pub fn clearDrag() void {
    EditorState.drag_kind = .none;
    EditorState.drag_asset_path_len = 0;
}

pub fn endFrameDrag(mouse_left_held: bool) void {
    if (EditorState.drag_kind != .none and !mouse_left_held) clearDrag();
}
