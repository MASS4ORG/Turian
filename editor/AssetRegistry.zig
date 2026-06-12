const std = @import("std");
const AssetType = @import("types/AssetType.zig").AssetType;

pub const AssetDescriptor = @import("types/AssetDescriptor.zig").AssetDescriptor;
pub const OpenMode = @import("types/AssetDescriptor.zig").OpenMode;
pub const IconHint = @import("types/AssetDescriptor.zig").IconHint;

/// Returns the editor descriptor for the given asset type.
/// Describes display name, extensions, open mode, and icon.
pub fn get(asset_type: AssetType) AssetDescriptor {
    return switch (asset_type) {
        .unknown => .{
            .name = "Unknown",
            .extensions = &.{},
            .open_mode = .none,
            .icon_hint = .document,
        },
        .script => .{
            .name = "Script",
            .extensions = &.{".zig"},
            .open_mode = .external_editor,
            .icon_hint = .code,
        },
        .image => .{
            .name = "Image",
            .extensions = &.{ ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp" },
            .open_mode = .external_editor,
            .icon_hint = .image,
        },
        .audio => .{
            .name = "Audio",
            .extensions = &.{ ".wav", ".ogg", ".mp3", ".flac" },
            .open_mode = .external_editor,
            .icon_hint = .sound,
        },
        .model => .{
            .name = "Model",
            .extensions = &.{ ".gltf", ".glb", ".obj", ".fbx" },
            .open_mode = .external_editor,
            .icon_hint = .model,
        },
        .scene => .{
            .name = "Scene",
            .extensions = &.{".json"},
            .open_mode = .internal_editor,
            .icon_hint = .document,
        },
        .material => .{
            .name = "Material",
            .extensions = &.{".material"},
            .open_mode = .internal_editor,
            .icon_hint = .material,
        },
        .data_asset => .{
            .name = "Data Asset",
            .extensions = &.{".asset"},
            .open_mode = .internal_editor,
            .icon_hint = .data,
        },
        .input_actions => .{
            .name = "Input Actions",
            .extensions = &.{".inputactions"},
            .open_mode = .internal_editor,
            .icon_hint = .data,
        },
    };
}

/// Returns the AssetType matching the filename extension, or .unknown.
/// Checks all registered asset type extensions.
pub fn lookupByFilename(filename: []const u8) AssetType {
    inline for (@typeInfo(AssetType).@"enum".fields) |field| {
        const at: AssetType = @enumFromInt(field.value);
        if (at == .unknown) continue;
        const desc = get(at);
        for (desc.extensions) |ext| {
            if (std.mem.endsWith(u8, filename, ext)) return at;
        }
    }
    return .unknown;
}
