//! Inspector panel for `.font` assets: import settings
//! (`ImportSettingsEditor`, shared with image/model assets). The live text
//! preview lives in `drawPreview` below, registered as `.font`'s
//! `PreviewSystem.LiveDrawFn` rather than drawn inline here — see
//! `PreviewSystem`'s live-provider registry.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const FontRegistry = @import("FontRegistry.zig");
const ImportSettingsEditor = @import("ImportSettingsEditor.zig");
const StudioLocale = @import("../../services/StudioLocale.zig");
const tr = StudioLocale.tr;

const PANGRAM = "The quick brown fox jumps over the lazy dog";
const Specimen = struct { text: []const u8, size: f32 };
const SPECIMENS = [_]Specimen{
    .{ .text = PANGRAM, .size = 14 },
    .{ .text = "Turian Engine", .size = 22 },
    .{ .text = "AaBbCc 123", .size = 38 },
};

pub fn draw(asset_path: []const u8, asset_type: editor.AssetType) void {
    ImportSettingsEditor.draw(asset_path, asset_type);
}

/// Live text preview rendered with the actual imported font (via
/// `FontRegistry`), set next to Studio's built-in dvui theme font for
/// comparison. Matches `PreviewSystem.LiveDrawFn`.
pub fn drawPreview(asset_path: []const u8, guid: []const u8) void {
    var box = gui.box(@src(), .{}, .{ .expand = .horizontal, .padding = .all(8) });
    defer box.deinit();

    const family = FontRegistry.ensure(guid, asset_path) orelse {
        gui.label(@src(), "{s}", .{tr("(preview unavailable — could not read font file)")}, .{ .id_extra = 99 });
        return;
    };

    for (SPECIMENS, 0..) |spec, i| {
        gui.label(@src(), "{s}", .{spec.text}, .{
            .font = gui.Font.find(.{ .family = family, .size = spec.size }),
            .id_extra = i,
            .padding = .{ .y = 2 },
        });
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 7150 });
    gui.label(@src(), "{s}", .{tr("dvui default (for comparison):")}, .{ .id_extra = 90, .style = .content });
    gui.label(@src(), "{s}", .{PANGRAM}, .{ .font = .theme(.body), .id_extra = 91, .padding = .{ .y = 2 } });
}
