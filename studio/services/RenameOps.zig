const std = @import("std");
const State = @import("State.zig");
const EditorState = @import("EditorState.zig");
const UndoRedo = @import("UndoRedo.zig");

pub const RenameTarget = enum { none, scene_object, asset };

pub const RenameState = struct {
    target: RenameTarget = .none,
    idx: usize = 0,
    buf: [256]u8 = undefined,
    len: usize = 0,
    asset_path_buf: [1024]u8 = undefined,
    asset_path_len: usize = 0,
    just_started: bool = false,
};

pub fn startRenameObject(idx: usize) void {
    if (idx >= EditorState.object_count) return;
    const name = EditorState.objects[idx].nameSlice();
    EditorState.g_rename = .{ .target = .scene_object, .idx = idx, .just_started = true };
    const n = @min(name.len, EditorState.g_rename.buf.len);
    @memcpy(EditorState.g_rename.buf[0..n], name[0..n]);
    if (n < EditorState.g_rename.buf.len) EditorState.g_rename.buf[n] = 0;
    EditorState.g_rename.len = n;
}

pub fn startRenameAsset(path: []const u8) void {
    const file_name = if (std.mem.lastIndexOfScalar(u8, path, '/')) |sep|
        path[sep + 1 ..]
    else
        path;
    EditorState.g_rename = .{ .target = .asset, .idx = 0, .just_started = true };
    const n = @min(file_name.len, EditorState.g_rename.buf.len);
    @memcpy(EditorState.g_rename.buf[0..n], file_name[0..n]);
    if (n < EditorState.g_rename.buf.len) EditorState.g_rename.buf[n] = 0;
    EditorState.g_rename.len = n;
    const pn = @min(path.len, EditorState.g_rename.asset_path_buf.len);
    @memcpy(EditorState.g_rename.asset_path_buf[0..pn], path[0..pn]);
    EditorState.g_rename.asset_path_len = pn;
}

pub fn commitRename(now: i128, io: std.Io) void {
    const AssetResolution = @import("AssetResolution.zig");
    switch (EditorState.g_rename.target) {
        .none => {},
        .scene_object => {
            const idx = EditorState.g_rename.idx;
            if (idx < EditorState.object_count) {
                const new_name = EditorState.g_rename.buf[0..EditorState.g_rename.len];
                if (!std.mem.eql(u8, EditorState.objects[idx].nameSlice(), new_name)) {
                    var old_name: [State.NAME_MAX]u8 = undefined;
                    const old_len = EditorState.objects[idx].name_len;
                    @memcpy(old_name[0..old_len], EditorState.objects[idx].name_buf[0..old_len]);

                    EditorState.objects[idx].setName(new_name);
                    EditorState.scene_dirty = true;

                    var cmd: UndoRedo.UndoCommand = .{ .rename_object = .{
                        .idx = idx,
                        .old_name = old_name,
                        .old_len = old_len,
                        .new_name = undefined,
                        .new_len = new_name.len,
                    } };
                    @memcpy(cmd.rename_object.new_name[0..new_name.len], new_name[0..new_name.len]);
                    UndoRedo.pushCommand(now, &cmd);
                }
            }
        },
        .asset => {
            const old_path = EditorState.g_rename.asset_path_buf[0..EditorState.g_rename.asset_path_len];
            const old_dir = if (std.mem.lastIndexOfScalar(u8, old_path, '/')) |sep|
                old_path[0..sep]
            else
                "";
            var new_path_buf: [1024]u8 = undefined;
            const new_path = if (old_dir.len > 0)
                std.fmt.bufPrint(&new_path_buf, "{s}/{s}", .{ old_dir, EditorState.g_rename.buf[0..EditorState.g_rename.len] }) catch ""
            else
                EditorState.g_rename.buf[0..EditorState.g_rename.len];
            if (new_path.len > 0 and !std.mem.eql(u8, old_path, new_path)) {
                std.Io.Dir.rename(std.Io.Dir.cwd(), old_path, std.Io.Dir.cwd(), new_path, io) catch {};
                var old_meta_buf: [1024 + 5]u8 = undefined;
                var new_meta_buf: [1024 + 5]u8 = undefined;
                const old_meta = std.fmt.bufPrint(&old_meta_buf, "{s}.meta", .{old_path}) catch "";
                const new_meta = std.fmt.bufPrint(&new_meta_buf, "{s}.meta", .{new_path}) catch "";
                if (old_meta.len > 0 and new_meta.len > 0) {
                    std.Io.Dir.rename(std.Io.Dir.cwd(), old_meta, std.Io.Dir.cwd(), new_meta, io) catch {};
                }
                State.selectAsset(new_path);
                AssetResolution.refreshComponents(io, std.heap.page_allocator);
            }
        },
    }
    cancelRename();
}

pub fn cancelRename() void {
    EditorState.g_rename = .{};
}

pub fn isRenaming() bool {
    return EditorState.g_rename.target != .none;
}
