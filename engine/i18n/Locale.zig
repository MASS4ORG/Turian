//! The localization service (ADR 0011). Register an instance with
//! `frame.services.register(Locale, &locale)`; consumers fetch it with
//! `frame.service(Locale)` or use the `Frame.tr`/`trc`/`trn`/`key`
//! convenience wrappers.
//!
//! Two entry points, one table:
//!   - `tr`/`trc`/`trn` — source-keyed, for UI chrome. The lookup id is the
//!     literal English source text (`trc` disambiguates same-text-different-
//!     meaning strings by prefixing a context). Never fails visibly: a
//!     missing translation formats and returns the English source itself.
//!   - `key` — id-keyed, for designer-authored game content. A missing
//!     translation returns `⟦id⟧` in debug builds (visible, so it's never
//!     mistaken for real text) and the bare `id` in release.
//!
//! `Locale` never allocates persistent storage for loaded tables and never
//! frees one on locale switch (D4): the caller (`loadTable`) owns the bytes
//! and must keep them alive for the session; `Locale` only borrows pointers.
//! This is what makes "switch locale, no scene reload" free of dangling
//! slices — text already handed out from the old table stays valid, it's
//! just stale until the caller re-fetches it.

const std = @import("std");
const builtin = @import("builtin");
const locale_id = @import("locale_id.zig");
const StringTableMod = @import("StringTable.zig");
const message = @import("message.zig");

pub const StringTable = StringTableMod.StringTable;
pub const Arg = message.Arg;
pub const Value = message.Value;

/// Maximum number of distinct locale tables held at once (one per loaded
/// locale, not per fallback chain entry).
pub const MAX_TABLES = 16;
/// Maximum extra args `trn` can append to caller-supplied args.
const MAX_ARGS = 8;

default_locale: locale_id.LocaleId,
active_locale: locale_id.LocaleId,
/// Bumped on every `setLocale`. Exists only for consumers that *cache*
/// formatted text (baked meshes, world-space labels) — immediate-mode UI
/// re-fetches every frame and needs nothing.
generation: u32 = 0,

tables: [MAX_TABLES]StringTable = undefined,
table_count: usize = 0,

const Locale = @This();

pub fn init(default_locale: []const u8) Locale {
    const id = locale_id.LocaleId.parse(default_locale);
    return .{ .default_locale = id, .active_locale = id };
}

/// Register a compiled `.strtab`'s bytes for the locale declared in its own
/// header. `bytes` must outlive `Locale` (or at least outlive the last use
/// of a string returned while this table was active) — see the module doc.
/// Replaces any previously loaded table for the same locale.
pub fn loadTable(self: *Locale, bytes: []const u8) StringTableMod.Error!void {
    const table = try StringTable.init(bytes);
    for (self.tables[0..self.table_count]) |*t| {
        if (std.ascii.eqlIgnoreCase(t.locale, table.locale)) {
            t.* = table;
            return;
        }
    }
    std.debug.assert(self.table_count < MAX_TABLES);
    self.tables[self.table_count] = table;
    self.table_count += 1;
}

fn tableFor(self: *const Locale, tag: []const u8) ?*const StringTable {
    for (self.tables[0..self.table_count]) |*t| {
        if (std.ascii.eqlIgnoreCase(t.locale, tag)) return t;
    }
    return null;
}

/// Switch the active locale. Does not load anything — call `loadTable`
/// first, or rely on the fallback chain resolving to an already-loaded
/// ancestor (e.g. requesting "pt-BR" with only "pt" loaded).
pub fn setLocale(self: *Locale, tag: []const u8) void {
    self.active_locale = locale_id.LocaleId.parse(tag);
    self.generation += 1;
}

fn fallbackChain(self: *const Locale) locale_id.FallbackChain {
    return locale_id.buildFallbackChain(self.active_locale.slice(), self.default_locale.slice());
}

/// Walk the fallback chain looking up `id`; format and return the first hit
/// (allocated). If nothing matches, format `fallback_source` in the active
/// locale's own language instead — the caller decides what that source is
/// (English text for `tr`, or null-like behavior via a separate path for `key`).
fn lookupOrFormat(self: *const Locale, allocator: std.mem.Allocator, id: []const u8, args: []const Arg) !?[]u8 {
    const chain = self.fallbackChain();
    var i: usize = 0;
    while (i < chain.count) : (i += 1) {
        const tag = chain.get(i);
        const table = self.tableFor(tag) orelse continue;
        if (table.get(id)) |msg| {
            return try message.formatAlloc(allocator, tag, msg, args);
        }
    }
    return null;
}

fn withExtraArg(args: []const Arg, extra: Arg, buf: *[MAX_ARGS]Arg) []const Arg {
    std.debug.assert(args.len < MAX_ARGS);
    @memcpy(buf[0..args.len], args);
    buf[args.len] = extra;
    return buf[0 .. args.len + 1];
}

/// Source-keyed UI text. `msg` must be a literal — a non-literal argument is
/// a compile error, which is the entire point: extraction completeness
/// becomes a build failure, not a QA finding.
pub fn tr(self: *const Locale, allocator: std.mem.Allocator, comptime msg: []const u8, args: []const Arg) ![]u8 {
    if (try self.lookupOrFormat(allocator, msg, args)) |s| return s;
    return message.formatAlloc(allocator, self.active_locale.language(), msg, args);
}

/// Source-keyed UI text disambiguated by a translator-facing `ctx` (two
/// identical English strings meaning different things, e.g. "Open" the verb
/// vs. a door state).
pub fn trc(self: *const Locale, allocator: std.mem.Allocator, comptime ctx: []const u8, comptime msg: []const u8, args: []const Arg) ![]u8 {
    const id = comptime ctx ++ "\x04" ++ msg;
    if (try self.lookupOrFormat(allocator, id, args)) |s| return s;
    return message.formatAlloc(allocator, self.active_locale.language(), msg, args);
}

/// Source-keyed plural text: `one`/`other` are the English forms, `n` is the
/// count. Synthesizes the ICU plural pattern (`{n, plural, one {..} other
/// {..}}`) as both the lookup id and the English fallback, so a missing
/// translation still pluralizes correctly against English's own CLDR rule.
pub fn trn(self: *const Locale, allocator: std.mem.Allocator, comptime one: []const u8, comptime other: []const u8, n: u64, args: []const Arg) ![]u8 {
    const pattern = comptime "{n, plural, one {" ++ one ++ "} other {" ++ other ++ "}}";
    var buf: [MAX_ARGS]Arg = undefined;
    const full_args = withExtraArg(args, .{ .name = "n", .value = .{ .number = n } }, &buf);
    if (try self.lookupOrFormat(allocator, pattern, full_args)) |s| return s;
    return message.formatAlloc(allocator, self.active_locale.language(), pattern, full_args);
}

/// Id-keyed game content. Missing translation: `⟦id⟧` in debug builds,
/// bare `id` in release — deliberately not run through the formatter, since
/// there is no guaranteed-safe source text to format for an arbitrary id.
pub fn key(self: *const Locale, allocator: std.mem.Allocator, id: []const u8, args: []const Arg) ![]u8 {
    if (try self.lookupOrFormat(allocator, id, args)) |s| return s;
    if (builtin.mode == .Debug) {
        return std.fmt.allocPrint(allocator, "\u{27E6}{s}\u{27E7}", .{id});
    }
    return allocator.dupe(u8, id);
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

const testing = std.testing;

fn tableBytes(allocator: std.mem.Allocator, tag: []const u8, units: []const StringTableMod.Unit) ![]u8 {
    return StringTableMod.encode(allocator, tag, units);
}

test "tr falls back to English source when nothing is loaded" {
    var loc = Locale.init("en");
    const s = try loc.tr(testing.allocator, "Open Scene…", &.{});
    defer testing.allocator.free(s);
    try testing.expectEqualStrings("Open Scene…", s);
}

test "tr resolves a translation from the active locale table" {
    var loc = Locale.init("en");
    const pt = try tableBytes(testing.allocator, "pt", &.{.{ .id = "Open Scene…", .value = "Abrir Cena…" }});
    defer testing.allocator.free(pt);
    try loc.loadTable(pt);
    loc.setLocale("pt-BR");

    const s = try loc.tr(testing.allocator, "Open Scene…", &.{});
    defer testing.allocator.free(s);
    try testing.expectEqualStrings("Abrir Cena…", s);
}

test "tr falls back through region -> language -> default" {
    var loc = Locale.init("en");
    loc.setLocale("pt-BR");
    // No "pt-BR" or "pt" table loaded at all: falls all the way to English source.
    const s = try loc.tr(testing.allocator, "Cancel", &.{});
    defer testing.allocator.free(s);
    try testing.expectEqualStrings("Cancel", s);
}

test "trc disambiguates by context" {
    var loc = Locale.init("en");
    const pt = try tableBytes(testing.allocator, "pt", &.{
        .{ .id = "door\x04Open", .value = "Aberta" },
        .{ .id = "verb\x04Open", .value = "Abrir" },
    });
    defer testing.allocator.free(pt);
    try loc.loadTable(pt);
    loc.setLocale("pt");

    const door = try loc.trc(testing.allocator, "door", "Open", &.{});
    defer testing.allocator.free(door);
    try testing.expectEqualStrings("Aberta", door);

    const verb = try loc.trc(testing.allocator, "verb", "Open", &.{});
    defer testing.allocator.free(verb);
    try testing.expectEqualStrings("Abrir", verb);
}

test "trn pluralizes the English fallback using English CLDR rules" {
    var loc = Locale.init("en");
    const one = try loc.trn(testing.allocator, "# file", "# files", 1, &.{});
    defer testing.allocator.free(one);
    try testing.expectEqualStrings("1 file", one);

    const many = try loc.trn(testing.allocator, "# file", "# files", 3, &.{});
    defer testing.allocator.free(many);
    try testing.expectEqualStrings("3 files", many);
}

test "trn resolves through a loaded table using the target language's plural rule" {
    var loc = Locale.init("en");
    const ru_pattern = "{n, plural, one {# file} other {# files}}";
    const ru = try tableBytes(testing.allocator, "ru", &.{
        .{ .id = ru_pattern, .value = "{n, plural, one {# файл} few {# файла} many {# файлов} other {# файла}}" },
    });
    defer testing.allocator.free(ru);
    try loc.loadTable(ru);
    loc.setLocale("ru");

    const s = try loc.trn(testing.allocator, "# file", "# files", 2, &.{});
    defer testing.allocator.free(s);
    try testing.expectEqualStrings("2 файла", s);
}

test "key falls back to bracketed marker in debug, bare id otherwise" {
    var loc = Locale.init("en");
    const s = try loc.key(testing.allocator, "dlg.act1.intro", &.{});
    defer testing.allocator.free(s);
    if (builtin.mode == .Debug) {
        try testing.expectEqualStrings("\u{27E6}dlg.act1.intro\u{27E7}", s);
    } else {
        try testing.expectEqualStrings("dlg.act1.intro", s);
    }
}

test "key resolves a designer-authored id" {
    var loc = Locale.init("en");
    const ja = try tableBytes(testing.allocator, "ja", &.{.{ .id = "dlg.act1.intro", .value = "こんにちは" }});
    defer testing.allocator.free(ja);
    try loc.loadTable(ja);
    loc.setLocale("ja");

    const s = try loc.key(testing.allocator, "dlg.act1.intro", &.{});
    defer testing.allocator.free(s);
    try testing.expectEqualStrings("こんにちは", s);
}

test "setLocale bumps generation and does not free previously loaded tables" {
    var loc = Locale.init("en");
    const pt = try tableBytes(testing.allocator, "pt", &.{.{ .id = "Cancel", .value = "Cancelar" }});
    defer testing.allocator.free(pt);
    try loc.loadTable(pt);

    try testing.expectEqual(@as(u32, 0), loc.generation);
    loc.setLocale("pt");
    try testing.expectEqual(@as(u32, 1), loc.generation);
    loc.setLocale("en");
    try testing.expectEqual(@as(u32, 2), loc.generation);

    // "pt" table is still loaded and reachable after switching away and back.
    loc.setLocale("pt");
    const s = try loc.tr(testing.allocator, "Cancel", &.{});
    defer testing.allocator.free(s);
    try testing.expectEqualStrings("Cancelar", s);
}

test "loadTable replaces an existing table for the same locale" {
    var loc = Locale.init("en");
    const first = try tableBytes(testing.allocator, "pt", &.{.{ .id = "Cancel", .value = "Old" }});
    defer testing.allocator.free(first);
    try loc.loadTable(first);
    const second = try tableBytes(testing.allocator, "pt", &.{.{ .id = "Cancel", .value = "Cancelar" }});
    defer testing.allocator.free(second);
    try loc.loadTable(second);

    try testing.expectEqual(@as(usize, 1), loc.table_count);
    loc.setLocale("pt");
    const s = try loc.tr(testing.allocator, "Cancel", &.{});
    defer testing.allocator.free(s);
    try testing.expectEqualStrings("Cancelar", s);
}
