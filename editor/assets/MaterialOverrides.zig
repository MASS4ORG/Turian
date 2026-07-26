//! Reverse lookup and write path for per-material import overrides: given a
//! `.material` sub-asset's GUID, find the model asset that generated it and
//! the source material name to key an override by, then persist an edit into
//! that model's tracked `.meta` instead of the gitignored cache artifact.
//! `ModelDerivedAssets.zig` is the read side that merges these back in at
//! cook time.
const std = @import("std");
const Guid = @import("guid").Guid;
const AssetDatabase = @import("AssetDatabase.zig").AssetDatabase;
const asset_meta = @import("AssetMeta.zig");
const MaterialOverride = @import("../types/ImportSettings.zig").MaterialOverride;

pub const Owner = struct {
    /// Path of the model asset that generated the material sub-asset.
    model_path: []const u8,
    /// Source material name (matches `MaterialInfo.name`), the override key.
    material_name: []const u8,
};

/// If `guid` is a `.material` sub-asset generated from a model, return its
/// owning model asset path and source material name. Scans every indexed
/// model's `.meta` — projects have at most a few hundred models, so this is
/// cheap relative to the disk reads it's already doing. Allocates from `arena`.
pub fn findOwner(io: std.Io, arena: std.mem.Allocator, db: *AssetDatabase, guid: Guid) ?Owner {
    var it = db.enumerate(.model);
    while (it.next()) |info| {
        const meta = asset_meta.readMeta(io, arena, info.path);
        for (meta.sub_assets) |sub| {
            if (sub.asset_type != .material or !sub.guid.eql(guid)) continue;
            return .{
                .model_path = arena.dupe(u8, info.path) catch info.path,
                .material_name = stripMaterialExt(sub.name),
            };
        }
    }
    return null;
}

fn stripMaterialExt(name: []const u8) []const u8 {
    const ext = ".material";
    if (std.mem.endsWith(u8, name, ext)) return name[0 .. name.len - ext.len];
    return name;
}

/// Merge `override` into `model_path`'s `.meta` (replacing any existing entry
/// for the same material name) and write it back. No-op if the asset isn't a
/// model. Does not reimport — the caller decides when to re-cook.
pub fn writeOverride(io: std.Io, arena: std.mem.Allocator, model_path: []const u8, override: MaterialOverride) void {
    var meta = asset_meta.readMeta(io, arena, model_path);
    var settings = switch (meta.import_settings) {
        .model => |s| s,
        else => return,
    };

    var list: std.ArrayList(MaterialOverride) = .empty;
    list.appendSlice(arena, settings.material_overrides) catch return;

    var replaced = false;
    for (list.items) |*o| {
        if (std.mem.eql(u8, o.material_name, override.material_name)) {
            o.* = override;
            replaced = true;
            break;
        }
    }
    if (!replaced) list.append(arena, override) catch return;

    settings.material_overrides = list.items;
    meta.import_settings = .{ .model = settings };
    asset_meta.writeMeta(io, arena, model_path, meta);
}
