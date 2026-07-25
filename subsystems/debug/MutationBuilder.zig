//! Parses and validates mutating JSON-RPC requests into `Server.Mutation`
//! values.
const std = @import("std");
const engine = @import("engine");
const Protocol = @import("Protocol.zig");
const Server = @import("Server.zig");
const introspect = engine.introspect;

const Request = Protocol.Request;

/// Wire names of the methods that mutate live engine state. The server routes
/// these through `buildMutation` + the host applier instead of `Handler.dispatch`.
pub const mutating_methods = [_][]const u8{
    "component.set",
    "transform.set",
    "entity.spawn",
    "entity.destroy",
    "asset.reload",
    "input.mouseMove",
    "input.click",
    "input.key",
    "input.text",
    "screenshot.capture",
    "camera.set",
    "viewport.setTab",
};

/// True if `method` is a mutating method (handled via the applier, not dispatch).
pub fn isMutation(method: []const u8) bool {
    for (mutating_methods) |mm| {
        if (std.mem.eql(u8, method, mm)) return true;
    }
    return false;
}

pub const MutationError = error{ InvalidParams, OutOfMemory };

/// Parses and validates a mutating request into a `Server.Mutation`. Every
/// borrowed string is duped into `arena`, so the result is valid for as long as
/// `arena` lives (the caller frees it after the applier returns).
pub fn buildMutation(arena: std.mem.Allocator, req: *const Request) MutationError!Server.Mutation {
    const parsed = std.json.parseFromSlice(std.json.Value, arena, req.params(), .{}) catch
        return error.InvalidParams;
    defer parsed.deinit();
    const obj = if (parsed.value == .object) parsed.value.object else return error.InvalidParams;
    const m = req.method();

    if (std.mem.eql(u8, m, "component.set")) {
        const value = try jsonToValue(arena, obj.get("value") orelse return error.InvalidParams);
        return .{ .set_component = .{
            .entity = try dupField(arena, obj, "entity"),
            .component = try dupField(arena, obj, "component"),
            .field = try dupField(arena, obj, "field"),
            .value = value,
        } };
    }
    if (std.mem.eql(u8, m, "transform.set")) {
        return .{ .set_transform = .{
            .channel = try dupField(arena, obj, "channel"),
            .value = try jsonToVec3(obj.get("value") orelse return error.InvalidParams),
            .entity = try dupField(arena, obj, "entity"),
        } };
    }
    if (std.mem.eql(u8, m, "entity.spawn")) {
        return .{ .spawn = .{ .name = try dupField(arena, obj, "name") } };
    }
    if (std.mem.eql(u8, m, "entity.destroy")) {
        return .{ .destroy = .{ .entity = try dupField(arena, obj, "entity") } };
    }
    if (std.mem.eql(u8, m, "asset.reload")) {
        return .{ .reload_asset = .{ .guid = try dupField(arena, obj, "guid") } };
    }
    if (std.mem.eql(u8, m, "input.mouseMove")) {
        return .{ .input_mouse_move = .{
            .x = try numField(obj, "x"),
            .y = try numField(obj, "y"),
        } };
    }
    if (std.mem.eql(u8, m, "input.click")) {
        return .{ .input_click = .{
            .x = try numField(obj, "x"),
            .y = try numField(obj, "y"),
            .button = if (obj.get("button")) |v| (if (v == .string) try arena.dupe(u8, v.string) else "left") else "left",
        } };
    }
    if (std.mem.eql(u8, m, "input.key")) {
        return .{ .input_key = .{
            .code = try dupField(arena, obj, "code"),
            .down = if (obj.get("down")) |v| (v == .bool and v.bool) else true,
        } };
    }
    if (std.mem.eql(u8, m, "input.text")) {
        return .{ .input_text = .{ .text = try dupField(arena, obj, "text") } };
    }
    if (std.mem.eql(u8, m, "screenshot.capture")) {
        return .{ .capture_window = .{} };
    }
    if (std.mem.eql(u8, m, "camera.set")) {
        return .{ .camera_set = .{
            .pos = if (obj.get("pos")) |v| try jsonToVec3(v) else null,
            .yaw = try numFieldOpt(obj, "yaw"),
            .pitch = try numFieldOpt(obj, "pitch"),
            .fov = try numFieldOpt(obj, "fov"),
        } };
    }
    if (std.mem.eql(u8, m, "viewport.setTab")) {
        return .{ .viewport_set_tab = .{ .tab = try dupField(arena, obj, "tab") } };
    }
    return error.InvalidParams;
}

fn dupField(arena: std.mem.Allocator, obj: std.json.ObjectMap, key: []const u8) MutationError![]const u8 {
    const v = obj.get(key) orelse return error.InvalidParams;
    if (v != .string) return error.InvalidParams;
    return try arena.dupe(u8, v.string);
}

fn numField(obj: std.json.ObjectMap, key: []const u8) MutationError!f32 {
    const v = obj.get(key) orelse return error.InvalidParams;
    return numFieldValue(v);
}

/// As `numField`, but the key may be absent — used by mutations where every
/// field is optional (e.g. `camera.set`, which only changes what's supplied).
fn numFieldOpt(obj: std.json.ObjectMap, key: []const u8) MutationError!?f32 {
    const v = obj.get(key) orelse return null;
    return try numFieldValue(v);
}

fn numFieldValue(v: std.json.Value) MutationError!f32 {
    return switch (v) {
        .integer => |n| @floatFromInt(n),
        .float => |f| @floatCast(f),
        .number_string => |s| std.fmt.parseFloat(f32, s) catch return error.InvalidParams,
        else => error.InvalidParams,
    };
}

/// Maps a JSON value to an `introspect.Value`. Numbers → number, bools →
/// boolean, strings → text (duped), 3-element numeric arrays → vec3.
fn jsonToValue(arena: std.mem.Allocator, v: std.json.Value) MutationError!introspect.Value {
    return switch (v) {
        .integer => |n| .{ .number = @floatFromInt(n) },
        .float => |f| .{ .number = f },
        .number_string => |s| .{ .number = std.fmt.parseFloat(f64, s) catch return error.InvalidParams },
        .bool => |b| .{ .boolean = b },
        .string => |s| .{ .text = try arena.dupe(u8, s) },
        .array => .{ .vec3 = try jsonToVec3(v) },
        else => error.InvalidParams,
    };
}

fn jsonToVec3(v: std.json.Value) MutationError![3]f32 {
    if (v != .array or v.array.items.len != 3) return error.InvalidParams;
    var out: [3]f32 = undefined;
    for (v.array.items, 0..) |item, i| {
        out[i] = try numFieldValue(item);
    }
    return out;
}
