//! `.strings` asset — the source-of-truth translation file for one locale
//! (ADR 0011, decision D3). JSON via `serde.json`, same on-disk family as
//! `Material`/`UiTheme`. A 1:1 model of an XLIFF `<trans-unit>` so the
//! XLIFF/CSV round-trip (`editor/i18n/`) is total in both directions.
//!
//! This is the *authoring* format: `editor/i18n/Compiler.zig` bakes it down
//! to the compact `.strtab` binary (`engine/i18n/StringTable.zig`) that
//! actually ships. A game never parses JSON for its string tables.
const std = @import("std");
const serde = @import("serde");

/// Translation lifecycle, mirroring XLIFF's `state` attribute.
pub const State = enum {
    /// Extracted, not yet translated.
    new,
    /// Translated previously, but the source text has since changed —
    /// the target is kept (better than nothing) but flagged for review.
    needs_review,
    translated,
    /// Reviewed and locked.
    final,
};

/// One id -> translated-string pair plus authoring metadata.
pub const Unit = struct {
    /// Lookup key: the literal English source for `tr`/`trc`/`trn` sites
    /// (see `engine.i18n.Locale`), or a designer-chosen id for `Locale.key`
    /// content. `trc` ids are `context ++ "\x04" ++ source`; `trn` ids are
    /// the synthesized ICU plural pattern.
    id: []const u8 = "",
    /// The original English text (for id-keyed units, a translator hint —
    /// not itself looked up at runtime).
    source: []const u8 = "",
    target: []const u8 = "",
    /// Free-form context for the translator (source file, surrounding UI).
    note: []const u8 = "",
    state: State = .new,
};

pub const CURRENT_VERSION: u32 = 1;

version: u32 = CURRENT_VERSION,
/// BCP-47 tag this file's `target`s are written in.
locale: []const u8 = "en",
units: []Unit = &.{},

const Strings = @This();

// ── Load ─────────────────────────────────────────────────────────────────

/// Parse a `.strings` asset from in-memory JSON bytes. The returned value
/// owns its slices; free with `deinit`.
pub fn loadFromBytes(allocator: std.mem.Allocator, bytes: []const u8) !Strings {
    var s = try serde.json.fromSlice(Strings, allocator, bytes);
    // Absent fields keep their compile-time defaults. `locale` then aliases
    // the struct literal, which `deinit` must not free — normalise it to an
    // owned copy so freeing is uniform (per-`Unit` string fields default to
    // `""`, which `free` always no-ops on regardless of origin).
    const def = Strings{};
    if (s.locale.ptr == def.locale.ptr) s.locale = try allocator.dupe(u8, s.locale);
    migrate(&s);
    return s;
}

/// Load a `.strings` asset from a file. The returned value owns its slices;
/// free with `deinit`.
pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Strings {
    var file = try std.Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = try reader.interface.allocRemaining(allocator, .unlimited);
    defer allocator.free(content);
    return loadFromBytes(allocator, content);
}

/// Free slices owned by a `Strings` produced via `load`/`loadFromBytes`.
pub fn deinit(self: Strings, allocator: std.mem.Allocator) void {
    allocator.free(self.locale);
    for (self.units) |u| {
        allocator.free(u.id);
        allocator.free(u.source);
        allocator.free(u.target);
        allocator.free(u.note);
    }
    if (self.units.len != 0) allocator.free(self.units);
}

// ── Save ─────────────────────────────────────────────────────────────────

/// Serialize this asset as pretty-printed JSON into `writer`.
pub fn serialize(self: Strings, writer: *std.Io.Writer) !void {
    try serde.json.toWriterWith(writer, self, .{ .pretty = true });
}

/// Serialize to a freshly allocated string. Caller frees with `allocator.free`.
pub fn serializeAlloc(self: Strings, allocator: std.mem.Allocator) ![]u8 {
    var out: std.Io.Writer.Allocating = .init(allocator);
    defer out.deinit();
    try self.serialize(&out.writer);
    return out.toOwnedSlice();
}

/// Write this asset to `path` as a `.strings` JSON file.
pub fn save(self: Strings, allocator: std.mem.Allocator, io: std.Io, path: []const u8) !void {
    const bytes = try self.serializeAlloc(allocator);
    defer allocator.free(bytes);
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = bytes });
}

fn migrate(s: *Strings) void {
    if (s.version < CURRENT_VERSION) s.version = CURRENT_VERSION;
}

// ── Tests ────────────────────────────────────────────────────────────────

test "round-trips through JSON" {
    const allocator = std.testing.allocator;

    const original = Strings{
        .locale = "pt-BR",
        .units = @constCast(&[_]Unit{
            .{ .id = "Open Scene…", .source = "Open Scene…", .target = "Abrir Cena…", .state = .translated },
            .{ .id = "dlg.act1.intro", .source = "Hello!", .target = "", .note = "Act 1 opening line", .state = .new },
        }),
    };

    const bytes = try original.serializeAlloc(allocator);
    defer allocator.free(bytes);

    var parsed = try Strings.loadFromBytes(allocator, bytes);
    defer parsed.deinit(allocator);

    try std.testing.expectEqualStrings("pt-BR", parsed.locale);
    try std.testing.expectEqual(@as(usize, 2), parsed.units.len);
    try std.testing.expectEqualStrings("Open Scene…", parsed.units[0].id);
    try std.testing.expectEqualStrings("Abrir Cena…", parsed.units[0].target);
    try std.testing.expectEqual(State.translated, parsed.units[0].state);
    try std.testing.expectEqualStrings("Act 1 opening line", parsed.units[1].note);
    try std.testing.expectEqual(State.new, parsed.units[1].state);
}

test "empty units list round-trips" {
    const allocator = std.testing.allocator;
    const original = Strings{ .locale = "en" };
    const bytes = try original.serializeAlloc(allocator);
    defer allocator.free(bytes);
    var parsed = try Strings.loadFromBytes(allocator, bytes);
    defer parsed.deinit(allocator);
    try std.testing.expectEqual(@as(usize, 0), parsed.units.len);
}
