//! Scans user script source files for UI event type declarations
//! (`pub const event_name = "..."` in a struct) — feeds the inspector's
//! event dropdown. Uses `std.zig.Ast` like `Scanner.zig`; kept separate
//! since this is Studio-only edit-time tooling.

const std = @import("std");

pub const MAX_EVENT_NAME_LEN = 64;
pub const MAX_EVENTS = 128;

const EVENT_MARKER = "event_name";

pub const EventDef = struct {
    name_buf: [MAX_EVENT_NAME_LEN]u8 = undefined,
    name_len: usize = 0,

    pub fn name(self: *const EventDef) []const u8 {
        return self.name_buf[0..self.name_len];
    }

    fn setName(self: *EventDef, s: []const u8) void {
        const n = @min(s.len, self.name_buf.len);
        @memcpy(self.name_buf[0..n], s[0..n]);
        self.name_len = n;
    }
};

/// Scans a directory (recursively) for .zig files declaring event types,
/// appending discovered (deduplicated) event names to result[].
pub fn scanEventNames(
    io: std.Io,
    allocator: std.mem.Allocator,
    assets_path: []const u8,
    result: []EventDef,
    result_count: *usize,
) void {
    var dir = std.Io.Dir.cwd().openDir(io, assets_path, .{ .iterate = true }) catch return;
    defer dir.close(io);
    scanDirInner(io, allocator, &dir, result, result_count);
}

fn scanDirInner(
    io: std.Io,
    allocator: std.mem.Allocator,
    dir: *std.Io.Dir,
    result: []EventDef,
    result_count: *usize,
) void {
    var iter = dir.iterate();
    while (iter.next(io) catch null) |entry| {
        if (result_count.* >= result.len) return;
        if (entry.kind == .directory) {
            var sub = dir.openDir(io, entry.name, .{ .iterate = true }) catch continue;
            defer sub.close(io);
            scanDirInner(io, allocator, &sub, result, result_count);
        } else if (entry.kind == .file and std.mem.endsWith(u8, entry.name, ".zig")) {
            scanFile(io, allocator, dir, entry.name, result, result_count);
        }
    }
}

fn scanFile(
    io: std.Io,
    allocator: std.mem.Allocator,
    dir: *std.Io.Dir,
    file_name: []const u8,
    result: []EventDef,
    result_count: *usize,
) void {
    var file = dir.openFile(io, file_name, .{}) catch return;
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var file_reader = file.reader(io, &fbuf);
    const content = file_reader.interface.allocRemaining(allocator, .unlimited) catch return;
    defer allocator.free(content);

    const source = allocator.dupeZ(u8, content) catch return;
    defer allocator.free(source);

    scanSource(allocator, source, result, result_count);
}

/// Parses one Zig source buffer and appends every event name it declares.
/// Split out from `scanFile` so it can be unit-tested without touching disk.
fn scanSource(
    allocator: std.mem.Allocator,
    source: [:0]const u8,
    result: []EventDef,
    result_count: *usize,
) void {
    var ast = std.zig.Ast.parse(allocator, source, .zig) catch return;
    defer ast.deinit(allocator);
    if (ast.errors.len > 0) return;

    var buf: [2]std.zig.Ast.Node.Index = undefined;
    for (ast.rootDecls()) |decl| {
        if (result_count.* >= result.len) return;

        const var_decl = ast.fullVarDecl(decl) orelse continue;
        const init_node = var_decl.ast.init_node.unwrap() orelse continue;
        const container = ast.fullContainerDecl(&buf, init_node) orelse continue;

        const value = eventNameValue(allocator, &ast, container) orelse continue;
        defer allocator.free(value);
        if (value.len == 0) continue;

        if (findDuplicate(result, result_count.*, value) != null) continue;

        var def: EventDef = .{};
        def.setName(value);
        result[result_count.*] = def;
        result_count.* += 1;
    }
}

/// Returns the unescaped string value of `event_name`'s string-literal
/// initializer, or null if the struct has no such member (or it isn't a
/// plain string literal).
fn eventNameValue(allocator: std.mem.Allocator, ast: *const std.zig.Ast, container: std.zig.Ast.full.ContainerDecl) ?[]u8 {
    for (container.ast.members) |member| {
        const member_decl = ast.fullVarDecl(member) orelse continue;
        const name = ast.tokenSlice(member_decl.ast.mut_token + 1);
        if (!std.mem.eql(u8, name, EVENT_MARKER)) continue;

        const value_node = member_decl.ast.init_node.unwrap() orelse return null;
        if (ast.nodeTag(value_node) != .string_literal) return null;
        const raw = ast.tokenSlice(ast.nodeMainToken(value_node));
        return std.zig.string_literal.parseAlloc(allocator, raw) catch null;
    }
    return null;
}

fn findDuplicate(result: []const EventDef, count: usize, value: []const u8) ?*const EventDef {
    for (result[0..count]) |*e| {
        if (std.mem.eql(u8, e.name(), value)) return e;
    }
    return null;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test "scanSource finds a single event_name declaration" {
    const source =
        \\pub const PlayClicked = struct {
        \\    pub const event_name = "play_clicked";
        \\};
    ;
    var result: [8]EventDef = undefined;
    var count: usize = 0;
    scanSource(std.testing.allocator, source, &result, &count);
    try std.testing.expectEqual(@as(usize, 1), count);
    try std.testing.expectEqualStrings("play_clicked", result[0].name());
}

test "scanSource finds multiple event types and skips non-event structs" {
    const source =
        \\pub const PlayClicked = struct {
        \\    pub const event_name = "play_clicked";
        \\};
        \\pub const QuitClicked = struct {
        \\    pub const event_name = "quit_clicked";
        \\};
        \\pub const NotAnEvent = struct {
        \\    pub const is_component = true;
        \\};
    ;
    var result: [8]EventDef = undefined;
    var count: usize = 0;
    scanSource(std.testing.allocator, source, &result, &count);
    try std.testing.expectEqual(@as(usize, 2), count);
    try std.testing.expectEqualStrings("play_clicked", result[0].name());
    try std.testing.expectEqualStrings("quit_clicked", result[1].name());
}

test "scanSource deduplicates the same event name declared twice" {
    const source =
        \\pub const A = struct {
        \\    pub const event_name = "jump_clicked";
        \\};
        \\pub const B = struct {
        \\    pub const event_name = "jump_clicked";
        \\};
    ;
    var result: [8]EventDef = undefined;
    var count: usize = 0;
    scanSource(std.testing.allocator, source, &result, &count);
    try std.testing.expectEqual(@as(usize, 1), count);
}

test "scanSource ignores files with parse errors instead of crashing" {
    const source = "pub const Broken = struct { pub const event_name = ";
    var result: [8]EventDef = undefined;
    var count: usize = 0;
    scanSource(std.testing.allocator, source, &result, &count);
    try std.testing.expectEqual(@as(usize, 0), count);
}

test "scanSource ignores a non-string event_name value" {
    const source =
        \\pub const Weird = struct {
        \\    pub const event_name = 42;
        \\};
    ;
    var result: [8]EventDef = undefined;
    var count: usize = 0;
    scanSource(std.testing.allocator, source, &result, &count);
    try std.testing.expectEqual(@as(usize, 0), count);
}
