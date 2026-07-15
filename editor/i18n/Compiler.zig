//! `.strings` (JSON, authoring) -> `.strtab` (binary, runtime) — see
//! `engine/assets/Strings.zig` and `engine/i18n/StringTable.zig`.
//!
//! Also merges freshly `Extractor`-discovered call sites into an existing
//! `.strings` file, preserving translations already on disk. Since a `tr()`
//! id *is* its English source (D1), an edited source string is a new id by
//! construction — there is no separate "id stable, source changed" case to
//! detect here, so this merge is simpler than XLIFF vendor round-tripping:
//! an id present in both keeps its existing `target`/`state`; a new id is
//! added as `.new`; an id no longer extracted is dropped (not archived to an
//! `obsolete` list — that refinement is future editor-tooling work, not
//! required for a first working pipeline).
const std = @import("std");
const engine = @import("engine");
const Strings = engine.assets.Strings;
const StringTableMod = engine.i18n.StringTableMod;
const Extractor = @import("Extractor.zig");

/// Bake `strings` (whatever locale it declares) down to `.strtab` bytes.
/// Only units with a non-empty `target` are included — a missing
/// translation already falls back to the English source at runtime
/// (`engine.i18n.Locale`), so there is no need to bake placeholder entries.
/// Caller owns and frees the result.
pub fn compile(allocator: std.mem.Allocator, strings: Strings) ![]u8 {
    var out_units: std.ArrayList(StringTableMod.Unit) = .empty;
    defer out_units.deinit(allocator);
    for (strings.units) |u| {
        if (u.target.len == 0) continue;
        try out_units.append(allocator, .{ .id = u.id, .value = u.target });
    }
    return StringTableMod.encode(allocator, strings.locale, out_units.items);
}

/// Build (or update) a `.strings` file for `locale` from freshly extracted
/// call sites, keeping any `target`/`state` already present in `existing`
/// for ids that still exist. Returns a new `Strings`; caller frees with
/// `.deinit`. Does not modify `existing`.
pub fn mergeExtracted(
    allocator: std.mem.Allocator,
    existing: ?Strings,
    extracted: []const Extractor.ExtractedUnit,
    locale: []const u8,
) !Strings {
    var units: std.ArrayList(Strings.Unit) = .empty;
    defer units.deinit(allocator);
    try units.ensureTotalCapacity(allocator, extracted.len);

    for (extracted) |e| {
        const prior = if (existing) |ex| findById(ex.units, e.id) else null;
        units.appendAssumeCapacity(.{
            .id = try allocator.dupe(u8, e.id),
            .source = try allocator.dupe(u8, e.source),
            .target = try allocator.dupe(u8, if (prior) |p| p.target else ""),
            .note = try allocator.dupe(u8, e.note),
            .state = if (prior) |p| p.state else .new,
        });
    }

    return Strings{
        .locale = try allocator.dupe(u8, locale),
        .units = try units.toOwnedSlice(allocator),
    };
}

fn findById(units: []const Strings.Unit, id: []const u8) ?*const Strings.Unit {
    for (units) |*u| if (std.mem.eql(u8, u.id, id)) return u;
    return null;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

const testing = std.testing;

test "compile emits only translated units, source-fallback ones are skipped" {
    const strings = Strings{
        .locale = "pt-BR",
        .units = @constCast(&[_]Strings.Unit{
            .{ .id = "Cancel", .source = "Cancel", .target = "Cancelar", .state = .translated },
            .{ .id = "Save", .source = "Save", .target = "", .state = .new },
        }),
    };
    const bytes = try compile(testing.allocator, strings);
    defer testing.allocator.free(bytes);

    const table = try engine.i18n.StringTable.init(bytes);
    try testing.expectEqual(@as(usize, 1), table.count);
    try testing.expectEqualStrings("Cancelar", table.get("Cancel").?);
    try testing.expectEqual(@as(?[]const u8, null), table.get("Save"));
}

test "mergeExtracted keeps existing translations for still-present ids" {
    var existing = Strings{
        .locale = "pt-BR",
        .units = @constCast(&[_]Strings.Unit{
            .{ .id = "Cancel", .source = "Cancel", .target = "Cancelar", .note = "old.zig:1", .state = .translated },
        }),
    };
    const extracted = [_]Extractor.ExtractedUnit{
        .{ .id = "Cancel", .source = "Cancel", .note = "new.zig:42" },
        .{ .id = "Save", .source = "Save", .note = "new.zig:50" },
    };

    var merged = try mergeExtracted(testing.allocator, existing, &extracted, "pt-BR");
    defer merged.deinit(testing.allocator);
    _ = &existing;

    try testing.expectEqual(@as(usize, 2), merged.units.len);
    try testing.expectEqualStrings("Cancelar", merged.units[0].target);
    try testing.expectEqual(Strings.State.translated, merged.units[0].state);
    try testing.expectEqualStrings("new.zig:42", merged.units[0].note);
    try testing.expectEqualStrings("", merged.units[1].target);
    try testing.expectEqual(Strings.State.new, merged.units[1].state);
}

test "mergeExtracted with no existing file: everything is new" {
    const extracted = [_]Extractor.ExtractedUnit{
        .{ .id = "Open", .source = "Open" },
    };
    var merged = try mergeExtracted(testing.allocator, null, &extracted, "ja");
    defer merged.deinit(testing.allocator);
    try testing.expectEqual(@as(usize, 1), merged.units.len);
    try testing.expectEqual(Strings.State.new, merged.units[0].state);
}
