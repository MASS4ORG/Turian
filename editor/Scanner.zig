const std = @import("std");
const engine = @import("engine");
pub const ComponentDef = @import("types/ComponentDef.zig").ComponentDef;
pub const DefKind = @import("types/ComponentDef.zig").DefKind;

pub const MAX_COMP_NAME = @import("types/ComponentDef.zig").MAX_COMP_NAME;
pub const MAX_COMP_FILE = @import("types/ComponentDef.zig").MAX_COMP_FILE;
pub const MAX_COMPONENTS = @import("types/ComponentDef.zig").MAX_COMPONENTS;
pub const MAX_COMP_FIELDS = @import("types/ComponentDef.zig").MAX_COMP_FIELDS;
pub const MAX_FIELD_NAME_LEN = @import("types/FieldDef.zig").MAX_FIELD_NAME_LEN;

pub const FieldDef = @import("types/FieldDef.zig").FieldDef;

const log = std.log.scoped(.scanner);

/// Marks a struct as a discoverable user component.
pub const COMPONENT_MARKER = "is_component";
/// Marks a struct as a discoverable data asset.
pub const DATA_ASSET_MARKER = "is_data_asset";

/// Fills result[] with builtin component defs from the engine's static list.
pub fn populateBuiltins(result: []ComponentDef, result_count: *usize) void {
    for (engine.BUILTIN_COMPONENTS) |b| {
        if (result_count.* >= result.len) break;
        var def = &result[result_count.*];
        def.* = .{ .is_builtin = true };
        def.setTypeName(b.type_name);
        result_count.* += 1;
    }
}

/// Scans a directory (recursively) for .zig files declaring component types,
/// appending discovered user component types to result[].
///
/// A type is a component when it declares the `is_component` marker, e.g.:
///     pub const Player = struct {
///         pub const is_component = true;
///         ...
///     };
/// Discovery parses each file with `std.zig.Ast`, so it is robust to
/// formatting, comments, and conditional compilation — unlike regex scanning.
pub fn scanAssetsDir(
    io: std.Io,
    allocator: std.mem.Allocator,
    assets_path: []const u8,
    result: []ComponentDef,
    result_count: *usize,
) void {
    var dir = std.Io.Dir.cwd().openDir(io, assets_path, .{ .iterate = true }) catch return;
    defer dir.close(io);
    scanDirInner(io, allocator, &dir, assets_path, result, result_count);
}

fn scanDirInner(
    io: std.Io,
    allocator: std.mem.Allocator,
    dir: *std.Io.Dir,
    dir_path: []const u8,
    result: []ComponentDef,
    result_count: *usize,
) void {
    var iter = dir.iterate();
    while (iter.next(io) catch null) |entry| {
        if (result_count.* >= result.len) return;
        if (entry.kind == .directory) {
            var sub = dir.openDir(io, entry.name, .{ .iterate = true }) catch continue;
            defer sub.close(io);
            var sub_path_buf: [512]u8 = undefined;
            const sub_path = std.fmt.bufPrint(&sub_path_buf, "{s}/{s}", .{ dir_path, entry.name }) catch continue;
            scanDirInner(io, allocator, &sub, sub_path, result, result_count);
        } else if (entry.kind == .file and std.mem.endsWith(u8, entry.name, ".zig")) {
            scanFile(io, allocator, dir, entry.name, dir_path, result, result_count);
        }
    }
}

fn scanFile(
    io: std.Io,
    allocator: std.mem.Allocator,
    dir: *std.Io.Dir,
    file_name: []const u8,
    dir_path: []const u8,
    result: []ComponentDef,
    result_count: *usize,
) void {
    var path_buf: [MAX_COMP_FILE]u8 = undefined;
    const full_path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ dir_path, file_name }) catch return;

    var file = dir.openFile(io, file_name, .{}) catch return;
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var file_reader = file.reader(io, &fbuf);
    const content = file_reader.interface.allocRemaining(allocator, .unlimited) catch return;
    defer allocator.free(content);

    // The Zig parser requires a NUL-terminated source buffer.
    const source = allocator.dupeZ(u8, content) catch return;
    defer allocator.free(source);

    scanSource(allocator, source, full_path, result, result_count);
}

/// Parses one Zig source buffer and appends every component/data-asset type it declares.
/// Split out from `scanFile` so it can be unit-tested without touching disk.
fn scanSource(
    allocator: std.mem.Allocator,
    source: [:0]const u8,
    file_path: []const u8,
    result: []ComponentDef,
    result_count: *usize,
) void {
    var ast = std.zig.Ast.parse(allocator, source, .zig) catch {
        log.warn("{s}: out of memory while parsing; skipping", .{file_path});
        return;
    };
    defer ast.deinit(allocator);

    if (ast.errors.len > 0) {
        log.warn("{s}: {d} parse error(s); component discovery skipped for this file", .{ file_path, ast.errors.len });
        return;
    }

    var buf: [2]std.zig.Ast.Node.Index = undefined;
    for (ast.rootDecls()) |decl| {
        if (result_count.* >= result.len) return;

        const var_decl = ast.fullVarDecl(decl) orelse continue;
        const init_node = var_decl.ast.init_node.unwrap() orelse continue;
        const container = ast.fullContainerDecl(&buf, init_node) orelse continue;

        const is_comp = markerValue(&ast, container, COMPONENT_MARKER);
        const is_da = markerValue(&ast, container, DATA_ASSET_MARKER);

        if (!is_comp and !is_da) continue;

        const type_name = ast.tokenSlice(var_decl.ast.mut_token + 1);

        if (is_comp and is_da) {
            if (!@import("builtin").is_test) log.err(
                "'{s}' in {s} declares both is_component and is_data_asset — only one allowed",
                .{ type_name, file_path },
            );
            continue;
        }

        if (findDuplicate(result, result_count.*, type_name)) |other| {
            if (!@import("builtin").is_test) log.err(
                "duplicate '{s}' in {s}; already defined in {s} — rename one of them",
                .{ type_name, file_path, other.sourceFile() },
            );
            continue;
        }

        var def = &result[result_count.*];
        def.* = .{ .is_builtin = false, .kind = if (is_da) .data_asset else .component };
        def.setTypeName(type_name);
        def.setSourceFile(file_path);
        result_count.* += 1;
    }
}

/// Returns true when a struct body contains `marker_name` set to a value
/// other than the `false` literal.
fn markerValue(ast: *const std.zig.Ast, container: std.zig.Ast.full.ContainerDecl, marker_name: []const u8) bool {
    for (container.ast.members) |member| {
        const member_decl = ast.fullVarDecl(member) orelse continue;
        const name = ast.tokenSlice(member_decl.ast.mut_token + 1);
        if (!std.mem.eql(u8, name, marker_name)) continue;

        const value_node = member_decl.ast.init_node.unwrap() orelse return true;
        const value = ast.tokenSlice(ast.nodeMainToken(value_node));
        return !std.mem.eql(u8, value, "false");
    }
    return false;
}

/// Finds an already-discovered component with the same type name, if any.
/// Duplicate names break the generated `LiveComponent` union, so callers
/// reject the second occurrence with an actionable error.
fn findDuplicate(result: []const ComponentDef, count: usize, type_name: []const u8) ?*const ComponentDef {
    for (result[0..count]) |*def| {
        if (std.mem.eql(u8, def.typeName(), type_name)) return def;
    }
    return null;
}

test "scanSource discovers marked components and ignores helpers" {
    const a = std.testing.allocator;
    const src =
        \\const std = @import("std");
        \\const engine = @import("engine");
        \\
        \\pub const Player = struct {
        \\    pub const is_component = true;
        \\    health: i32 = 100,
        \\};
        \\
        \\// A plain helper struct — no marker, not a component.
        \\pub const Helper = struct {
        \\    x: f32 = 0,
        \\};
        \\
        \\pub const Rotator = struct { // comments do not confuse the parser
        \\    pub const is_component = true;
        \\    speed: f32 = 45.0,
        \\};
    ;
    var result: [MAX_COMPONENTS]ComponentDef = undefined;
    var count: usize = 0;
    scanSource(a, src, "assets/Player.zig", &result, &count);

    try std.testing.expectEqual(@as(usize, 2), count);
    try std.testing.expectEqualStrings("Player", result[0].typeName());
    try std.testing.expectEqualStrings("assets/Player.zig", result[0].sourceFile());
    try std.testing.expectEqual(DefKind.component, result[0].kind);
    try std.testing.expectEqualStrings("Rotator", result[1].typeName());
    try std.testing.expectEqual(DefKind.component, result[1].kind);
}

test "scanSource discovers data asset marker" {
    const a = std.testing.allocator;
    const src =
        \\pub const EnemyStats = struct {
        \\    pub const is_data_asset = true;
        \\    max_health: f32 = 100,
        \\    move_speed: f32 = 5,
        \\};
        \\pub const Ignored = struct {
        \\    x: f32 = 0,
        \\};
    ;
    var result: [MAX_COMPONENTS]ComponentDef = undefined;
    var count: usize = 0;
    scanSource(a, src, "assets/EnemyStats.zig", &result, &count);

    try std.testing.expectEqual(@as(usize, 1), count);
    try std.testing.expectEqualStrings("EnemyStats", result[0].typeName());
    try std.testing.expectEqual(DefKind.data_asset, result[0].kind);
}

test "scanSource respects is_data_asset = false opt-out" {
    const a = std.testing.allocator;
    const src =
        \\pub const Disabled = struct {
        \\    pub const is_data_asset = false;
        \\    x: f32 = 0,
        \\};
    ;
    var result: [MAX_COMPONENTS]ComponentDef = undefined;
    var count: usize = 0;
    scanSource(a, src, "assets/Disabled.zig", &result, &count);
    try std.testing.expectEqual(@as(usize, 0), count);
}

test "scanSource rejects type with both markers" {
    const a = std.testing.allocator;
    const src =
        \\pub const Both = struct {
        \\    pub const is_component = true;
        \\    pub const is_data_asset = true;
        \\};
    ;
    var result: [MAX_COMPONENTS]ComponentDef = undefined;
    var count: usize = 0;
    scanSource(a, src, "assets/Both.zig", &result, &count);
    try std.testing.expectEqual(@as(usize, 0), count);
}

test "scanSource discovers mixed component and data-asset types" {
    const a = std.testing.allocator;
    const src =
        \\pub const MyComp = struct {
        \\    pub const is_component = true;
        \\    speed: f32 = 1,
        \\};
        \\pub const MyData = struct {
        \\    pub const is_data_asset = true;
        \\    value: i32 = 42,
        \\};
    ;
    var result: [MAX_COMPONENTS]ComponentDef = undefined;
    var count: usize = 0;
    scanSource(a, src, "assets/Mixed.zig", &result, &count);
    try std.testing.expectEqual(@as(usize, 2), count);
    try std.testing.expectEqual(DefKind.component, result[0].kind);
    try std.testing.expectEqual(DefKind.data_asset, result[1].kind);
}

test "scanSource respects is_component = false opt-out" {
    const a = std.testing.allocator;
    const src =
        \\pub const Disabled = struct {
        \\    pub const is_component = false;
        \\    x: f32 = 0,
        \\};
    ;
    var result: [MAX_COMPONENTS]ComponentDef = undefined;
    var count: usize = 0;
    scanSource(a, src, "assets/Disabled.zig", &result, &count);
    try std.testing.expectEqual(@as(usize, 0), count);
}

test "scanSource rejects duplicate component names" {
    const a = std.testing.allocator;
    const src =
        \\pub const Player = struct {
        \\    pub const is_component = true;
        \\};
        \\pub const Player = struct {
        \\    pub const is_component = true;
        \\};
    ;
    var result: [MAX_COMPONENTS]ComponentDef = undefined;
    var count: usize = 0;
    scanSource(a, src, "assets/Player.zig", &result, &count);
    try std.testing.expectEqual(@as(usize, 1), count);
}

test "scanSource skips files that fail to parse" {
    const a = std.testing.allocator;
    const src =
        \\pub const Broken = struct {
        \\    pub const is_component = true;
        \\    this is not valid zig @#$
    ;
    var result: [MAX_COMPONENTS]ComponentDef = undefined;
    var count: usize = 0;
    scanSource(a, src, "assets/Broken.zig", &result, &count);
    try std.testing.expectEqual(@as(usize, 0), count);
}
