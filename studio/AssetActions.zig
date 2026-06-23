//! Asset file actions for the asset browser (issue #44 item 7): opening assets
//! in external tools, revealing them in the OS file manager, and creating new
//! assets of each kind. Split out of AssetBrowser.zig to keep that file focused
//! on browsing/navigation UI.

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const ProjectOps = @import("ProjectOps.zig");

/// Open `file_name` (inside `browse_path`) in the OS-default external editor.
pub fn openExternal(browse_path: []const u8, file_name: []const u8) void {
    var path_buf: [1024]u8 = undefined;
    const path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return;
    const builtin = @import("builtin");
    if (comptime builtin.os.tag == .windows) {
        const argv = [_][]const u8{ "cmd.exe", "/c", "start", "", path };
        _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
    } else if (comptime builtin.os.tag == .macos) {
        const argv = [_][]const u8{ "open", path };
        _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
    } else {
        const argv = [_][]const u8{ "xdg-open", path };
        _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
    }
}

/// Reveal `file_name` (or `browse_path` itself when empty) in the OS file manager.
pub fn revealInFileManager(browse_path: []const u8, file_name: []const u8) void {
    const builtin = @import("builtin");
    if (comptime builtin.os.tag == .windows) {
        if (file_name.len > 0) {
            var path_buf: [1024]u8 = undefined;
            const path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return;
            const argv = [_][]const u8{ "explorer.exe", "/select,", path };
            _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
        } else {
            const argv = [_][]const u8{ "explorer.exe", browse_path };
            _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
        }
    } else if (comptime builtin.os.tag == .macos) {
        if (file_name.len > 0) {
            var path_buf: [1024]u8 = undefined;
            const path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return;
            const argv = [_][]const u8{ "open", "-R", path };
            _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
        } else {
            const argv = [_][]const u8{ "open", browse_path };
            _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
        }
    } else {
        const argv = [_][]const u8{ "xdg-open", browse_path };
        _ = std.process.spawn(gui.io, .{ .argv = &argv }) catch return;
    }
}

/// Find the first non-colliding name `base`/`base_N` (with `ext`) inside
/// `browse_path` and return the full path. Returns null if 100 names collide
/// or the path overflows. Writes into the caller-supplied buffers.
fn uniquePath(
    browse_path: []const u8,
    base: []const u8,
    ext: []const u8,
    name_buf: []u8,
    path_buf: []u8,
) ?[]const u8 {
    var n: usize = 0;
    while (n < 100) : (n += 1) {
        const file_name = if (n == 0)
            std.fmt.bufPrint(name_buf, "{s}.{s}", .{ base, ext }) catch return null
        else
            std.fmt.bufPrint(name_buf, "{s}_{d}.{s}", .{ base, n, ext }) catch return null;
        const full_path = std.fmt.bufPrint(path_buf, "{s}/{s}", .{ browse_path, file_name }) catch return null;
        const exists = blk: {
            _ = std.Io.Dir.cwd().openFile(gui.io, full_path, .{}) catch break :blk false;
            break :blk true;
        };
        if (!exists) return full_path;
    }
    return null;
}

/// Refresh the asset list and select the freshly created asset.
fn finishCreate(full_path: []const u8) void {
    EditorState.refreshComponents(gui.io, gui.currentWindow().arena());
    EditorState.selectAsset(full_path);
}

/// Create a new empty prefab (serialized scene-hierarchy asset) in `browse_path`.
pub fn createNewPrefab(browse_path: []const u8) void {
    var name_buf: [192]u8 = undefined;
    var path_buf: [1024]u8 = undefined;
    const full_path = uniquePath(browse_path, "new_prefab", "prefab", &name_buf, &path_buf) orelse return;
    ProjectOps.saveScene(full_path);
    finishCreate(full_path);
}

pub fn createNewMaterialFromPreset(browse_path: []const u8, preset: engine.Material.Preset) void {
    var name_buf: [192]u8 = undefined;
    var path_buf: [1024]u8 = undefined;
    const full_path = uniquePath(browse_path, "new_material", "material", &name_buf, &path_buf) orelse return;
    engine.Material.savePreset(preset, engine.shader.default(), gui.io, full_path) catch return;
    finishCreate(full_path);
}

pub fn createNewInputActions(browse_path: []const u8) void {
    var name_buf: [192]u8 = undefined;
    var path_buf: [1024]u8 = undefined;
    const full_path = uniquePath(browse_path, "input", "inputactions", &name_buf, &path_buf) orelse return;
    const default_ia = engine.InputActions{
        .version = engine.InputActions.CURRENT_VERSION,
        .actions = &.{
            .{ .name = "jump", .kind = .button, .pos = &.{.{ .device = .key, .code = "space" }} },
        },
    };
    default_ia.save(gui.io, full_path) catch return;
    finishCreate(full_path);
}

pub fn createNewProjectSettings(browse_path: []const u8) void {
    var name_buf: [192]u8 = undefined;
    var path_buf: [1024]u8 = undefined;
    const full_path = uniquePath(browse_path, "project", "projectsettings", &name_buf, &path_buf) orelse return;
    const name = if (EditorState.current_project) |*p| p.nameSlice() else "Untitled";
    const default_ps = engine.ProjectSettings{ .project = .{ .name = name } };
    default_ps.save(gui.io, full_path) catch return;
    finishCreate(full_path);
}

pub fn createNewDataAsset(browse_path: []const u8, def: *const editor.ComponentDef) void {
    const type_name = def.typeName();
    var lc_buf: [128]u8 = undefined;
    const tl = @min(type_name.len, lc_buf.len);
    for (type_name[0..tl], 0..) |c, i| lc_buf[i] = std.ascii.toLower(c);
    const lc_name = lc_buf[0..tl];

    var name_buf: [192]u8 = undefined;
    var path_buf: [1024]u8 = undefined;
    const full_path = uniquePath(browse_path, lc_name, "asset", &name_buf, &path_buf) orelse return;
    const file = editor.data_asset_io.defaultFromDef(def);
    editor.data_asset_io.save(gui.io, full_path, file) catch return;
    finishCreate(full_path);
}
