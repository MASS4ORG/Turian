const std = @import("std");
const AssetType = @import("../types/AssetType.zig").AssetType;

pub const AssetDescriptor = @import("../types/AssetDescriptor.zig").AssetDescriptor;
pub const OpenMode = @import("../types/AssetDescriptor.zig").OpenMode;
pub const IconHint = @import("../types/AssetDescriptor.zig").IconHint;

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
            .extensions = &.{ ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp", ".ktx2", ".dds", ".hdr" },
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
            // The serialized node-hierarchy asset is a "Prefab"; the live,
            // open hierarchy is the "Scene". `.prefab` is the
            // canonical extension; `.json` is still read for older scenes.
            .name = "Prefab",
            .extensions = &.{ ".prefab", ".json" },
            .open_mode = .internal_editor,
            .icon_hint = .document,
            .create_menu_path = "Prefab",
        },
        .material => .{
            .name = "Material",
            .extensions = &.{".material"},
            .open_mode = .internal_editor,
            .icon_hint = .material,
            // Leaf-less: the asset browser appends "/<preset name>" per
            // built-in `engine.Material.presets` entry.
            .create_menu_path = "Material",
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
            .create_menu_path = "Settings/Input Actions",
        },
        .project_settings => .{
            .name = "Project Settings",
            .extensions = &.{".projectsettings"},
            .open_mode = .internal_editor,
            .icon_hint = .data,
            .create_menu_path = "Settings/Project Settings",
        },
        .ui_document => .{
            .name = "UI Document",
            .extensions = &.{".uidoc"},
            .open_mode = .internal_editor,
            .icon_hint = .document,
            .create_menu_path = "UI/Document",
        },
        .font => .{
            .name = "Font",
            .extensions = &.{ ".ttf", ".otf" },
            .open_mode = .external_editor,
            .icon_hint = .font,
        },
        .ui_theme => .{
            .name = "UI Theme",
            .extensions = &.{".uitheme"},
            .open_mode = .internal_editor,
            .icon_hint = .theme,
            .create_menu_path = "UI/Theme",
        },
        .studio_settings => .{
            .name = "Studio Settings",
            // Opened programmatically (`Documents.openAsset`), never via
            // extension lookup.
            .extensions = &.{},
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
