//! Dedicated editor panel for `.projectsettings` assets.
//!
//! Edits the game/project configuration DataAsset (`engine.ProjectSettings`):
//! project metadata, graphics options, platform options, and the boot scene.
//! Save writes ZON back to the asset and re-cooks it. Mirrors the structure of
//! `InputActionsEditor` / `MaterialEditor` (module-level loaded state + Save row).

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("../../services/EditorState.zig");
const StudioLocale = @import("../../services/StudioLocale.zig");
const tr = StudioLocale.tr;

const ProjectSettings = engine.ProjectSettings;
const Quality = ProjectSettings.Graphics.Quality;
const Target = ProjectSettings.Platform.Target;
const Optimize = ProjectSettings.Platform.Optimize;

// ── Loaded state ───────────────────────────────────────────────────────────────

var loaded_path_buf: [1024]u8 = undefined;
var loaded_path_len: usize = 0;
var dirty: bool = false;

var name_buf: [128]u8 = .{0} ** 128;
var company_buf: [128]u8 = .{0} ** 128;
var version_buf: [32]u8 = .{0} ** 32;
var icon_buf: [40]u8 = .{0} ** 40;
var scene_buf: [40]u8 = .{0} ** 40;
var ui_theme_buf: [40]u8 = .{0} ** 40;
var width_buf: [12]u8 = .{0} ** 12;
var height_buf: [12]u8 = .{0} ** 12;
var vsync: bool = true;
var fullscreen: bool = false;
var quality: Quality = .high;
var target: Target = .auto;
var optimize: Optimize = .debug;

fn loadedPath() []const u8 {
    return loaded_path_buf[0..loaded_path_len];
}

fn bufStr(b: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, b, 0) orelse b.len;
    return b[0..end];
}

fn setBuf(dst: []u8, s: []const u8) void {
    const n = @min(s.len, dst.len - 1);
    @memcpy(dst[0..n], s[0..n]);
    @memset(dst[n..], 0);
}

// ── Draw ───────────────────────────────────────────────────────────────────────

/// Draw the editor for the ProjectSettings asset at `asset_path`. Loads (or
/// reloads) when the selected asset changes.
pub fn draw(asset_path: []const u8) void {
    if (!std.mem.eql(u8, asset_path, loadedPath())) load(asset_path);

    section(tr("Project"));
    textRow(tr("Name"), &name_buf, 1);
    textRow(tr("Company"), &company_buf, 2);
    textRow(tr("Version"), &version_buf, 3);
    textRow(tr("Icon (GUID)"), &icon_buf, 4);

    section(tr("Graphics"));
    textRow(tr("Width"), &width_buf, 5);
    textRow(tr("Height"), &height_buf, 6);
    checkRow(tr("VSync"), &vsync, 7);
    checkRow(tr("Fullscreen"), &fullscreen, 8);
    enumRow(Quality, tr("Quality"), &quality, 9);

    section(tr("Platform"));
    enumRow(Target, tr("Target"), &target, 10);
    enumRow(Optimize, tr("Optimize"), &optimize, 11);

    section(tr("Boot"));
    textRow(tr("First Scene (GUID)"), &scene_buf, 12);
    textRow(tr("UI Theme (GUID)"), &ui_theme_buf, 13);

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 9100 });

    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .all(6) });
        defer row.deinit();
        if (dirty)
            gui.label(@src(), "{s}", .{tr("Unsaved changes")}, .{ .gravity_y = 0.5, .expand = .horizontal })
        else
            gui.label(@src(), "{s}", .{tr("Saved")}, .{ .gravity_y = 0.5, .expand = .horizontal });
        if (gui.button(@src(), tr("Save"), .{}, .{ .gravity_y = 0.5, .style = if (dirty) .highlight else .control })) {
            save();
        }
    }
}

fn section(title: []const u8) void {
    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = @intFromPtr(title.ptr) });
    gui.label(@src(), "{s}", .{title}, .{
        .id_extra = @intFromPtr(title.ptr),
        .padding = .{ .x = 6, .y = 6 },
    });
}

fn textRow(label: []const u8, buf: []u8, id: usize) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 2 }, .id_extra = id });
    defer row.deinit();

    gui.label(@src(), "{s}", .{label}, .{ .id_extra = id, .gravity_y = 0.5, .min_size_content = .{ .w = 140 } });

    var te = gui.textEntry(@src(), .{ .text = .{ .buffer = buf } }, .{
        .id_extra = id,
        .gravity_y = 0.5,
        .expand = .horizontal,
    });
    const changed = te.text_changed;
    te.deinit();
    if (changed) dirty = true;
}

fn checkRow(label: []const u8, value: *bool, id: usize) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 2 }, .id_extra = id });
    defer row.deinit();

    gui.label(@src(), "{s}", .{label}, .{ .id_extra = id, .gravity_y = 0.5, .min_size_content = .{ .w = 140 } });

    const before = value.*;
    _ = gui.checkbox(@src(), value, "", .{ .id_extra = id, .gravity_y = 0.5 });
    if (value.* != before) dirty = true;
}

fn enumRow(comptime T: type, label: []const u8, value: *T, id: usize) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 2 }, .id_extra = id });
    defer row.deinit();

    gui.label(@src(), "{s}", .{label}, .{ .id_extra = id, .gravity_y = 0.5, .min_size_content = .{ .w = 140 } });

    if (gui.dropdownEnum(@src(), T, .{ .choice = value }, .{}, .{
        .id_extra = id,
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 120 },
    })) dirty = true;
}

// ── Load / Save ────────────────────────────────────────────────────────────────

fn load(asset_path: []const u8) void {
    setBuf(loaded_path_buf[0..], asset_path);
    loaded_path_len = @min(asset_path.len, loaded_path_buf.len - 1);
    dirty = false;

    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    // Start from defaults so a malformed/empty file still yields a usable form.
    const def = ProjectSettings{};
    var ps = def;
    if (std.Io.Dir.cwd().readFileAlloc(gui.io, asset_path, arena, .unlimited)) |bytes| {
        if (ProjectSettings.loadFromBytes(arena, bytes)) |parsed| {
            ps = parsed;
        } else |_| {}
    } else |_| {}

    setBuf(&name_buf, ps.project.name);
    setBuf(&company_buf, ps.project.company);
    setBuf(&version_buf, ps.project.version);
    setBuf(&icon_buf, ps.project.icon);
    setBuf(&scene_buf, ps.first_scene);
    setBuf(&ui_theme_buf, ps.ui_theme);

    var wbuf: [12]u8 = undefined;
    setBuf(&width_buf, std.fmt.bufPrint(&wbuf, "{d}", .{ps.graphics.width}) catch "1280");
    var hbuf: [12]u8 = undefined;
    setBuf(&height_buf, std.fmt.bufPrint(&hbuf, "{d}", .{ps.graphics.height}) catch "720");
    vsync = ps.graphics.vsync;
    fullscreen = ps.graphics.fullscreen;
    quality = ps.graphics.quality;
    target = ps.platform.target;
    optimize = ps.platform.optimize;
}

fn save() void {
    const width = std.fmt.parseInt(u32, bufStr(&width_buf), 10) catch 1280;
    const height = std.fmt.parseInt(u32, bufStr(&height_buf), 10) catch 720;

    const ps = ProjectSettings{
        .version = ProjectSettings.CURRENT_VERSION,
        .project = .{
            .name = bufStr(&name_buf),
            .company = bufStr(&company_buf),
            .version = bufStr(&version_buf),
            .icon = bufStr(&icon_buf),
        },
        .graphics = .{
            .width = width,
            .height = height,
            .vsync = vsync,
            .fullscreen = fullscreen,
            .quality = quality,
        },
        .platform = .{ .target = target, .optimize = optimize },
        .first_scene = bufStr(&scene_buf),
        .ui_theme = bufStr(&ui_theme_buf),
    };

    ps.save(gui.io, loadedPath()) catch return;
    dirty = false;

    // Keep the cached artifact in sync with the freshly written source.
    if (EditorState.project_path) |proj| {
        editor.asset_importer.importAssetForce(gui.io, gui.currentWindow().arena(), proj, loadedPath());
    }
}
