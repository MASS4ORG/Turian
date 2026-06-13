/// Comptime reflection helpers compiled into user .so wrappers.
/// Imported via -Mreflection= when building user component shared libraries.
/// NOT imported directly by the engine module at build time.
const std = @import("std");
const engine = @import("engine");
const api = engine.api;

fn zigTypeToFieldType(comptime T: type) ?api.FieldType {
    switch (T) {
        f32 => return .f32,
        f64 => return .f64,
        i32 => return .i32,
        i64 => return .i64,
        u32 => return .u32,
        bool => return .bool,
        else => {},
    }
    const info = @typeInfo(T);
    if (info == .@"struct") {
        if (@hasDecl(T, "_turian_ref_kind")) return T._turian_ref_kind;

        const fs = info.@"struct".fields;
        if (fs.len == 2 and
            std.mem.eql(u8, fs[0].name, "x") and fs[0].type == f32 and
            std.mem.eql(u8, fs[1].name, "y") and fs[1].type == f32)
            return .vec2;
        if (fs.len == 3 and
            std.mem.eql(u8, fs[0].name, "x") and fs[0].type == f32 and
            std.mem.eql(u8, fs[1].name, "y") and fs[1].type == f32 and
            std.mem.eql(u8, fs[2].name, "z") and fs[2].type == f32)
            return .vec3;
        if (fs.len == 4 and
            std.mem.eql(u8, fs[0].name, "x") and fs[0].type == f32 and
            std.mem.eql(u8, fs[1].name, "y") and fs[1].type == f32 and
            std.mem.eql(u8, fs[2].name, "z") and fs[2].type == f32 and
            std.mem.eql(u8, fs[3].name, "w") and fs[3].type == f32)
            return .vec4;
    }
    if (info == .array and info.array.child == u8) return .string;
    return null;
}

/// Asset category declared by a typed asset reference field, or `.any`
/// for plain `AssetRef` and non-asset fields.
fn assetFilterOf(comptime T: type) api.AssetFilter {
    if (canHaveDecls(T) and @hasDecl(T, "_turian_asset_filter"))
        return T._turian_asset_filter;
    return .any;
}

fn canHaveDecls(comptime T: type) bool {
    return switch (@typeInfo(T)) {
        .@"struct", .@"union", .@"enum", .@"opaque" => true,
        else => false,
    };
}

/// True for struct types that aren't already leaf types and have at least one
/// reflectable field (possibly through nesting). Used to decide whether to
/// recurse into a field rather than skip it.
fn isNestedReflectableStruct(comptime T: type) bool {
    if (zigTypeToFieldType(T) != null) return false;
    if (@typeInfo(T) != .@"struct") return false;
    return countFields(T) > 0;
}

/// Count how many leaf FieldInfo entries type T produces (including through
/// recursively flattened nested structs).
fn countFields(comptime T: type) usize {
    comptime var n: usize = 0;
    inline for (std.meta.fields(T)) |f| {
        if (f.is_comptime) continue;
        if (comptime zigTypeToFieldType(f.type) != null) {
            n += 1;
        } else if (comptime isNestedReflectableStruct(f.type)) {
            n += countFields(f.type);
        }
    }
    return n;
}

/// Recursively emit FieldInfo entries for type T into `out`, prefixing every
/// name with `prefix` (e.g. "spring." for fields inside a `spring` field).
fn collectFields(
    comptime T: type,
    comptime prefix: []const u8,
    out: [*]api.FieldInfo,
    idx: *usize,
) void {
    inline for (std.meta.fields(T)) |f| {
        if (f.is_comptime) continue;
        if (comptime zigTypeToFieldType(f.type)) |ft| {
            // Build a statically-allocated null-terminated name.
            const full_name: [:0]const u8 = comptime std.fmt.comptimePrint("{s}{s}", .{ prefix, f.name });
            out[idx.*] = .{
                .name = full_name.ptr,
                .field_type = ft,
                .default_value = getDefaultValue(f, ft),
                .asset_filter = comptime assetFilterOf(f.type),
            };
            idx.* += 1;
        } else if (comptime isNestedReflectableStruct(f.type)) {
            const sub_prefix: []const u8 = comptime std.fmt.comptimePrint("{s}{s}.", .{ prefix, f.name });
            collectFields(f.type, sub_prefix, out, idx);
        }
    }
}

fn getDefaultValue(
    comptime field: std.builtin.Type.StructField,
    comptime ft: api.FieldType,
) api.FieldValue {
    switch (ft) {
        .game_object_ref, .component_ref, .asset_ref, .string => return .{ .as_f32 = 0 },
        else => {},
    }
    const ptr = field.default_value_ptr orelse return switch (ft) {
        .f32 => .{ .as_f32 = 0 },
        .f64 => .{ .as_f64 = 0 },
        .i32 => .{ .as_i32 = 0 },
        .i64 => .{ .as_i64 = 0 },
        .u32 => .{ .as_u32 = 0 },
        .bool => .{ .as_bool = false },
        .vec2 => .{ .as_vec2 = .{} },
        .vec3 => .{ .as_vec3 = .{} },
        .vec4 => .{ .as_vec4 = .{} },
        else => .{ .as_f32 = 0 },
    };
    return switch (ft) {
        .f32 => .{ .as_f32 = @as(*const f32, @ptrCast(@alignCast(ptr))).* },
        .f64 => .{ .as_f64 = @as(*const f64, @ptrCast(@alignCast(ptr))).* },
        .i32 => .{ .as_i32 = @as(*const i32, @ptrCast(@alignCast(ptr))).* },
        .i64 => .{ .as_i64 = @as(*const i64, @ptrCast(@alignCast(ptr))).* },
        .u32 => .{ .as_u32 = @as(*const u32, @ptrCast(@alignCast(ptr))).* },
        .bool => .{ .as_bool = @as(*const bool, @ptrCast(@alignCast(ptr))).* },
        .vec2 => blk: {
            const v = @as(*const engine.Vector2, @ptrCast(@alignCast(ptr))).*;
            break :blk .{ .as_vec2 = .{ .x = v.x, .y = v.y } };
        },
        .vec3 => blk: {
            const v = @as(*const engine.Vector3, @ptrCast(@alignCast(ptr))).*;
            break :blk .{ .as_vec3 = .{ .x = v.x, .y = v.y, .z = v.z } };
        },
        .vec4 => blk: {
            const v = @as(*const engine.Vector4, @ptrCast(@alignCast(ptr))).*;
            break :blk .{ .as_vec4 = .{ .x = v.x, .y = v.y, .z = v.z, .w = v.w } };
        },
        else => .{ .as_f32 = 0 },
    };
}

/// Build reflection info for any reflectable struct (component or data asset).
/// No marker assertion — callers are responsible for ensuring T is intentional.
pub fn buildReflectedInfo(comptime T: type, allocator: std.mem.Allocator) !api.ComponentInfo {
    comptime {
        if (@typeInfo(T) != .@"struct")
            @compileError("'" ++ @typeName(T) ++ "' must be a struct");
    }

    const field_count = comptime countFields(T);
    const fields = try allocator.alloc(api.FieldInfo, field_count);
    var idx: usize = 0;
    collectFields(T, "", fields.ptr, &idx);

    return .{
        .name = @typeName(T).ptr,
        .fields = fields.ptr,
        .field_count = field_count,
    };
}

pub fn buildComponentInfo(comptime T: type, allocator: std.mem.Allocator) !api.ComponentInfo {
    comptime {
        if (@typeInfo(T) != .@"struct")
            @compileError("Component '" ++ @typeName(T) ++ "' must be a struct");
        if (!@hasDecl(T, "is_component") or @field(T, "is_component") != true)
            @compileError("Component '" ++ @typeName(T) ++ "' must declare `pub const is_component = true;`");
    }
    return buildReflectedInfo(T, allocator);
}
