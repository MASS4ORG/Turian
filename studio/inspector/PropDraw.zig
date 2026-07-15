const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const PropDrawMath = @import("PropDrawMath.zig");
const PropDrawScalars = @import("PropDrawScalars.zig");
const PropDrawCollections = @import("PropDrawCollections.zig");
const PropDrawRef = @import("PropDrawRef.zig");
const PropDrawReflect = @import("PropDrawReflect.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

const FieldHint = engine.FieldHint;
const math = engine.math;

// ─── Re-exports from PropDrawMath ─────────────────────────────────────────────
pub const DrawCtx = PropDrawMath.DrawCtx;
pub const drawVec3Row = PropDrawMath.drawVec3Row;
pub const drawVec2Row = PropDrawMath.drawVec2Row;
pub const drawVec4Row = PropDrawMath.drawVec4Row;

// ─── Re-exports from child modules ─────────────────────────────────────────────
pub const drawRefDropZone = PropDrawRef.drawRefDropZone;
pub const drawScriptAssetRef = PropDrawRef.drawScriptAssetRef;
pub const drawStringEdit = PropDrawScalars.drawStringEdit;
pub const displayLabel = PropDrawReflect.displayLabel;

// ─── Public entry points ──────────────────────────────────────────────────────

pub fn drawComponent(comptime T: type, ptr: *T, id_base: usize, read_only: bool) bool {
    return drawComponentAlloc(T, ptr, id_base, read_only, std.heap.page_allocator);
}

pub fn drawComponentAlloc(comptime T: type, ptr: *T, id_base: usize, read_only: bool, allocator: std.mem.Allocator) bool {
    if (comptime PropDrawReflect.canHaveDecls(T) and @hasDecl(T, "turian_draw")) {
        var al = gui.Alignment.init(@src(), id_base);
        defer al.deinit();
        var ctx = DrawCtx{ .al = &al, .read_only = read_only, .allocator = allocator };
        return T.turian_draw("", ptr, FieldHint{}, &ctx, id_base);
    }
    var al = gui.Alignment.init(@src(), id_base);
    defer al.deinit();
    var ctx = DrawCtx{ .al = &al, .read_only = read_only, .allocator = allocator };
    return drawStructFields(T, ptr, &ctx);
}

pub fn drawValue(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    if (hint.hidden) return false;

    if (comptime PropDrawReflect.canHaveDecls(T) and @hasDecl(T, "turian_draw"))
        return T.turian_draw(label, ptr, hint, ctx, id);

    if (comptime T == engine.ui.EventBinding) return PropDrawScalars.drawEventBinding(label, ptr, hint, ctx, id);

    return switch (comptime @typeInfo(T)) {
        .bool => PropDrawScalars.drawBool(label, ptr, hint, ctx, id),
        .int, .float => PropDrawScalars.drawNumber(T, label, ptr, hint, ctx, id),
        .@"enum" => PropDrawScalars.drawEnum(T, label, ptr, hint, ctx, id),
        .optional => drawOptional(T, label, ptr, hint, ctx, id),
        .@"struct" => drawStructValue(T, label, ptr, hint, ctx, id),
        .pointer => |info| if (info.size == .slice and info.child == u8)
            PropDrawScalars.drawStringSlice(label, ptr, hint, ctx, id)
        else if (info.size == .slice)
            PropDrawCollections.drawGenericSlice(T, info.child, label, ptr, hint, ctx, id, drawValue)
        else
            drawFallback(label, ctx, id),
        .array => |info| if (info.child == u8)
            PropDrawCollections.drawStringArray(T, label, ptr, hint, ctx, id)
        else
            PropDrawCollections.drawGenericArray(T, info.child, label, ptr, hint, ctx, id, drawValue),
        else => drawFallback(label, ctx, id),
    };
}

// ─── Struct field walker ──────────────────────────────────────────────────────

fn drawStructFields(comptime T: type, ptr: *T, ctx: *DrawCtx) bool {
    var changed = false;

    comptime var groups: []const []const u8 = &.{};
    comptime {
        for (std.meta.fields(T)) |field| {
            const h: FieldHint = if (@hasDecl(T, "turian_hints") and @hasDecl(T.turian_hints, field.name))
                @field(T.turian_hints, field.name)
            else
                .{};
            if (h.group) |g| {
                var found = false;
                for (groups) |existing| {
                    if (std.mem.eql(u8, existing, g)) {
                        found = true;
                        break;
                    }
                }
                if (!found) groups = groups ++ &[_][]const u8{g};
            }
        }
    }

    inline for (std.meta.fields(T), 0..) |field, fi| {
        const hint = comptime PropDrawReflect.fieldHint(T, field.name);
        if (comptime hint.hidden) continue;
        if (comptime hint.group != null) continue;
        const label = comptime PropDrawReflect.displayLabel(field.name, hint);
        if (comptime @hasDecl(T, "turian_drawers") and @hasDecl(T.turian_drawers, field.name)) {
            const drawer = comptime @field(T.turian_drawers, field.name);
            changed = drawer(label, &@field(ptr, field.name), hint, ctx, fi) or changed;
        } else {
            if (drawValue(field.type, label, &@field(ptr, field.name), hint, ctx, fi))
                changed = true;
        }
    }

    inline for (groups, 0..) |g, gi| {
        if (gui.expander(@src(), tr(g), .{ .default_expanded = true }, .{
            .expand = .horizontal,
            .padding = .all(2),
            .id_extra = gi,
        })) {
            var indent = gui.box(@src(), .{}, .{
                .expand = .horizontal,
                .padding = .{ .x = 12, .y = 0 },
                .id_extra = gi,
            });
            defer indent.deinit();

            var grp_al = gui.Alignment.init(@src(), gi);
            defer grp_al.deinit();
            var grp_ctx = DrawCtx{ .al = &grp_al, .depth = ctx.depth + 1, .read_only = ctx.read_only, .allocator = ctx.allocator };

            inline for (std.meta.fields(T), 0..) |field, fi| {
                const hint = comptime PropDrawReflect.fieldHint(T, field.name);
                if (comptime hint.hidden) continue;
                if (comptime hint.group == null or !std.mem.eql(u8, hint.group.?, g)) continue;
                const label = comptime PropDrawReflect.displayLabel(field.name, hint);
                if (comptime @hasDecl(T, "turian_drawers") and @hasDecl(T.turian_drawers, field.name)) {
                    const drawer = comptime @field(T.turian_drawers, field.name);
                    changed = drawer(label, &@field(ptr, field.name), hint, &grp_ctx, fi) or changed;
                } else {
                    if (drawValue(field.type, label, &@field(ptr, field.name), hint, &grp_ctx, fi))
                        changed = true;
                }
            }
        }
    }

    return changed;
}

// ─── Type dispatch ────────────────────────────────────────────────────────────

fn drawStructValue(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    if (comptime T == math.Vector2) return PropDrawMath.drawVec2(label, ptr, hint, ctx, id);
    if (comptime T == math.Vector3) return PropDrawMath.drawVec3(label, ptr, hint, ctx, id);
    if (comptime T == math.Vector4) {
        return if (hint.is_color) PropDrawMath.drawColorVec4(label, ptr, hint, ctx, id) else PropDrawMath.drawVec4(label, ptr, hint, ctx, id);
    }
    if (comptime T == math.Vector2i) return PropDrawMath.drawVec2i(label, ptr, hint, ctx, id);
    if (comptime T == math.Vector3i) return PropDrawMath.drawVec3i(label, ptr, hint, ctx, id);
    if (comptime T == math.Vector4i) return PropDrawMath.drawVec4i(label, ptr, hint, ctx, id);
    if (comptime T == math.Quaternion) return PropDrawMath.drawQuaternion(label, ptr, hint, ctx, id);
    if (comptime T == math.Matrix4) return PropDrawMath.drawMatrix4(label, ptr, hint, ctx, id);
    if (comptime @hasDecl(T, "_turian_ref_kind")) return PropDrawRef.drawRef(T, label, ptr, hint, ctx, id);
    return drawNestedStruct(T, label, ptr, hint, ctx, id);
}

fn drawOptional(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    const Child = @typeInfo(T).optional.child;
    var changed = false;
    var has_val = ptr.* != null;
    const ro = ctx.read_only or hint.read_only;

    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
        defer row.deinit();

        gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });

        var aligned = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .margin = ctx.al.margin(row.data().id),
            .gravity_y = 0.5,
            .id_extra = id,
        });
        defer aligned.deinit();
        ctx.al.record(row.data().id, aligned.data());

        if (!ro) {
            if (gui.checkbox(@src(), &has_val, null, .{ .gravity_y = 0.5, .id_extra = id })) {
                if (has_val) {
                    ptr.* = std.mem.zeroes(Child);
                } else {
                    ptr.* = null;
                }
                changed = true;
            }
        } else {
            gui.label(@src(), "{s}", .{if (has_val) tr("set") else tr("null")}, .{ .gravity_y = 0.5, .id_extra = id });
        }
    }

    if (ptr.*) |*inner| {
        var inner_al = gui.Alignment.init(@src(), id);
        defer inner_al.deinit();
        var inner_ctx = DrawCtx{ .al = &inner_al, .depth = ctx.depth + 1, .read_only = ro, .allocator = ctx.allocator };
        var indent = gui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 20, .y = 0 },
            .id_extra = id,
        });
        defer indent.deinit();
        if (drawValue(Child, label, inner, hint, &inner_ctx, id))
            changed = true;
    }

    return changed;
}

fn drawNestedStruct(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    if (gui.expander(@src(), label, .{ .default_expanded = true }, .{
        .expand = .horizontal,
        .padding = .all(2),
        .id_extra = id,
    })) {
        var indent = gui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 12, .y = 0 },
            .id_extra = id,
        });
        defer indent.deinit();
        var sub_al = gui.Alignment.init(@src(), id);
        defer sub_al.deinit();
        var sub_ctx = DrawCtx{ .al = &sub_al, .depth = ctx.depth + 1, .read_only = ctx.read_only or hint.read_only, .allocator = ctx.allocator };
        return drawStructFields(T, ptr, &sub_ctx);
    }
    return false;
}

fn drawFallback(label: []const u8, ctx: *DrawCtx, id: usize) bool {
    _ = ctx;
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{StudioLocale.trArgs("{field}: (unsupported type)", &.{.{ .name = "field", .value = .{ .text = label } }})}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
    return false;
}
