//! ICU MessageFormat subset. Parses and formats in a single pass, supporting
//! simple substitution, CLDR cardinal plurals, exact-value branches, select,
//! and escaped literals. No nested argument types beyond plural/select,
//! `selectordinal`, or date-time formatters.

const std = @import("std");
const plurals = @import("plurals.zig");

/// A message argument value. Plural arguments must be `.number`; select
/// arguments must be `.text`.
pub const Value = union(enum) {
    text: []const u8,
    number: u64,
};

pub const Arg = struct {
    name: []const u8,
    value: Value,
};

pub fn findArg(args: []const Arg, name: []const u8) ?Value {
    for (args) |a| if (std.mem.eql(u8, a.name, name)) return a.value;
    return null;
}

/// Format `msg` (ICU-subset) into `writer`, resolving `{name}` placeholders
/// and plural/select branches against `args`, using `language`'s CLDR
/// cardinal rule for plural category selection.
pub fn format(writer: *std.Io.Writer, language: []const u8, msg: []const u8, args: []const Arg) std.Io.Writer.Error!void {
    try formatInto(writer, language, msg, args, null);
}

/// Format into a freshly allocated string. Caller frees with `allocator.free`.
pub fn formatAlloc(allocator: std.mem.Allocator, language: []const u8, msg: []const u8, args: []const Arg) ![]u8 {
    var out: std.Io.Writer.Allocating = .init(allocator);
    defer out.deinit();
    format(&out.writer, language, msg, args) catch |err| switch (err) {
        error.WriteFailed => return error.OutOfMemory,
    };
    return out.toOwnedSlice();
}

fn formatInto(writer: *std.Io.Writer, language: []const u8, msg: []const u8, args: []const Arg, hash_value: ?u64) std.Io.Writer.Error!void {
    var pos: usize = 0;
    while (pos < msg.len) {
        const c = msg[pos];
        switch (c) {
            '\'' => {
                pos += 1;
                if (pos < msg.len and msg[pos] == '\'') {
                    try writer.writeByte('\'');
                    pos += 1;
                    continue;
                }
                const start = pos;
                while (pos < msg.len and msg[pos] != '\'') pos += 1;
                try writer.writeAll(msg[start..pos]);
                if (pos < msg.len) pos += 1;
            },
            '{' => {
                pos += 1;
                pos = try formatArgument(writer, language, msg, pos, args);
            },
            '#' => {
                if (hash_value) |n| {
                    try writer.print("{d}", .{n});
                } else {
                    try writer.writeByte('#');
                }
                pos += 1;
            },
            else => {
                try writer.writeByte(c);
                pos += 1;
            },
        }
    }
}

/// `pos` is just past the opening `{`. Returns the position just past the
/// argument's closing `}`.
fn formatArgument(writer: *std.Io.Writer, language: []const u8, msg: []const u8, pos_in: usize, args: []const Arg) std.Io.Writer.Error!usize {
    var pos = pos_in;
    const name_start = pos;
    while (pos < msg.len and msg[pos] != ',' and msg[pos] != '}') pos += 1;
    const name = std.mem.trim(u8, msg[name_start..pos], " \t\n\r");
    if (pos >= msg.len) return pos;

    if (msg[pos] == '}') {
        if (findArg(args, name)) |v| {
            switch (v) {
                .text => |s| try writer.writeAll(s),
                .number => |n| try writer.print("{d}", .{n}),
            }
        } else {
            try writer.writeByte('{');
            try writer.writeAll(name);
            try writer.writeByte('}');
        }
        return pos + 1;
    }

    // msg[pos] == ','
    pos += 1;
    pos = skipWs(msg, pos);
    const kw_start = pos;
    while (pos < msg.len and msg[pos] != ',' and msg[pos] != '}') pos += 1;
    const keyword = std.mem.trim(u8, msg[kw_start..pos], " \t\n\r");
    if (pos < msg.len and msg[pos] == ',') pos += 1;

    if (std.mem.eql(u8, keyword, "plural")) {
        return formatPlural(writer, language, msg, pos, name, args);
    } else if (std.mem.eql(u8, keyword, "select")) {
        return formatSelect(writer, language, msg, pos, name, args);
    }
    // Unsupported sub-format: skip its branches without emitting anything.
    return skipBranches(msg, pos);
}

const Branch = struct { keyword: []const u8, content: []const u8 };
const MAX_BRANCHES = 16;

fn parseBranches(msg: []const u8, pos_in: usize, out: *[MAX_BRANCHES]Branch) struct { count: usize, next: usize } {
    var pos = skipWs(msg, pos_in);
    var count: usize = 0;
    while (pos < msg.len and msg[pos] != '}') {
        const kw_start = pos;
        while (pos < msg.len and msg[pos] != '{' and !isWs(msg[pos])) pos += 1;
        const keyword = msg[kw_start..pos];
        pos = skipWs(msg, pos);
        if (pos >= msg.len or msg[pos] != '{') break;
        const end = findMatchingBrace(msg, pos);
        const content = msg[pos + 1 .. if (end > pos + 1) end - 1 else pos + 1];
        if (count < out.len) {
            out[count] = .{ .keyword = keyword, .content = content };
            count += 1;
        }
        pos = skipWs(msg, end);
    }
    if (pos < msg.len and msg[pos] == '}') pos += 1;
    return .{ .count = count, .next = pos };
}

/// Skip a sub-format's branch list without recording it (unsupported keyword path).
fn skipBranches(msg: []const u8, pos_in: usize) usize {
    var scratch: [MAX_BRANCHES]Branch = undefined;
    const r = parseBranches(msg, pos_in, &scratch);
    return r.next;
}

fn formatPlural(writer: *std.Io.Writer, language: []const u8, msg: []const u8, pos_in: usize, arg_name: []const u8, args: []const Arg) std.Io.Writer.Error!usize {
    var branches: [MAX_BRANCHES]Branch = undefined;
    const r = parseBranches(msg, pos_in, &branches);

    const n: u64 = switch (findArg(args, arg_name) orelse Value{ .number = 0 }) {
        .number => |v| v,
        .text => 0,
    };
    const cat_name = @tagName(plurals.category(language, n));

    var exact_buf: [24]u8 = undefined;
    const exact_kw = std.fmt.bufPrint(&exact_buf, "={d}", .{n}) catch unreachable;

    var chosen: ?[]const u8 = null;
    for (branches[0..r.count]) |b| {
        if (std.mem.eql(u8, b.keyword, exact_kw)) {
            chosen = b.content;
            break;
        }
    }
    if (chosen == null) {
        for (branches[0..r.count]) |b| {
            if (std.mem.eql(u8, b.keyword, cat_name)) {
                chosen = b.content;
                break;
            }
        }
    }
    if (chosen == null) {
        for (branches[0..r.count]) |b| {
            if (std.mem.eql(u8, b.keyword, "other")) {
                chosen = b.content;
                break;
            }
        }
    }
    if (chosen) |content| {
        try formatInto(writer, language, content, args, n);
    }
    return r.next;
}

fn formatSelect(writer: *std.Io.Writer, language: []const u8, msg: []const u8, pos_in: usize, arg_name: []const u8, args: []const Arg) std.Io.Writer.Error!usize {
    var branches: [MAX_BRANCHES]Branch = undefined;
    const r = parseBranches(msg, pos_in, &branches);

    const selector: []const u8 = switch (findArg(args, arg_name) orelse Value{ .text = "" }) {
        .text => |s| s,
        .number => "",
    };

    var chosen: ?[]const u8 = null;
    for (branches[0..r.count]) |b| {
        if (std.mem.eql(u8, b.keyword, selector)) {
            chosen = b.content;
            break;
        }
    }
    if (chosen == null) {
        for (branches[0..r.count]) |b| {
            if (std.mem.eql(u8, b.keyword, "other")) {
                chosen = b.content;
                break;
            }
        }
    }
    if (chosen) |content| {
        try formatInto(writer, language, content, args, null);
    }
    return r.next;
}

/// `msg[open_pos] == '{'`. Returns the index just past its matching `}`,
/// treating `'...'` spans as opaque (so a literal `{`/`}` inside a quoted
/// span does not affect depth).
fn findMatchingBrace(msg: []const u8, open_pos: usize) usize {
    var i = open_pos + 1;
    var depth: usize = 1;
    while (i < msg.len and depth > 0) {
        const c = msg[i];
        if (c == '\'') {
            i += 1;
            if (i < msg.len and msg[i] == '\'') {
                i += 1;
                continue;
            }
            while (i < msg.len and msg[i] != '\'') i += 1;
            if (i < msg.len) i += 1;
            continue;
        }
        if (c == '{') depth += 1;
        if (c == '}') depth -= 1;
        i += 1;
    }
    return i;
}

fn isWs(c: u8) bool {
    return c == ' ' or c == '\t' or c == '\n' or c == '\r';
}

fn skipWs(msg: []const u8, pos_in: usize) usize {
    var pos = pos_in;
    while (pos < msg.len and isWs(msg[pos])) pos += 1;
    return pos;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

const testing = std.testing;

fn expectFormat(expected: []const u8, language: []const u8, msg: []const u8, args: []const Arg) !void {
    const got = try formatAlloc(testing.allocator, language, msg, args);
    defer testing.allocator.free(got);
    try testing.expectEqualStrings(expected, got);
}

test "plain text passes through" {
    try expectFormat("Hello, world!", "en", "Hello, world!", &.{});
}

test "simple placeholder" {
    try expectFormat("Hello, Ada!", "en", "Hello, {name}!", &.{.{ .name = "name", .value = .{ .text = "Ada" } }});
}

test "missing placeholder falls back to visible marker" {
    try expectFormat("Hello, {name}!", "en", "Hello, {name}!", &.{});
}

test "escaped literal quote" {
    try expectFormat("it's fine", "en", "it''s fine", &.{});
}

test "quoted literal span escapes braces" {
    try expectFormat("{literal}", "en", "'{literal}'", &.{});
}

test "plural: english one/other" {
    const msg = "{count, plural, one {# file} other {# files}}";
    try expectFormat("1 file", "en", msg, &.{.{ .name = "count", .value = .{ .number = 1 } }});
    try expectFormat("3 files", "en", msg, &.{.{ .name = "count", .value = .{ .number = 3 } }});
    try expectFormat("0 files", "en", msg, &.{.{ .name = "count", .value = .{ .number = 0 } }});
}

test "plural: exact-value branch takes priority over category" {
    const msg = "{count, plural, =0 {no files} one {# file} other {# files}}";
    try expectFormat("no files", "en", msg, &.{.{ .name = "count", .value = .{ .number = 0 } }});
    try expectFormat("1 file", "en", msg, &.{.{ .name = "count", .value = .{ .number = 1 } }});
}

test "plural: russian one/few/many" {
    const msg = "{n, plural, one {# файл} few {# файла} many {# файлов} other {# файла}}";
    try expectFormat("1 файл", "ru", msg, &.{.{ .name = "n", .value = .{ .number = 1 } }});
    try expectFormat("2 файла", "ru", msg, &.{.{ .name = "n", .value = .{ .number = 2 } }});
    try expectFormat("5 файлов", "ru", msg, &.{.{ .name = "n", .value = .{ .number = 5 } }});
}

test "select: gender" {
    const msg = "{gender, select, male {He} female {She} other {They}} joined";
    try expectFormat("He joined", "en", msg, &.{.{ .name = "gender", .value = .{ .text = "male" } }});
    try expectFormat("She joined", "en", msg, &.{.{ .name = "gender", .value = .{ .text = "female" } }});
    try expectFormat("They joined", "en", msg, &.{.{ .name = "gender", .value = .{ .text = "nonbinary" } }});
}

test "plural and placeholder combined" {
    const msg = "{name} has {count, plural, one {# file} other {# files}}";
    try expectFormat("Ada has 3 files", "en", msg, &.{
        .{ .name = "name", .value = .{ .text = "Ada" } },
        .{ .name = "count", .value = .{ .number = 3 } },
    });
}

test "nested select inside plural branch" {
    const msg = "{count, plural, one {# item ({kind, select, new {new} other {old}})} other {# items}}";
    try expectFormat("1 item (new)", "en", msg, &.{
        .{ .name = "count", .value = .{ .number = 1 } },
        .{ .name = "kind", .value = .{ .text = "new" } },
    });
}
