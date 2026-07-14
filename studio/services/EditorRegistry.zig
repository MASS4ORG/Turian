//! Editor registry : a data-driven `asset_type -> draw fn` table
//! replacing `studio/Inspector.zig`'s hardcoded `if (asset_type == .X)` chain.
//! Callers `register` once (see `Inspector.ensureRegistered`) and then
//! dispatch through `has`/`draw` instead of adding another branch. This epic
//! will register a `UiDocumentEditor` here in M2 — the
//! reason to build this now rather than grow the if-chain a 6th time.

const std = @import("std");
const editor = @import("editor");

/// `asset_type` is passed through so one draw fn can serve multiple types
/// (e.g. `ImportSettingsEditor` handles both `.image` and `.model`).
pub const DrawFn = *const fn (asset_path: []const u8, asset_type: editor.AssetType) void;

const Entry = struct {
    asset_type: editor.AssetType,
    draw_fn: DrawFn,
};

const MAX_ENTRIES = 32;
var entries: [MAX_ENTRIES]Entry = undefined;
var entry_count: usize = 0;

/// Register the editor for `asset_type`. Call once at startup (idempotent
/// guards belong to the caller); re-registering the same type overwrites it.
pub fn register(asset_type: editor.AssetType, draw_fn: DrawFn) void {
    for (entries[0..entry_count]) |*e| {
        if (e.asset_type == asset_type) {
            e.draw_fn = draw_fn;
            return;
        }
    }
    std.debug.assert(entry_count < MAX_ENTRIES);
    entries[entry_count] = .{ .asset_type = asset_type, .draw_fn = draw_fn };
    entry_count += 1;
}

/// Whether an editor is registered for `asset_type`.
pub fn has(asset_type: editor.AssetType) bool {
    for (entries[0..entry_count]) |e| {
        if (e.asset_type == asset_type) return true;
    }
    return false;
}

/// Draw the registered editor for `asset_type`, if any. Returns whether one
/// was found and drawn.
pub fn draw(asset_type: editor.AssetType, asset_path: []const u8) bool {
    for (entries[0..entry_count]) |e| {
        if (e.asset_type == asset_type) {
            e.draw_fn(asset_path, asset_type);
            return true;
        }
    }
    return false;
}

test "register/has/draw dispatch by asset_type" {
    entry_count = 0; // isolate from other tests sharing module state

    const S = struct {
        var last_path: []const u8 = "";
        var last_type: editor.AssetType = .unknown;
        fn onDraw(asset_path: []const u8, asset_type: editor.AssetType) void {
            last_path = asset_path;
            last_type = asset_type;
        }
    };

    try std.testing.expect(!has(.material));
    register(.material, S.onDraw);
    try std.testing.expect(has(.material));
    try std.testing.expect(!has(.data_asset));

    try std.testing.expect(draw(.material, "foo.material"));
    try std.testing.expectEqualStrings("foo.material", S.last_path);
    try std.testing.expectEqual(editor.AssetType.material, S.last_type);

    try std.testing.expect(!draw(.image, "foo.png"));
}

test "re-registering the same type overwrites instead of duplicating" {
    entry_count = 0;

    const S = struct {
        fn a(_: []const u8, _: editor.AssetType) void {}
        fn b(_: []const u8, _: editor.AssetType) void {}
    };
    register(.model, S.a);
    register(.model, S.b);
    try std.testing.expectEqual(@as(usize, 1), entry_count);
    try std.testing.expect(entries[0].draw_fn == &S.b);
}
