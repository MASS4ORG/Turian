//! Open-document JSON persistence — split out of `Documents.zig` to keep that
//! file to the document model itself. Built entirely on `Documents.zig`'s
//! `pub` API (`count`, `activeIndex`, `docAt`, `openScene`, `openAsset`,
//! `activate`, `closeAll`); it never touches that file's private `docs` array.

const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const Documents = @import("Documents.zig");

const OPEN_KEY = "editor.open_documents";

/// True while `restore` is replaying tabs, so the per-open `persist` calls
/// don't clobber the settings value we're still reading from.
var restoring: bool = false;

/// Persist the open-document list (project-relative paths) + active index into
/// settings, scoped to the current project. Saved to disk on editor shutdown.
pub fn persist() void {
    if (restoring) return;
    if (!EditorState.settingsReady()) return;
    const proj = EditorState.project_path orelse return;

    const a = EditorState.gpa;
    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(a);

    out.appendSlice(a, "{\"project\":") catch return;
    writeJsonString(a, &out, proj) catch return;
    var num_buf: [32]u8 = undefined;
    const num = std.fmt.bufPrint(&num_buf, ",\"active\":{d},\"docs\":[", .{Documents.activeIndex() orelse 0}) catch return;
    out.appendSlice(a, num) catch return;
    const doc_count = Documents.count();
    for (0..doc_count) |i| {
        if (i > 0) out.appendSlice(a, ",") catch return;
        writeJsonString(a, &out, relativeTo(proj, Documents.docAt(i).path())) catch return;
    }
    out.appendSlice(a, "]}") catch return;

    EditorState.settings.set(OPEN_KEY, out.items) catch {};
}

/// Restore the previously-open tabs for the just-opened project. Called from
/// `ProjectOps.openProject` after the project's component registry is ready.
pub fn restore() void {
    Documents.closeAll();
    if (!EditorState.settingsReady()) return;
    const proj = EditorState.project_path orelse return;

    const raw_ref = EditorState.settings.get(OPEN_KEY) orelse return;
    const arena = gui.currentWindow().arena();
    // Copy out of settings memory: opening tabs below triggers settings writes
    // that can invalidate `raw_ref` (and JSON strings reference their source).
    const raw = arena.dupe(u8, raw_ref) catch return;
    const parsed = std.json.parseFromSlice(std.json.Value, arena, raw, .{}) catch return;

    restoring = true;
    defer {
        restoring = false;
        persist();
    }
    if (parsed.value != .object) return;
    const obj = parsed.value.object;

    // Only restore tabs that belong to the project being opened.
    const saved_proj = obj.get("project") orelse return;
    if (saved_proj != .string or !std.mem.eql(u8, saved_proj.string, proj)) return;

    const docs_val = obj.get("docs") orelse return;
    if (docs_val != .array) return;

    for (docs_val.array.items) |item| {
        if (item != .string) continue;
        var path_buf: [1024]u8 = undefined;
        const full = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ proj, item.string }) catch continue;
        if (!fileExists(full)) continue;
        const at = editor.asset_registry.lookupByFilename(full);
        if (at == .scene) Documents.openScene(full) else Documents.openAsset(full, at);
    }

    if (obj.get("active")) |av| {
        if (av == .integer) {
            const ai: usize = @intCast(@max(0, av.integer));
            if (ai < Documents.count()) Documents.activate(ai);
        }
    }
}

fn fileExists(full: []const u8) bool {
    var f = std.Io.Dir.cwd().openFile(gui.io, full, .{}) catch return false;
    f.close(gui.io);
    return true;
}

/// Strip the `<proj>/` prefix from `full`, yielding a project-relative path.
fn relativeTo(proj: []const u8, full: []const u8) []const u8 {
    if (full.len > proj.len + 1 and std.mem.startsWith(u8, full, proj) and full[proj.len] == '/') {
        return full[proj.len + 1 ..];
    }
    return full;
}

fn writeJsonString(a: std.mem.Allocator, out: *std.ArrayList(u8), s: []const u8) !void {
    try out.append(a, '"');
    for (s) |c| switch (c) {
        '"' => try out.appendSlice(a, "\\\""),
        '\\' => try out.appendSlice(a, "\\\\"),
        '\n' => try out.appendSlice(a, "\\n"),
        '\r' => try out.appendSlice(a, "\\r"),
        else => try out.append(a, c),
    };
    try out.append(a, '"');
}
