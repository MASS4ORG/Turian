//! UiTheme asset — a serializable subset of DVUI's `Theme`, stored as a
//! `.uitheme` file as JSON (same on-disk family as materials).
//!
//! Covers colors and corner rounding only. Fonts are intentionally NOT part of
//! this asset: DVUI's `Font`/`Font.Source`/`Ninepatch` carry raw embedded font
//! bytes that aren't reasonably JSON-serializable, and font size is already a
//! separate Studio-wide setting independent of the active theme. Consumers
//! that need a real `gui.Theme` (Studio, the shipped game) convert this asset
//! by overlaying it onto a base theme that already has fonts loaded — see
//! `subsystems/ui_render/theme.zig`'s `toDvuiTheme`. `engine/` itself has no
//! GUI dependency, so that conversion cannot live here.
const std = @import("std");
const serde = @import("serde");

/// Plain RGBA color, byte-for-byte compatible with `dvui.Color`.
pub const Color = struct {
    r: u8 = 0xff,
    g: u8 = 0xff,
    b: u8 = 0xff,
    a: u8 = 0xff,
};

/// Widget corner rounding, byte-for-byte compatible with `dvui.Corner`.
pub const Corner = struct {
    pub const Kind = enum { theme, square, round, chamfer, nudge, angular };

    kind: Kind = .round,
    /// Radius (or x offset for `.nudge`), in points. -1 means "use theme size".
    rx: f32 = 5,
    y: f32 = 0,
};

/// Per-role color overrides, mirroring `dvui.Theme.Style`. Null falls back to
/// the theme's base colors.
pub const Style = struct {
    fill: ?Color = null,
    fill_hover: ?Color = null,
    fill_press: ?Color = null,
    text: ?Color = null,
    text_hover: ?Color = null,
    text_press: ?Color = null,
    border: ?Color = null,
};

/// Current theme format version. Bump when the layout changes and add a
/// migration in `migrate` so older assets keep loading.
pub const CURRENT_VERSION: u32 = 1;

version: u32 = CURRENT_VERSION,
name: []const u8 = "Untitled",
/// Whether this is a dark theme — used by consumers to auto-lighten/darken
/// hover/press states that don't have an explicit override.
dark: bool = false,

/// Focus-ring color.
focus: Color = .{ .r = 0x35, .g = 0x84, .b = 0xe4 },

/// Base content colors, fallback for any style group without its own.
fill: Color = .{ .r = 0xff, .g = 0xff, .b = 0xff },
fill_hover: ?Color = null,
fill_press: ?Color = null,
text: Color = .{ .r = 0, .g = 0, .b = 0 },
text_hover: ?Color = null,
text_press: ?Color = null,
border: Color = .{ .r = 0xa0, .g = 0xa0, .b = 0xa0 },

/// Colors for normal controls like buttons.
control: Style = .{},
/// Colors for windows/boxes that contain controls.
window: Style = .{},
/// Colors for highlighting: menu/dropdown items, checkboxes, radio buttons.
highlight: Style = .{},
/// Colors for buttons that perform dangerous actions.
err: Style = .{},
/// Dock panel body chrome (background + border) — Studio repurposes this
/// reserved dvui style slot for panel fill/border so panels read as visually
/// distinct from the root window and tab strip (both `.window`-styled).
/// Games are free to use it for their own purposes instead.
app1: Style = .{},
app2: Style = .{},
app3: Style = .{},

/// Dock panel border thickness, in logical pixels. Defaults to 0 (no border)
/// — a theme opts in by setting this explicitly.
panel_border_width: f32 = 0,

/// Dock panel corner radius, in logical pixels. Independent of `corner`
/// (which rounds ordinary widgets): panels are large surfaces and read best
/// with a softer radius than a button's.
panel_corner_radius: f32 = 0,

/// Default widget corner.
corner: Corner = .{},

/// Suggested font family name (must already be loaded — one of the base
/// theme's embedded families, e.g. "Vera Sans" / "Vera Sans Mono" — or a
/// user-picked system font registered separately). Empty keeps the base
/// theme's own fonts untouched.
font_family: []const u8 = "",

const UiTheme = @This();

// ── Load ───────────────────────────────────────────────────────────────────

/// Parse a theme from in-memory `.uitheme` (JSON) bytes. The returned value
/// owns its slices; free with `deinit`. `bytes` need not be NUL-terminated.
pub fn loadFromBytes(allocator: std.mem.Allocator, bytes: []const u8) !UiTheme {
    var t = try serde.json.fromSlice(UiTheme, allocator, bytes);
    // Absent fields keep their compile-time defaults. `name`/`font_family`
    // then alias the struct literal, which `deinit` must not free —
    // normalise them to owned copies so freeing is uniform.
    const def = UiTheme{};
    if (t.name.ptr == def.name.ptr) t.name = try allocator.dupe(u8, t.name);
    if (t.font_family.ptr == def.font_family.ptr) t.font_family = try allocator.dupe(u8, t.font_family);
    migrate(&t);
    return t;
}

/// Load a theme from a `.uitheme` file. The returned value owns its slices;
/// free with `deinit`.
pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !UiTheme {
    var file = try std.Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = try reader.interface.allocRemaining(allocator, .unlimited);
    defer allocator.free(content);
    return loadFromBytes(allocator, content);
}

/// Free slices owned by a theme produced via `load`/`loadFromBytes`.
/// Must not be called on a theme assembled by a caller (e.g. a built-in
/// preset), whose slices point at static/caller-owned buffers.
pub fn deinit(self: UiTheme, allocator: std.mem.Allocator) void {
    allocator.free(self.name);
    allocator.free(self.font_family);
}

// ── Save ───────────────────────────────────────────────────────────────────

/// Serialize this theme as pretty-printed JSON into `writer`.
pub fn serialize(self: UiTheme, writer: *std.Io.Writer) !void {
    try serde.json.toWriterWith(writer, self, .{ .pretty = true });
}

/// Write this theme to `path` as a `.uitheme` JSON file.
pub fn save(self: UiTheme, io: std.Io, path: []const u8) !void {
    var buf: [1024 * 16]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buf);
    try self.serialize(&writer);
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = writer.buffered() });
}

/// Upgrade a just-parsed theme in place to `CURRENT_VERSION`. New versions
/// add cases here so older assets keep loading.
fn migrate(t: *UiTheme) void {
    if (t.version < CURRENT_VERSION) t.version = CURRENT_VERSION;
}

// ── Tests ────────────────────────────────────────────────────────────────────

test "default theme round-trips through JSON" {
    const allocator = std.testing.allocator;

    var buf: [1024 * 16]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buf);
    const original = UiTheme{
        .name = "Test Theme",
        .dark = true,
        .control = .{ .fill = .{ .r = 0x40, .g = 0x40, .b = 0x40 } },
    };
    try original.serialize(&writer);

    var parsed = try loadFromBytes(allocator, writer.buffered());
    defer parsed.deinit(allocator);

    try std.testing.expectEqualStrings("Test Theme", parsed.name);
    try std.testing.expectEqual(true, parsed.dark);
    try std.testing.expectEqual(@as(u8, 0x40), parsed.control.fill.?.r);
    try std.testing.expectEqual(UiTheme.CURRENT_VERSION, parsed.version);
}

test "missing fields fall back to defaults" {
    const allocator = std.testing.allocator;
    var parsed = try loadFromBytes(allocator, "{}");
    defer parsed.deinit(allocator);

    try std.testing.expectEqualStrings("Untitled", parsed.name);
    try std.testing.expectEqual(false, parsed.dark);
}
