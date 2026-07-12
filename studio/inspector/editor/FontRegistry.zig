//! Registers imported Font assets' raw TTF/OTF bytes with dvui (#109) so they
//! actually render glyphs — today for the Inspector's live preview; later for
//! `.uidoc` text components once #104's Theme-asset integration lands.
//!
//! dvui has no "replace a registered font" API (`Window.addFont` only
//! appends), so each GUID is registered at most once per Studio session under
//! its own GUID string as the dvui family name — stable, collision-free, and
//! avoids the ever-growing-database problem of re-registering every frame a
//! panel is drawn. A font file edited on disk needs a Studio restart to be
//! picked up, same as dvui's own embedded-font lifetime model.
const std = @import("std");
const gui = @import("gui");

const GUID_LEN = 36;
const MAX_FONTS = 64;

var guids: [MAX_FONTS][GUID_LEN]u8 = undefined;
var guid_count: usize = 0;

fn isRegistered(guid: []const u8) bool {
    for (guids[0..guid_count]) |*g| {
        if (std.mem.eql(u8, g, guid)) return true;
    }
    return false;
}

/// Ensures `guid`'s font (source file at `path`) is registered with dvui
/// under `guid` as the dvui family name. Returns the family name to build a
/// `gui.Font` with (`gui.Font.find(.{ .family = family, .size = ... })`), or
/// null if the file couldn't be read or dvui rejected it as an invalid font.
pub fn ensure(guid: []const u8, path: []const u8) ?[]const u8 {
    if (guid.len != GUID_LEN) return null;
    if (isRegistered(guid)) return guid;
    if (guid_count >= MAX_FONTS) return null;

    const bytes = std.Io.Dir.cwd().readFileAlloc(gui.io, path, std.heap.page_allocator, .unlimited) catch return null;
    gui.addFont(guid, bytes, std.heap.page_allocator) catch {
        std.heap.page_allocator.free(bytes);
        return null;
    };

    @memcpy(&guids[guid_count], guid[0..GUID_LEN]);
    guid_count += 1;
    return guid;
}
