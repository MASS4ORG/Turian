//! Extracts translatable strings from Zig source by walking `std.zig.Ast`
//! (robust to formatting/comments, unlike regex scanning). Recognizes
//! `tr`/`trArgs`/`trc`/`trn`/`trKey`/`key` calls by callee identifier, so
//! it works for bare, qualified, or `frame.*` call sites. Non-literal
//! arguments are silently skipped.

const std = @import("std");

pub const Kind = enum { source_keyed, id_keyed };

pub const ExtractedUnit = struct {
    id: []const u8,
    source: []const u8,
    /// `trc`'s disambiguating context, or empty.
    context: []const u8 = "",
    /// `"file:line"` of the (first) call site, for translator-facing notes.
    note: []const u8 = "",
    kind: Kind = .source_keyed,

    pub fn deinit(self: ExtractedUnit, allocator: std.mem.Allocator) void {
        allocator.free(self.id);
        allocator.free(self.source);
        if (self.context.len != 0) allocator.free(self.context);
        if (self.note.len != 0) allocator.free(self.note);
    }
};

/// Recursively scan `dir_path` for `.zig` files and append every distinct
/// `tr`/`trc`/`trn`/`trKey` call site found. `out` is caller-owned; each
/// appended unit is caller-owned (free with `ExtractedUnit.deinit`).
pub fn extractDir(io: std.Io, allocator: std.mem.Allocator, dir_path: []const u8, out: *std.ArrayList(ExtractedUnit)) !void {
    var dir = try std.Io.Dir.cwd().openDir(io, dir_path, .{ .iterate = true });
    defer dir.close(io);
    try extractDirInner(io, allocator, &dir, dir_path, out);
}

fn extractDirInner(io: std.Io, allocator: std.mem.Allocator, dir: *std.Io.Dir, dir_path: []const u8, out: *std.ArrayList(ExtractedUnit)) !void {
    var iter = dir.iterate();
    while (try iter.next(io)) |entry| {
        if (entry.kind == .directory) {
            var sub = dir.openDir(io, entry.name, .{ .iterate = true }) catch continue;
            defer sub.close(io);
            var path_buf: [1024]u8 = undefined;
            const sub_path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ dir_path, entry.name }) catch continue;
            try extractDirInner(io, allocator, &sub, sub_path, out);
        } else if (entry.kind == .file and std.mem.endsWith(u8, entry.name, ".zig")) {
            extractFile(io, allocator, dir, entry.name, dir_path, out) catch |err| {
                std.log.scoped(.i18n_extractor).warn("{s}/{s}: {t}", .{ dir_path, entry.name, err });
            };
        }
    }
}

fn extractFile(io: std.Io, allocator: std.mem.Allocator, dir: *std.Io.Dir, file_name: []const u8, dir_path: []const u8, out: *std.ArrayList(ExtractedUnit)) !void {
    var path_buf: [1024]u8 = undefined;
    const full_path = try std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ dir_path, file_name });

    var file = try dir.openFile(io, file_name, .{});
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var file_reader = file.reader(io, &fbuf);
    const content = try file_reader.interface.allocRemaining(allocator, .unlimited);
    defer allocator.free(content);

    const source = try allocator.dupeZ(u8, content);
    defer allocator.free(source);

    try extractSource(allocator, source, full_path, out);
}

/// Parses one Zig source buffer and appends every `tr`/`trc`/`trn`/`trKey`
/// call site found anywhere in it. Split out from `extractFile` so it's
/// unit-testable without touching disk.
pub fn extractSource(allocator: std.mem.Allocator, source: [:0]const u8, file_path: []const u8, out: *std.ArrayList(ExtractedUnit)) !void {
    var tree = std.zig.Ast.parse(allocator, source, .zig) catch return;
    defer tree.deinit(allocator);
    if (tree.errors.len > 0) {
        std.log.scoped(.i18n_extractor).warn("{s}: {d} parse error(s); extraction skipped for this file", .{ file_path, tree.errors.len });
        return;
    }

    var call_buf: [1]std.zig.Ast.Node.Index = undefined;
    var i: usize = 0;
    while (i < tree.nodes.len) : (i += 1) {
        const node: std.zig.Ast.Node.Index = @enumFromInt(i);
        const call = tree.fullCall(&call_buf, node) orelse continue;
        const name = calleeName(&tree, call.ast.fn_expr) orelse continue;

        const line = lineOf(&tree, tree.nodeMainToken(node));
        var unit = (try unitFromCall(allocator, &tree, name, call.ast.params)) orelse continue;
        unit.note = try std.fmt.allocPrint(allocator, "{s}:{d}", .{ file_path, line });

        if (findDuplicate(out.items, unit.id)) |_| {
            unit.deinit(allocator);
            continue;
        }
        try out.append(allocator, unit);
    }
}

fn calleeName(tree: *const std.zig.Ast, fn_expr: std.zig.Ast.Node.Index) ?[]const u8 {
    return switch (tree.nodeTag(fn_expr)) {
        .identifier => tree.tokenSlice(tree.nodeMainToken(fn_expr)),
        .field_access => tree.tokenSlice(tree.nodeData(fn_expr).node_and_token[1]),
        else => null,
    };
}

fn unitFromCall(allocator: std.mem.Allocator, tree: *const std.zig.Ast, name: []const u8, params: []const std.zig.Ast.Node.Index) !?ExtractedUnit {
    if (std.mem.eql(u8, name, "tr") or std.mem.eql(u8, name, "trArgs")) {
        if (params.len < 1) return null;
        const msg = stringLiteral(allocator, tree, params[0]) orelse return null;
        return ExtractedUnit{ .id = try allocator.dupe(u8, msg), .source = msg, .kind = .source_keyed };
    }
    if (std.mem.eql(u8, name, "trc")) {
        if (params.len < 2) return null;
        const ctx = stringLiteral(allocator, tree, params[0]) orelse return null;
        defer allocator.free(ctx);
        const msg = stringLiteral(allocator, tree, params[1]) orelse return null;
        const id = try std.fmt.allocPrint(allocator, "{s}\x04{s}", .{ ctx, msg });
        return ExtractedUnit{ .id = id, .source = msg, .context = try allocator.dupe(u8, ctx), .kind = .source_keyed };
    }
    if (std.mem.eql(u8, name, "trn")) {
        if (params.len < 2) return null;
        const one = stringLiteral(allocator, tree, params[0]) orelse return null;
        defer allocator.free(one);
        const other = stringLiteral(allocator, tree, params[1]) orelse return null;
        defer allocator.free(other);
        const pattern = try std.fmt.allocPrint(allocator, "{{n, plural, one {{{s}}} other {{{s}}}}}", .{ one, other });
        return ExtractedUnit{ .id = pattern, .source = try allocator.dupe(u8, pattern), .kind = .source_keyed };
    }
    if (std.mem.eql(u8, name, "trKey") or std.mem.eql(u8, name, "key")) {
        if (params.len < 1) return null;
        const id = stringLiteral(allocator, tree, params[0]) orelse return null;
        return ExtractedUnit{ .id = id, .source = try allocator.dupe(u8, id), .kind = .id_keyed };
    }
    return null;
}

fn stringLiteral(allocator: std.mem.Allocator, tree: *const std.zig.Ast, node: std.zig.Ast.Node.Index) ?[]u8 {
    if (tree.nodeTag(node) != .string_literal) return null;
    const raw = tree.tokenSlice(tree.nodeMainToken(node));
    return std.zig.string_literal.parseAlloc(allocator, raw) catch null;
}

fn lineOf(tree: *const std.zig.Ast, token: std.zig.Ast.TokenIndex) usize {
    const loc = tree.tokenLocation(0, token);
    return loc.line + 1;
}

fn findDuplicate(units: []const ExtractedUnit, id: []const u8) ?*const ExtractedUnit {
    for (units) |*u| if (std.mem.eql(u8, u.id, id)) return u;
    return null;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

fn extractStr(allocator: std.mem.Allocator, source: [:0]const u8) !std.ArrayList(ExtractedUnit) {
    var out: std.ArrayList(ExtractedUnit) = .empty;
    try extractSource(allocator, source, "test.zig", &out);
    return out;
}

test "extracts a simple tr() call nested inside a function body" {
    const a = std.testing.allocator;
    const src =
        \\fn draw() void {
        \\    if (true) {
        \\        gui.label(@src(), "{s}", .{tr("Open Scene…")}, .{});
        \\    }
        \\}
    ;
    var out = try extractStr(a, src);
    defer {
        for (out.items) |u| u.deinit(a);
        out.deinit(a);
    }
    try std.testing.expectEqual(@as(usize, 1), out.items.len);
    try std.testing.expectEqualStrings("Open Scene…", out.items[0].id);
    try std.testing.expectEqualStrings("Open Scene…", out.items[0].source);
    try std.testing.expectEqual(Kind.source_keyed, out.items[0].kind);
    try std.testing.expectEqualStrings("test.zig:3", out.items[0].note);
}

test "extracts trArgs() using the same id scheme as tr()" {
    const a = std.testing.allocator;
    const src =
        \\fn draw() void {
        \\    _ = StudioLocale.trArgs("Tasks ({n})", &.{.{ .name = "n", .value = .{ .number = count } }});
        \\}
    ;
    var out = try extractStr(a, src);
    defer {
        for (out.items) |u| u.deinit(a);
        out.deinit(a);
    }
    try std.testing.expectEqual(@as(usize, 1), out.items.len);
    try std.testing.expectEqualStrings("Tasks ({n})", out.items[0].id);
    try std.testing.expectEqual(Kind.source_keyed, out.items[0].kind);
}

test "extracts a qualified StudioLocale.tr() call" {
    const a = std.testing.allocator;
    const src =
        \\fn draw() void {
        \\    _ = StudioLocale.tr("Cancel");
        \\}
    ;
    var out = try extractStr(a, src);
    defer {
        for (out.items) |u| u.deinit(a);
        out.deinit(a);
    }
    try std.testing.expectEqual(@as(usize, 1), out.items.len);
    try std.testing.expectEqualStrings("Cancel", out.items[0].id);
}

test "extracts trc() with context-disambiguated id" {
    const a = std.testing.allocator;
    const src =
        \\fn draw() void {
        \\    _ = frame.trc("door", "Open", &.{});
        \\}
    ;
    var out = try extractStr(a, src);
    defer {
        for (out.items) |u| u.deinit(a);
        out.deinit(a);
    }
    try std.testing.expectEqual(@as(usize, 1), out.items.len);
    try std.testing.expectEqualStrings("door\x04Open", out.items[0].id);
    try std.testing.expectEqualStrings("Open", out.items[0].source);
    try std.testing.expectEqualStrings("door", out.items[0].context);
}

test "extracts trn() as a synthesized ICU plural pattern" {
    const a = std.testing.allocator;
    const src =
        \\fn draw() void {
        \\    _ = frame.trn("# file", "# files", count, &.{});
        \\}
    ;
    var out = try extractStr(a, src);
    defer {
        for (out.items) |u| u.deinit(a);
        out.deinit(a);
    }
    try std.testing.expectEqual(@as(usize, 1), out.items.len);
    try std.testing.expectEqualStrings("{n, plural, one {# file} other {# files}}", out.items[0].id);
}

test "extracts trKey() as id-keyed content" {
    const a = std.testing.allocator;
    const src =
        \\fn draw() void {
        \\    _ = frame.trKey("dlg.act1.intro", &.{});
        \\}
    ;
    var out = try extractStr(a, src);
    defer {
        for (out.items) |u| u.deinit(a);
        out.deinit(a);
    }
    try std.testing.expectEqual(@as(usize, 1), out.items.len);
    try std.testing.expectEqualStrings("dlg.act1.intro", out.items[0].id);
    try std.testing.expectEqual(Kind.id_keyed, out.items[0].kind);
}

test "deduplicates repeated tr() sites with the same source text" {
    const a = std.testing.allocator;
    const src =
        \\fn a() void { _ = tr("Cancel"); }
        \\fn b() void { _ = tr("Cancel"); }
    ;
    var out = try extractStr(a, src);
    defer {
        for (out.items) |u| u.deinit(a);
        out.deinit(a);
    }
    try std.testing.expectEqual(@as(usize, 1), out.items.len);
}

test "ignores unrelated function calls" {
    const a = std.testing.allocator;
    const src =
        \\fn draw() void {
        \\    std.debug.print("not translatable", .{});
        \\    log.warn("also not", .{});
        \\}
    ;
    var out = try extractStr(a, src);
    defer {
        for (out.items) |u| u.deinit(a);
        out.deinit(a);
    }
    try std.testing.expectEqual(@as(usize, 0), out.items.len);
}

test "skips a non-literal key() argument rather than crashing" {
    const a = std.testing.allocator;
    const src =
        \\fn draw() void {
        \\    _ = frame.trKey(dynamic_id, &.{});
        \\}
    ;
    var out = try extractStr(a, src);
    defer {
        for (out.items) |u| u.deinit(a);
        out.deinit(a);
    }
    try std.testing.expectEqual(@as(usize, 0), out.items.len);
}

test "skips files with parse errors" {
    const a = std.testing.allocator;
    const src = "fn draw() void { _ = tr(";
    var out = try extractStr(a, src);
    defer {
        for (out.items) |u| u.deinit(a);
        out.deinit(a);
    }
    try std.testing.expectEqual(@as(usize, 0), out.items.len);
}
