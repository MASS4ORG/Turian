//! Inspector panel for an asset's import settings, shown for image
//! and model assets. Edits live in `<asset>.meta`; Save writes the meta and
//! re-cooks the asset. Mirrors `ProjectSettingsEditor`'s loaded-state + Save row.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");

// ── Loaded state ───────────────────────────────────────────────────────────────

var loaded_path_buf: [1024]u8 = undefined;
var loaded_path_len: usize = 0;
var loaded_type: editor.AssetType = .unknown;
var dirty: bool = false;

// Image fields.
var img: editor.ImageImportSettings = .{};
var max_size_buf: [12]u8 = .{0} ** 12;

// Model fields.
var model: editor.ModelImportSettings = .{};
var scale_buf: [16]u8 = .{0} ** 16;

// Font fields.
var font: editor.FontImportSettings = .{};
var default_size_buf: [12]u8 = .{0} ** 12;

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

/// Returns true if `asset_type` has editable import settings.
pub fn handles(asset_type: editor.AssetType) bool {
    return asset_type == .image or asset_type == .model or asset_type == .font;
}

// ── Draw ───────────────────────────────────────────────────────────────────────

pub fn draw(asset_path: []const u8, asset_type: editor.AssetType) void {
    if (!std.mem.eql(u8, asset_path, loadedPath()) or asset_type != loaded_type)
        load(asset_path, asset_type);

    section("Import Settings");

    switch (loaded_type) {
        .image => {
            enumRow(editor.TextureType, "Texture Type", &img.texture_type, 1);
            enumRow(editor.ColorSpace, "Color Space", &img.color_space, 2);
            checkRow("Generate Mipmaps", &img.generate_mipmaps, 3);
            enumRow(editor.TextureCompression, "Compression", &img.compression, 4);
            enumRow(editor.ImageFilter, "Filter", &img.filter, 5);
            enumRow(editor.ImageWrap, "Wrap", &img.wrap, 6);
            textRow("Max Size", &max_size_buf, 7);
        },
        .model => {
            checkRow("Import Materials", &model.import_materials, 1);
            checkRow("Import Animations", &model.import_animations, 2);
            textRow("Scale Factor", &scale_buf, 3);
        },
        .font => {
            textRow("Default Size", &default_size_buf, 1);
        },
        else => return,
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 9200 });
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .all(6) });
        defer row.deinit();
        if (dirty)
            gui.label(@src(), "Unsaved changes", .{}, .{ .gravity_y = 0.5, .expand = .horizontal })
        else
            gui.label(@src(), "Saved", .{}, .{ .gravity_y = 0.5, .expand = .horizontal });
        if (gui.button(@src(), "Apply", .{}, .{ .gravity_y = 0.5, .style = if (dirty) .highlight else .control }))
            save();
    }
}

fn section(title: []const u8) void {
    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = @intFromPtr(title.ptr) });
    gui.label(@src(), "{s}", .{title}, .{ .id_extra = @intFromPtr(title.ptr), .padding = .{ .x = 6, .y = 6 } });
}

fn textRow(label: []const u8, buf: []u8, id: usize) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 2 }, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{label}, .{ .id_extra = id, .gravity_y = 0.5, .min_size_content = .{ .w = 140 } });
    var te = gui.textEntry(@src(), .{ .text = .{ .buffer = buf } }, .{ .id_extra = id, .gravity_y = 0.5, .expand = .horizontal });
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
    if (gui.dropdownEnum(@src(), T, .{ .choice = value }, .{}, .{ .id_extra = id, .gravity_y = 0.5, .min_size_content = .{ .w = 120 } }))
        dirty = true;
}

// ── Load / Save ────────────────────────────────────────────────────────────────

fn load(asset_path: []const u8, asset_type: editor.AssetType) void {
    setBuf(loaded_path_buf[0..], asset_path);
    loaded_path_len = @min(asset_path.len, loaded_path_buf.len - 1);
    loaded_type = asset_type;
    dirty = false;

    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const meta = editor.asset_meta.readMeta(gui.io, arena_state.allocator(), asset_path);

    img = .{};
    model = .{};
    font = .{};
    switch (meta.import_settings) {
        .image => |s| img = s,
        .model => |s| model = s,
        .font => |s| font = s,
        else => {},
    }

    var nb: [12]u8 = undefined;
    setBuf(&max_size_buf, std.fmt.bufPrint(&nb, "{d}", .{img.max_size}) catch "2048");
    var sb: [16]u8 = undefined;
    setBuf(&scale_buf, std.fmt.bufPrint(&sb, "{d}", .{model.scale_factor}) catch "1");
    var fb: [12]u8 = undefined;
    setBuf(&default_size_buf, std.fmt.bufPrint(&fb, "{d}", .{font.default_size}) catch "16");
}

fn save() void {
    const proj = EditorState.project_path orelse return;
    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const a = arena_state.allocator();

    // Re-read the meta so GUID, hash, and sub-asset manifest are preserved.
    var meta = editor.asset_meta.readMeta(gui.io, a, loadedPath());
    switch (loaded_type) {
        .image => {
            img.max_size = std.fmt.parseInt(u32, bufStr(&max_size_buf), 10) catch img.max_size;
            meta.import_settings = .{ .image = img };
        },
        .model => {
            model.scale_factor = std.fmt.parseFloat(f32, bufStr(&scale_buf)) catch model.scale_factor;
            meta.import_settings = .{ .model = model };
        },
        .font => {
            font.default_size = std.fmt.parseFloat(f32, bufStr(&default_size_buf)) catch font.default_size;
            meta.import_settings = .{ .font = font };
        },
        else => return,
    }

    editor.asset_meta.writeMeta(gui.io, a, loadedPath(), meta);
    dirty = false;
    // Re-cook so the new settings take effect.
    editor.asset_importer.importAssetForce(gui.io, a, proj, loadedPath());
}
