const std = @import("std");
const engine = @import("engine");
const EditorState = @import("../services/EditorState.zig");

const FieldHint = engine.FieldHint;

pub fn canHaveDecls(comptime T: type) bool {
    return switch (@typeInfo(T)) {
        .@"struct", .@"union", .@"enum", .@"opaque" => true,
        else => false,
    };
}

pub fn fieldHint(comptime T: type, comptime name: []const u8) FieldHint {
    if (@hasDecl(T, "turian_hints") and @hasDecl(T.turian_hints, name))
        return @field(T.turian_hints, name);
    return .{};
}

pub fn displayLabel(comptime field_name: []const u8, hint: FieldHint) []const u8 {
    if (hint.label) |l| return l;
    return comptime titleCase(field_name);
}

pub fn titleCase(comptime name: []const u8) []const u8 {
    comptime {
        var buf: [name.len]u8 = undefined;
        for (name, 0..) |c, i| buf[i] = if (c == '_') ' ' else c;
        if (buf.len > 0) buf[0] = std.ascii.toUpper(buf[0]);
        const final = buf;
        return &final;
    }
}

pub fn castHintBound(comptime T: type, val: f64) T {
    return switch (@typeInfo(T)) {
        .float => @floatCast(val),
        .int => @intFromFloat(@trunc(val)),
        else => unreachable,
    };
}

pub fn guidDisplayName(kind: engine.api.FieldType, guid_str: []const u8) []const u8 {
    if (guid_str.len == 0) return "(none)";
    const opt: ?[]const u8 = switch (kind) {
        .asset_ref => EditorState.resolveAssetGuid(guid_str),
        .game_object_ref, .component_ref => EditorState.resolveObjectGuid(guid_str),
        else => null,
    };
    const resolved = opt orelse return guid_str;
    return if (std.mem.lastIndexOfScalar(u8, resolved, '/')) |sep|
        resolved[sep + 1 ..]
    else
        resolved;
}
