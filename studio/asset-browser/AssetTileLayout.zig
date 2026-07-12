//! Grid tile sizing and label-truncation for `AssetGridView.zig` and
//! `AssetSubAssetTiles.zig` (issue #84): how big a tile is (zoom + how much of
//! a filename it reserves room for) and how a filename gets shortened to fit.
//! Split out of `AssetGridView.zig` to keep that file under the project's
//! long-file budget (`docs/ADR` file-size guidance) — this half is a
//! self-contained "tile metrics" concern with no dependency on the directory
//! listing/drawing itself.

const std = @import("std");
const gui = @import("gui");
const EditorState = @import("../services/EditorState.zig");

/// Tile content size (icon or preview thumbnail), adjustable via the header's
/// slider (issue #25's "preview size can be adjusted") — real-time zoom, not
/// persisted, matching most other per-panel view toggles here. Deliberately
/// independent of `max_name_chars`: this only scales the tile/icon, not how
/// much of the filename shows (issue #84 follow-up — zoom and name length are
/// separate knobs).
pub var tile_content: f32 = 32;
pub const TILE_CONTENT_MIN: f32 = 20;
pub const TILE_CONTENT_MAX: f32 = 128;

pub fn tileHeight() f32 {
    return tile_content + 40;
}

/// Padding taken off a tile's box on each axis (matches the tiles' own
/// `.padding = .all(4)`), subtracted from `tileWidth()` to get the width
/// actually available to a tile's icon/label.
const TILE_PADDING: f32 = 8;

/// Rough average glyph width for the current theme's body font: there's no
/// way to know a filename's exact rendered width without measuring that exact
/// string, so `tileWidth()` guesses using a representative alphanumeric
/// sample instead (issue #84 follow-up — converting `max_name_chars` into a
/// pixel cell width needs *some* char-to-pixel estimate).
fn avgCharWidth() f32 {
    const font = gui.themeGet().font_body;
    const sample = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    return font.textSize(sample).w / @as(f32, @floatFromInt(sample.len));
}

/// Tile width: wide enough to show `max_name_chars` characters (estimated —
/// see `avgCharWidth`), but never narrower than the icon needs
/// (`tileHeight()`). Grid cells are rectangular, not square, once the name
/// cap needs more room than the icon does (issue #84 follow-up).
pub fn tileWidth() f32 {
    const char_cap: f32 = @floatFromInt(@max(max_name_chars, 1));
    const estimated_name_width = avgCharWidth() * char_cap;
    return @max(tileHeight(), estimated_name_width + TILE_PADDING);
}

pub fn tileContentWidth() f32 {
    return tileWidth() - TILE_PADDING;
}

/// Max characters of a filename shown in a tile label before truncating with
/// an ellipsis (issue #84), independent of `tile_content`'s zoom. Controlled
/// from the Studio Settings editor — `SettingsEditor.save()` writes this
/// directly so a change applies immediately.
pub var max_name_chars: i64 = 16;

/// Whether to hide file extensions in tile labels (issue #84), e.g. show
/// "MyTexture" instead of "MyTexture.png". Same Settings-editor-controlled
/// pattern as `max_name_chars`.
pub var hide_extensions: bool = true;

const NAME_CHAR_LENGTH_SETTING_KEY = "asset_browser.name_char_length";
const HIDE_EXT_SETTING_KEY = "asset_browser.hide_extensions";
var synced_from_settings: bool = false;

pub fn syncFromSettings() void {
    if (synced_from_settings or !EditorState.settingsReady()) return;
    max_name_chars = EditorState.settings.getInt(NAME_CHAR_LENGTH_SETTING_KEY, max_name_chars);
    hide_extensions = EditorState.settings.getBool(HIDE_EXT_SETTING_KEY, hide_extensions);
    synced_from_settings = true;
}

/// Truncate `text` to at most `max_len` characters, replacing the tail with
/// "..." when it doesn't fit. Returns `text` unchanged (not copied into
/// `buf`) when it already fits.
fn truncateToLen(text: []const u8, max_len: usize, buf: []u8) []const u8 {
    if (text.len <= max_len) return text;

    const ellipsis = "...";
    if (max_len <= ellipsis.len) {
        const n = @min(max_len, @min(text.len, buf.len));
        @memcpy(buf[0..n], text[0..n]);
        return buf[0..n];
    }
    const keep = max_len - ellipsis.len;
    const n = @min(keep, buf.len -| ellipsis.len);
    @memcpy(buf[0..n], text[0..n]);
    @memcpy(buf[n .. n + ellipsis.len], ellipsis);
    return buf[0 .. n + ellipsis.len];
}

/// Strip a file's extension when `hide_extensions` is on (a no-op for
/// directories or when it's off). Shared by `truncatedDisplayName` (the fixed
/// grid tiles) and the folder-tree views (`AssetTreeView.zig`'s
/// `Model.name`), which respect `hide_extensions` but — unlike the grid —
/// have no fixed-size cell to protect, so they skip `max_name_chars`
/// entirely (issue #84 follow-up).
pub fn stripExtensionIfHidden(name: []const u8, is_dir: bool) []const u8 {
    if (!hide_extensions or is_dir) return name;
    if (std.mem.lastIndexOfScalar(u8, name, '.')) |dot| {
        if (dot > 0) return name[0..dot];
    }
    return name;
}

/// Display text for a tile label: optionally strips the extension
/// (`hide_extensions`, skipped for directories), then caps it to
/// `max_name_chars` characters — the primary, zoom-independent control for
/// how much of a filename shows (issue #84 follow-up). Also fits the result
/// within `max_width` pixels as a safety net so a very small zoom still can't
/// overflow the fixed-size tile. Returns `null` when `name` fits unmodified;
/// callers show a tooltip with the full original name only when this returns
/// non-null.
pub fn truncatedDisplayName(name: []const u8, is_dir: bool, max_width: f32, buf: []u8) ?[]const u8 {
    const base = stripExtensionIfHidden(name, is_dir);
    var altered = base.len != name.len;

    var char_buf: [300]u8 = undefined;
    const char_cap: usize = @intCast(@max(max_name_chars, 1));
    const text = truncateToLen(base, char_cap, &char_buf);
    if (text.len != base.len) altered = true;

    const font = gui.themeGet().font_body;
    if (font.textSize(text).w <= max_width) {
        if (!altered) return null;
        const n = @min(text.len, buf.len);
        @memcpy(buf[0..n], text[0..n]);
        return buf[0..n];
    }

    const ellipsis = "...";
    const avail = @max(max_width - font.textSize(ellipsis).w, 0);
    var end_idx: usize = 0;
    _ = font.textSizeEx(text, .{ .max_width = avail, .end_idx = &end_idx, .end_metric = .before });
    const n = @min(end_idx, buf.len -| ellipsis.len);
    @memcpy(buf[0..n], text[0..n]);
    @memcpy(buf[n .. n + ellipsis.len], ellipsis);
    return buf[0 .. n + ellipsis.len];
}
