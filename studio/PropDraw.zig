const std = @import("std");
const dvui = @import("dvui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");

const FieldHint = engine.FieldHint;
const math = engine.math;

// ─── Axis colour palette ──────────────────────────────────────────────────────
const col_x = dvui.Color{ .r = 210, .g = 60, .b = 60 };
const col_y = dvui.Color{ .r = 60, .g = 180, .b = 60 };
const col_z = dvui.Color{ .r = 60, .g = 100, .b = 210 };
const col_w = dvui.Color{ .r = 180, .g = 100, .b = 210 };

// ─── DrawCtx ──────────────────────────────────────────────────────────────────

/// Threaded context for the recursive property drawer.
/// Do not persist across frames — create fresh each draw call.
pub const DrawCtx = struct {
    al: *dvui.Alignment,
    depth: u32 = 0,
    read_only: bool = false,
    /// Allocator for slice field mutations (add/remove). Null disables those controls.
    allocator: ?std.mem.Allocator = null,
};

// ─── Public entry points ──────────────────────────────────────────────────────

/// Draw all inspector fields for a component struct `T`.
/// `id_base` must be unique per component slot (e.g. the component index).
/// Returns true if any field value changed this frame.
pub fn drawComponent(comptime T: type, ptr: *T, id_base: usize, read_only: bool) bool {
    // Check for full type-level override first.
    if (comptime canHaveDecls(T) and @hasDecl(T, "turian_draw")) {
        var al = dvui.Alignment.init(@src(), id_base);
        defer al.deinit();
        var ctx = DrawCtx{ .al = &al, .read_only = read_only, .allocator = std.heap.page_allocator };
        return T.turian_draw("", ptr, FieldHint{}, &ctx, id_base);
    }
    var al = dvui.Alignment.init(@src(), id_base);
    defer al.deinit();
    var ctx = DrawCtx{ .al = &al, .read_only = read_only, .allocator = std.heap.page_allocator };
    return drawStructFields(T, ptr, &ctx);
}

/// Draw a labelled editor for a single value of type `T`.
/// `id` must be unique among siblings at the same nesting level.
/// Returns true if the value changed.
///
/// OdinInspector-style customisation hooks (checked in order):
///   1. `T.turian_draw(label, ptr, hint, ctx, id) bool` — full type override
///   2. Known engine math/ref types → dedicated drawers
///   3. Generic Zig type dispatch (bool, int/float, enum, optional, struct, slice, array)
pub fn drawValue(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    if (hint.hidden) return false;

    if (comptime canHaveDecls(T) and @hasDecl(T, "turian_draw"))
        return T.turian_draw(label, ptr, hint, ctx, id);

    return switch (comptime @typeInfo(T)) {
        .bool => drawBool(label, ptr, hint, ctx, id),
        .int, .float => drawNumber(T, label, ptr, hint, ctx, id),
        .@"enum" => drawEnum(T, label, ptr, hint, ctx, id),
        .optional => drawOptional(T, label, ptr, hint, ctx, id),
        .@"struct" => drawStructValue(T, label, ptr, hint, ctx, id),
        .pointer => |info| if (info.size == .Slice and info.child == u8)
            drawStringSlice(label, ptr, hint, ctx, id)
        else if (info.size == .Slice)
            drawGenericSlice(T, info.child, label, ptr, hint, ctx, id)
        else
            drawFallback(label, ctx, id),
        .array => |info| if (info.child == u8)
            drawStringArray(T, label, ptr, hint, ctx, id)
        else
            drawGenericArray(T, info.child, label, ptr, hint, ctx, id),
        else => drawFallback(label, ctx, id),
    };
}

// ─── Legacy helpers (kept for Inspector.drawScriptField) ──────────────────────

/// Draw a row of X / Y / Z number inputs for a Vector3 value.
/// Returns true if any component changed.
pub fn drawVec3Row(src: std.builtin.SourceLocation, v: *engine.Vector3) bool {
    var changed = false;
    var row = dvui.box(src, .{ .dir = .horizontal }, .{ .expand = .horizontal });
    defer row.deinit();

    dvui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rx = dvui.textEntryNumber(@src(), f32, .{ .value = &v.x }, .{ .min_size_content = .{ .w = 52, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rx.changed) changed = true;

    dvui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const ry = dvui.textEntryNumber(@src(), f32, .{ .value = &v.y }, .{ .min_size_content = .{ .w = 52, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (ry.changed) changed = true;

    dvui.label(@src(), "Z", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rz = dvui.textEntryNumber(@src(), f32, .{ .value = &v.z }, .{ .min_size_content = .{ .w = 52, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rz.changed) changed = true;

    return changed;
}

/// Draw a row of X / Y number inputs for a Vector2 value.
/// Returns true if any component changed.
pub fn drawVec2Row(src: std.builtin.SourceLocation, v: *engine.Vector2) bool {
    var changed = false;
    var row = dvui.box(src, .{ .dir = .horizontal }, .{ .expand = .horizontal });
    defer row.deinit();

    dvui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rx = dvui.textEntryNumber(@src(), f32, .{ .value = &v.x }, .{ .min_size_content = .{ .w = 52, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rx.changed) changed = true;

    dvui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const ry = dvui.textEntryNumber(@src(), f32, .{ .value = &v.y }, .{ .min_size_content = .{ .w = 52, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (ry.changed) changed = true;

    return changed;
}

/// Draw a row of X / Y / Z / W number inputs for a Vector4 value.
/// Returns true if any component changed.
pub fn drawVec4Row(src: std.builtin.SourceLocation, v: *engine.Vector4) bool {
    var changed = false;
    var row = dvui.box(src, .{ .dir = .horizontal }, .{ .expand = .horizontal });
    defer row.deinit();

    dvui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rx = dvui.textEntryNumber(@src(), f32, .{ .value = &v.x }, .{ .min_size_content = .{ .w = 44, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rx.changed) changed = true;

    dvui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const ry = dvui.textEntryNumber(@src(), f32, .{ .value = &v.y }, .{ .min_size_content = .{ .w = 44, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (ry.changed) changed = true;

    dvui.label(@src(), "Z", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rz = dvui.textEntryNumber(@src(), f32, .{ .value = &v.z }, .{ .min_size_content = .{ .w = 44, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rz.changed) changed = true;

    dvui.label(@src(), "W", .{}, .{ .color_text = col_w, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rw = dvui.textEntryNumber(@src(), f32, .{ .value = &v.w }, .{ .min_size_content = .{ .w = 44, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rw.changed) changed = true;

    return changed;
}

/// Drop-zone that accepts dragged asset / game-object references.
/// Returns the accepted GUID string if something was dropped, null otherwise.
var s_accepted_guid_buf: [36]u8 = undefined;

pub fn drawRefDropZone(
    src: std.builtin.SourceLocation,
    kind: engine.api.FieldType,
    current_guid: []const u8,
    id_extra: usize,
) ?[]const u8 {
    const drag_compatible = switch (kind) {
        .game_object_ref, .component_ref => EditorState.drag_kind == .game_object,
        .asset_ref => EditorState.drag_kind == .asset,
        else => false,
    };

    var drop_box = dvui.box(src, .{}, .{
        .expand = .horizontal,
        .gravity_y = 0.5,
        .border = .all(if (drag_compatible) 2 else 1),
        .style = if (drag_compatible) .highlight else .content,
        .padding = .{ .x = 4, .y = 2 },
        .corner_radius = .all(3),
        .id_extra = id_extra,
    });
    defer drop_box.deinit();

    var accepted: ?[]const u8 = null;
    for (dvui.events()) |*e| {
        if (!dvui.eventMatchSimple(e, drop_box.data())) continue;
        switch (e.evt) {
            .mouse => |me| {
                if (me.action == .release and me.button == .left and drag_compatible) {
                    e.handle(@src(), drop_box.data());
                    accepted = switch (EditorState.drag_kind) {
                        .game_object => blk: {
                            const idx = EditorState.drag_object_idx;
                            if (idx >= EditorState.object_count) break :blk null;
                            const gs = EditorState.objects[idx].guidSlice();
                            if (gs.len == 0) break :blk null;
                            @memcpy(s_accepted_guid_buf[0..gs.len], gs);
                            break :blk s_accepted_guid_buf[0..gs.len];
                        },
                        .asset => EditorState.dragAssetGuidStr(&s_accepted_guid_buf),
                        .none => null,
                    };
                    if (accepted != null) EditorState.clearDrag();
                }
            },
            else => {},
        }
    }

    const display = guidDisplayName(kind, current_guid);
    dvui.label(@src(), "{s}", .{display}, .{
        .expand = .horizontal,
        .gravity_y = 0.5,
        .id_extra = id_extra,
    });

    return accepted;
}

// ─── Struct field walker ──────────────────────────────────────────────────────

fn drawStructFields(comptime T: type, ptr: *T, ctx: *DrawCtx) bool {
    var changed = false;

    // Collect unique non-null group labels at comptime.
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

    // Pass 1 — ungrouped fields in declaration order.
    inline for (std.meta.fields(T), 0..) |field, fi| {
        const hint = comptime fieldHint(T, field.name);
        if (comptime hint.hidden) continue;
        if (comptime hint.group != null) continue;
        if (comptime @hasDecl(T, "turian_drawers") and @hasDecl(T.turian_drawers, field.name)) {
            const drawer = comptime @field(T.turian_drawers, field.name);
            changed = drawer(field.name, &@field(ptr, field.name), hint, ctx, fi) or changed;
        } else {
            if (drawValue(field.type, field.name, &@field(ptr, field.name), hint, ctx, fi))
                changed = true;
        }
    }

    // Pass 2 — grouped fields, each group under its own collapsible expander.
    inline for (groups, 0..) |g, gi| {
        if (dvui.expander(@src(), g, .{ .default_expanded = true }, .{
            .expand = .horizontal,
            .padding = .all(2),
            .id_extra = gi,
        })) {
            var indent = dvui.box(@src(), .{}, .{
                .expand = .horizontal,
                .padding = .{ .x = 12, .y = 0 },
                .id_extra = gi,
            });
            defer indent.deinit();

            // Fresh alignment scope for each group.
            var grp_al = dvui.Alignment.init(@src(), gi);
            defer grp_al.deinit();
            var grp_ctx = DrawCtx{ .al = &grp_al, .depth = ctx.depth + 1, .read_only = ctx.read_only, .allocator = ctx.allocator };

            inline for (std.meta.fields(T), 0..) |field, fi| {
                const hint = comptime fieldHint(T, field.name);
                if (comptime hint.hidden) continue;
                if (comptime hint.group == null or !std.mem.eql(u8, hint.group.?, g)) continue;
                if (comptime @hasDecl(T, "turian_drawers") and @hasDecl(T.turian_drawers, field.name)) {
                    const drawer = comptime @field(T.turian_drawers, field.name);
                    changed = drawer(field.name, &@field(ptr, field.name), hint, &grp_ctx, fi) or changed;
                } else {
                    if (drawValue(field.type, field.name, &@field(ptr, field.name), hint, &grp_ctx, fi))
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
    // Known engine math types matched by identity.
    if (comptime T == math.Vector2) return drawVec2(label, ptr, hint, ctx, id);
    if (comptime T == math.Vector3) return drawVec3(label, ptr, hint, ctx, id);
    if (comptime T == math.Vector4) {
        return if (hint.is_color) drawColorVec4(label, ptr, hint, ctx, id) else drawVec4(label, ptr, hint, ctx, id);
    }
    if (comptime T == math.Vector2i) return drawVec2i(label, ptr, hint, ctx, id);
    if (comptime T == math.Vector3i) return drawVec3i(label, ptr, hint, ctx, id);
    if (comptime T == math.Vector4i) return drawVec4i(label, ptr, hint, ctx, id);
    if (comptime T == math.Quaternion) return drawQuaternion(label, ptr, hint, ctx, id);
    if (comptime T == math.Matrix4) return drawMatrix4(label, ptr, hint, ctx, id);
    if (comptime @hasDecl(T, "_turian_ref_kind")) return drawRef(T, label, ptr, hint, ctx, id);
    // Generic struct — recurse under a collapsible sub-group.
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

    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();

    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });

    var aligned = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer aligned.deinit();
    ctx.al.record(row.data().id, aligned.data());

    const ro = ctx.read_only or hint.read_only;
    if (!ro) {
        if (dvui.checkbox(@src(), &has_val, null, .{ .gravity_y = 0.5, .id_extra = id })) {
            if (has_val) {
                ptr.* = std.mem.zeroes(Child);
            } else {
                ptr.* = null;
            }
            changed = true;
        }
    } else {
        dvui.label(@src(), "{s}", .{if (has_val) "set" else "null"}, .{ .gravity_y = 0.5, .id_extra = id });
    }
    aligned.deinit();
    row.deinit();

    if (ptr.*) |*inner| {
        var inner_al = dvui.Alignment.init(@src(), id);
        defer inner_al.deinit();
        var inner_ctx = DrawCtx{ .al = &inner_al, .depth = ctx.depth + 1, .read_only = ro, .allocator = ctx.allocator };
        var indent = dvui.box(@src(), .{}, .{
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

// ─── Leaf drawers ─────────────────────────────────────────────────────────────

fn drawBool(label: []const u8, ptr: *bool, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();

    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);

    var aligned = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer aligned.deinit();
    ctx.al.record(row.data().id, aligned.data());

    const ro = ctx.read_only or hint.read_only;
    if (ro) {
        dvui.label(@src(), "{}", .{ptr.*}, .{ .gravity_y = 0.5, .id_extra = id });
        return false;
    }
    return dvui.checkbox(@src(), ptr, null, .{ .gravity_y = 0.5, .id_extra = id });
}

fn drawNumber(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();

    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);

    var aligned = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer aligned.deinit();
    ctx.al.record(row.data().id, aligned.data());

    const ro = ctx.read_only or hint.read_only;
    if (ro) {
        dvui.label(@src(), "{d}", .{ptr.*}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
        return false;
    }

    // f32 with range → slider or sliderEntry
    if (comptime T == f32) {
        const has_range = hint.min != null and hint.max != null;
        if (has_range) {
            const lo: f32 = @floatCast(hint.min.?);
            const hi: f32 = @floatCast(hint.max.?);
            if (hint.widget == .slider_entry) {
                return dvui.sliderEntry(@src(), null, .{
                    .value = ptr,
                    .min = lo,
                    .max = hi,
                    .interval = if (hint.step) |s| @floatCast(s) else null,
                }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
            }
            if (hint.widget == .slider) {
                var frac: f32 = if (hi > lo) std.math.clamp((ptr.* - lo) / (hi - lo), 0, 1) else 0;
                if (dvui.slider(@src(), .{ .fraction = &frac }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id })) {
                    ptr.* = lo + frac * (hi - lo);
                    if (hint.step) |s| {
                        const sf: f32 = @floatCast(s);
                        ptr.* = @round(ptr.* / sf) * sf;
                    }
                    ptr.* = std.math.clamp(ptr.*, lo, hi);
                    return true;
                }
                return false;
            }
        }
    }

    // Generic number entry.
    const result = dvui.textEntryNumber(@src(), T, .{
        .value = ptr,
        .min = if (hint.min) |m| castHintBound(T, m) else null,
        .max = if (hint.max) |m| castHintBound(T, m) else null,
    }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
    return result.changed;
}

fn drawEnum(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();

    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);

    var aligned = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer aligned.deinit();
    ctx.al.record(row.data().id, aligned.data());

    const ro = ctx.read_only or hint.read_only;
    if (ro) {
        dvui.label(@src(), "{s}", .{@tagName(ptr.*)}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
        return false;
    }
    return dvui.dropdownEnum(@src(), T, .{ .choice = ptr }, .{}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
}

fn drawStringSlice(label: []const u8, ptr: *[]const u8, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    // Mutable string slices via the inspector are unusual — show as read-only label.
    _ = hint;
    _ = ctx;
    dvui.label(@src(), "{s}: {s}", .{ label, ptr.* }, .{ .expand = .horizontal, .id_extra = id });
    return false;
}

fn drawStringArray(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    const ro = ctx.read_only or hint.read_only;
    const buf: []u8 = ptr[0..];

    if (hint.multiline) {
        // Multiline text area — label above, textarea below.
        dvui.label(@src(), "{s}", .{label}, .{ .id_extra = id });
        if (ro) return false;
        var te = dvui.textEntry(@src(), .{
            .text = .{ .buffer = buf },
            .multiline = true,
        }, .{ .expand = .horizontal, .min_size_content = .{ .h = 80 }, .id_extra = id });
        defer te.deinit();
        return te.text_changed;
    }

    // Single-line: standard label + entry row.
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);
    var aligned = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer aligned.deinit();
    ctx.al.record(row.data().id, aligned.data());

    if (ro) {
        // Show null-terminated portion only.
        const end = std.mem.indexOfScalar(u8, buf, 0) orelse buf.len;
        dvui.label(@src(), "{s}", .{buf[0..end]}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
        return false;
    }
    var te = dvui.textEntry(@src(), .{
        .text = .{ .buffer = buf },
    }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
    defer te.deinit();
    return te.text_changed;
}

fn drawGenericArray(
    comptime T: type,
    comptime Child: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    const len = @typeInfo(T).array.len;
    if (dvui.expander(@src(), label, .{ .default_expanded = false }, .{
        .expand = .horizontal,
        .padding = .all(2),
        .id_extra = id,
    })) {
        var indent = dvui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 12, .y = 0 },
            .id_extra = id,
        });
        defer indent.deinit();
        var arr_al = dvui.Alignment.init(@src(), id);
        defer arr_al.deinit();
        var arr_ctx = DrawCtx{ .al = &arr_al, .depth = ctx.depth + 1, .read_only = ctx.read_only or hint.read_only, .allocator = ctx.allocator };
        var changed = false;
        inline for (0..len) |i| {
            var name_buf: [32]u8 = undefined;
            const name = std.fmt.bufPrint(&name_buf, "[{d}]", .{i}) catch "[?]";
            if (drawValue(Child, name, &ptr[i], .{}, &arr_ctx, id * 1000 + i))
                changed = true;
        }
        return changed;
    }
    return false;
}

fn drawGenericSlice(
    comptime T: type,
    comptime Child: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    const effective_ro = ctx.read_only or hint.read_only;
    const can_mutate = !effective_ro and ctx.allocator != null;

    // ── Header row: label  [N]  [+] ──────────────────────────────────────────
    var add = false;
    {
        var hdr = dvui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .padding = .{ .x = 0, .y = 2 },
            .id_extra = id,
        });
        defer hdr.deinit();

        dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
        dvui.label(@src(), "[{d}]", .{ptr.*.len}, .{
            .expand = .horizontal,
            .gravity_y = 0.5,
            .id_extra = id,
        });
        if (can_mutate) {
            if (dvui.button(@src(), "+", .{}, .{ .gravity_y = 0.5, .id_extra = id })) add = true;
        }
    }

    // ── Element rows (indented) ───────────────────────────────────────────────
    var changed = false;
    var remove_idx: ?usize = null;

    if (ptr.*.len > 0) {
        var indent = dvui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 12, .y = 0 },
            .id_extra = id,
        });
        defer indent.deinit();

        var elem_al = dvui.Alignment.init(@src(), id);
        defer elem_al.deinit();
        var elem_ctx = DrawCtx{
            .al = &elem_al,
            .depth = ctx.depth + 1,
            .read_only = effective_ro,
            .allocator = ctx.allocator,
        };

        for (0..ptr.*.len) |ei| {
            var row = dvui.box(@src(), .{ .dir = .horizontal }, .{
                .expand = .horizontal,
                .id_extra = id * 10000 + ei + 1,
            });
            defer row.deinit();

            if (can_mutate) {
                if (dvui.button(@src(), "×", .{}, .{
                    .style = .err,
                    .gravity_y = 0.5,
                    .id_extra = id * 10000 + ei + 1,
                })) remove_idx = ei;
            }

            var name_buf: [16]u8 = undefined;
            const elem_name = std.fmt.bufPrint(&name_buf, "[{d}]", .{ei}) catch "[?]";
            if (drawValue(Child, elem_name, &ptr.*[ei], .{}, &elem_ctx, id * 10000 + ei + 1))
                changed = true;
        }
    }

    // ── Apply mutations after full render ─────────────────────────────────────
    // Always allocate a fresh slice so that undo snapshots (which hold the old
    // pointer) remain valid — page_allocator pages are never freed during the
    // editor session.
    if (ctx.allocator) |alloc| {
        if (remove_idx) |ri| {
            if (ptr.*.len > 0) {
                const ns = alloc.alloc(Child, ptr.*.len - 1) catch return changed;
                @memcpy(ns[0..ri], ptr.*[0..ri]);
                if (ri < ptr.*.len - 1) @memcpy(ns[ri..], ptr.*[ri + 1 ..]);
                ptr.* = ns;
                changed = true;
            }
        } else if (add) {
            const ns = alloc.alloc(Child, ptr.*.len + 1) catch return changed;
            @memcpy(ns[0..ptr.*.len], ptr.*);
            ns[ptr.*.len] = std.mem.zeroes(Child);
            ptr.* = ns;
            changed = true;
        }
    }

    return changed;
}

fn drawFallback(label: []const u8, ctx: *DrawCtx, id: usize) bool {
    _ = ctx;
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}: (unsupported type)", .{label}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
    return false;
}

// ─── Nested struct expander ───────────────────────────────────────────────────

fn drawNestedStruct(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    if (dvui.expander(@src(), label, .{ .default_expanded = true }, .{
        .expand = .horizontal,
        .padding = .all(2),
        .id_extra = id,
    })) {
        var indent = dvui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 12, .y = 0 },
            .id_extra = id,
        });
        defer indent.deinit();
        var sub_al = dvui.Alignment.init(@src(), id);
        defer sub_al.deinit();
        var sub_ctx = DrawCtx{ .al = &sub_al, .depth = ctx.depth + 1, .read_only = ctx.read_only or hint.read_only, .allocator = ctx.allocator };
        return drawStructFields(T, ptr, &sub_ctx);
    }
    return false;
}

// ─── Math type drawers ────────────────────────────────────────────────────────

fn drawVec2(label: []const u8, ptr: *math.Vector2, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    var al_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        dvui.label(@src(), "({d:.3}, {d:.3})", .{ ptr.x, ptr.y }, .{ .id_extra = id });
        return false;
    }
    var changed = false;
    dvui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), f32, .{ .value = &ptr.x }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 2 }).changed) changed = true;
    dvui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), f32, .{ .value = &ptr.y }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 2 + 1 }).changed) changed = true;
    return changed;
}

fn drawVec3(label: []const u8, ptr: *math.Vector3, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);
    var al_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        dvui.label(@src(), "({d:.3}, {d:.3}, {d:.3})", .{ ptr.x, ptr.y, ptr.z }, .{ .id_extra = id });
        return false;
    }
    return drawVec3Row(@src(), ptr);
}

fn drawVec4(label: []const u8, ptr: *math.Vector4, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    var al_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        dvui.label(@src(), "({d:.3}, {d:.3}, {d:.3}, {d:.3})", .{ ptr.x, ptr.y, ptr.z, ptr.w }, .{ .id_extra = id });
        return false;
    }
    var changed = false;
    dvui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), f32, .{ .value = &ptr.x }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 }).changed) changed = true;
    dvui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), f32, .{ .value = &ptr.y }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 1 }).changed) changed = true;
    dvui.label(@src(), "Z", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), f32, .{ .value = &ptr.z }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 2 }).changed) changed = true;
    dvui.label(@src(), "W", .{}, .{ .color_text = col_w, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), f32, .{ .value = &ptr.w }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 3 }).changed) changed = true;
    return changed;
}

fn drawColorVec4(label: []const u8, ptr: *math.Vector4, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);
    var al_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    const ro = ctx.read_only or hint.read_only;
    const r8: u8 = @intFromFloat(std.math.clamp(ptr.x, 0, 1) * 255);
    const g8: u8 = @intFromFloat(std.math.clamp(ptr.y, 0, 1) * 255);
    const b8: u8 = @intFromFloat(std.math.clamp(ptr.z, 0, 1) * 255);
    const a8: u8 = @intFromFloat(std.math.clamp(ptr.w, 0, 1) * 255);
    var swatch = dvui.box(@src(), .{}, .{
        .min_size_content = .{ .w = 20, .h = 20 },
        .background = true,
        .color_fill = .fromColor(.{ .r = r8, .g = g8, .b = b8, .a = a8 }),
        .border = .all(1),
        .corner_radius = .all(2),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    swatch.deinit();
    if (!ro) {
        var changed = false;
        dvui.label(@src(), "R", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
        if (dvui.sliderEntry(@src(), null, .{ .value = &ptr.x, .min = 0, .max = 1, .interval = 0.01 }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 })) changed = true;
        dvui.label(@src(), "G", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
        if (dvui.sliderEntry(@src(), null, .{ .value = &ptr.y, .min = 0, .max = 1, .interval = 0.01 }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 1 })) changed = true;
        dvui.label(@src(), "B", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
        if (dvui.sliderEntry(@src(), null, .{ .value = &ptr.z, .min = 0, .max = 1, .interval = 0.01 }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 2 })) changed = true;
        dvui.label(@src(), "A", .{}, .{ .color_text = col_w, .gravity_y = 0.5, .padding = .{ .x = 4 } });
        if (dvui.sliderEntry(@src(), null, .{ .value = &ptr.w, .min = 0, .max = 1, .interval = 0.01 }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 3 })) changed = true;
        return changed;
    }
    return false;
}

fn drawVec2i(label: []const u8, ptr: *math.Vector2i, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    var al_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        dvui.label(@src(), "({d}, {d})", .{ ptr.x, ptr.y }, .{ .id_extra = id });
        return false;
    }
    var changed = false;
    dvui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), i32, .{ .value = &ptr.x }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 2 }).changed) changed = true;
    dvui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), i32, .{ .value = &ptr.y }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 2 + 1 }).changed) changed = true;
    return changed;
}

fn drawVec3i(label: []const u8, ptr: *math.Vector3i, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    var al_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        dvui.label(@src(), "({d}, {d}, {d})", .{ ptr.x, ptr.y, ptr.z }, .{ .id_extra = id });
        return false;
    }
    var changed = false;
    dvui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), i32, .{ .value = &ptr.x }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 }).changed) changed = true;
    dvui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), i32, .{ .value = &ptr.y }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 + 1 }).changed) changed = true;
    dvui.label(@src(), "Z", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), i32, .{ .value = &ptr.z }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 + 2 }).changed) changed = true;
    return changed;
}

fn drawVec4i(label: []const u8, ptr: *math.Vector4i, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    var al_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        dvui.label(@src(), "({d}, {d}, {d}, {d})", .{ ptr.x, ptr.y, ptr.z, ptr.w }, .{ .id_extra = id });
        return false;
    }
    var changed = false;
    dvui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), i32, .{ .value = &ptr.x }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 }).changed) changed = true;
    dvui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), i32, .{ .value = &ptr.y }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 1 }).changed) changed = true;
    dvui.label(@src(), "Z", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), i32, .{ .value = &ptr.z }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 2 }).changed) changed = true;
    dvui.label(@src(), "W", .{}, .{ .color_text = col_w, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), i32, .{ .value = &ptr.w }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 3 }).changed) changed = true;
    return changed;
}

fn drawQuaternion(label: []const u8, ptr: *math.Quaternion, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);
    var al_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());

    var euler = quatToEulerDeg(ptr.*);

    const ro = ctx.read_only or hint.read_only;
    if (ro) {
        dvui.label(@src(), "P:{d:.1} Y:{d:.1} R:{d:.1}", .{ euler[0], euler[1], euler[2] }, .{ .id_extra = id });
        return false;
    }

    // Persist euler angles per widget id to avoid losing precision on unchanged axes.
    const eid = dvui.parentGet().extendId(@src(), id);
    const stored = dvui.dataGet(null, eid, "euler", [3]f32);
    // Only use stored euler if the underlying quaternion hasn't changed externally.
    const stored_quat = dvui.dataGet(null, eid, "quat", math.Quaternion);
    if (stored != null and stored_quat != null and std.meta.eql(stored_quat.?, ptr.*)) {
        euler = stored.?;
    }

    var changed = false;
    dvui.label(@src(), "P", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), f32, .{ .value = &euler[0] }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 }).changed) changed = true;
    dvui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), f32, .{ .value = &euler[1] }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 + 1 }).changed) changed = true;
    dvui.label(@src(), "R", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (dvui.textEntryNumber(@src(), f32, .{ .value = &euler[2] }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 + 2 }).changed) changed = true;

    if (changed) {
        ptr.* = math.Quaternion.fromEuler(euler[0], euler[1], euler[2]);
        dvui.dataSet(null, eid, "euler", euler);
        dvui.dataSet(null, eid, "quat", ptr.*);
    } else {
        dvui.dataSet(null, eid, "euler", euler);
        dvui.dataSet(null, eid, "quat", ptr.*);
    }

    return changed;
}

// ─── Matrix4 drawer (read-only) ───────────────────────────────────────────────

fn drawMatrix4(label: []const u8, ptr: *math.Matrix4, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    _ = ctx;
    _ = hint;
    if (dvui.expander(@src(), label, .{ .default_expanded = false }, .{
        .expand = .horizontal,
        .padding = .all(2),
        .id_extra = id,
    })) {
        var grid = dvui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 12, .y = 2 },
            .id_extra = id,
        });
        defer grid.deinit();
        // Column-major storage: m[col*4 + row]. Display in row-major visual order.
        inline for (0..4) |row_i| {
            var row_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
                .expand = .horizontal,
                .id_extra = row_i,
            });
            defer row_box.deinit();
            inline for (0..4) |col_i| {
                dvui.label(@src(), "{d:.3}", .{ptr.m[col_i * 4 + row_i]}, .{
                    .expand = .horizontal,
                    .gravity_x = 0.5,
                    .id_extra = row_i * 4 + col_i,
                });
            }
        }
    }
    return false;
}

// ─── Reference drawer ─────────────────────────────────────────────────────────

fn drawRef(
    comptime RefT: type,
    label: []const u8,
    ptr: *RefT,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    dvui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);
    var al_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());

    const ro = ctx.read_only or hint.read_only;
    if (ro) {
        const display = guidDisplayName(RefT._turian_ref_kind, ptr.slice());
        dvui.label(@src(), "{s}", .{display}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
        return false;
    }

    var changed = false;

    // Drop zone for drag-and-drop.
    if (drawRefDropZone(@src(), RefT._turian_ref_kind, ptr.slice(), id)) |new_guid| {
        ptr.set(new_guid);
        changed = true;
    }

    // Picker button — opens a floating list of matching assets or scene objects.
    // picker_id must be computed before the button so dataSet and dataGet share the same ID.
    const picker_id = dvui.parentGet().extendId(@src(), id);
    if (dvui.button(@src(), "...", .{}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 24 },
        .id_extra = id,
    })) {
        dvui.dataSet(null, picker_id, "picker_open", true);
    }
    const picker_open = dvui.dataGet(null, picker_id, "picker_open", bool) orelse false;
    if (picker_open) {
        var fw = dvui.floatingMenu(@src(), .{ .from = row.data().rectScale().r.toNatural() }, .{ .id_extra = id });
        defer fw.deinit();

        switch (RefT._turian_ref_kind) {
            .asset_ref => {
                const filter = comptime if (@hasDecl(RefT, "_turian_asset_filter"))
                    RefT._turian_asset_filter
                else
                    engine.AssetFilter.any;
                if (pickerAsset(filter, fw)) |g| {
                    ptr.set(g);
                    changed = true;
                    dvui.dataSet(null, picker_id, "picker_open", false);
                }
            },
            .game_object_ref, .component_ref => {
                if (pickerSceneObject(RefT._turian_ref_kind, ptr, fw)) {
                    changed = true;
                    dvui.dataSet(null, picker_id, "picker_open", false);
                }
            },
            else => {},
        }

        // Close on focus loss — but skip the check on the floatingMenu's first frame
        // because dvui only focuses a new floatingMenu on its second frame (via minSizeGet).
        if (dvui.minSizeGet(fw.data().id) != null and fw.data().id != dvui.focusedSubwindowId()) {
            dvui.dataSet(null, picker_id, "picker_open", false);
        }
    }

    return changed;
}

/// Floating list of assets matching `filter`. Returns the chosen GUID string
/// (into a static buffer, valid until the next call) or null if nothing picked.
/// The runtime `filter` lets both the comptime component path and the
/// reflection-driven script-field path share one picker.
var s_picked_guid_buf: [36]u8 = undefined;

fn pickerAsset(
    filter: engine.AssetFilter,
    fw: *dvui.FloatingMenuWidget,
) ?[]const u8 {
    // Built-in presets (material filter only).
    if (filter == .material) {
        dvui.label(@src(), "Built-in", .{}, .{ .expand = .horizontal, .style = .content });
        for (engine.Material.presets, 0..) |preset, pi| {
            if (dvui.menuItemLabel(@src(), preset.name, .{}, .{ .expand = .horizontal, .id_extra = pi })) |_| {
                fw.close();
                return preset.guid;
            }
        }
        _ = dvui.separator(@src(), .{ .expand = .horizontal, .margin = dvui.Rect.all(2) });
    }

    if (!EditorState.assetDbReady()) {
        dvui.label(@src(), "(no project open)", .{}, .{});
        return null;
    }
    const asset_type: editor.AssetType = switch (filter) {
        .any => .unknown,
        .mesh => .model,
        .texture => .image,
        .audio => .audio,
        .material => .material,
        .input_actions => .input_actions,
        .scene => .scene,
    };
    var any_shown = false;

    var idx: usize = 0;
    var map_it = EditorState.asset_db.by_guid.valueIterator();
    while (map_it.next()) |info| {
        if (filter != .any and info.asset_type != asset_type) continue;
        any_shown = true;
        const basename = if (std.mem.lastIndexOfScalar(u8, info.path, '/')) |sep|
            info.path[sep + 1 ..]
        else
            info.path;
        var guid_buf: [36]u8 = undefined;
        const guid_str = info.guid.toString(&guid_buf);
        if (dvui.menuItemLabel(@src(), basename, .{}, .{ .expand = .horizontal, .id_extra = idx })) |_| {
            const n = @min(guid_str.len, s_picked_guid_buf.len);
            @memcpy(s_picked_guid_buf[0..n], guid_str[0..n]);
            fw.close();
            return s_picked_guid_buf[0..n];
        }
        idx += 1;
    }
    if (!any_shown and filter != .any) dvui.label(@src(), "(no project assets)", .{}, .{});
    return null;
}

/// Reference row for a reflection-driven script `asset_ref` field: a drag-drop
/// zone plus a "..." picker filtered by `filter` (e.g. `.scene` shows only
/// scene assets). Returns the chosen GUID string, or null if unchanged.
pub fn drawScriptAssetRef(
    src: std.builtin.SourceLocation,
    current_guid: []const u8,
    filter: engine.AssetFilter,
    id: usize,
) ?[]const u8 {
    var row = dvui.box(src, .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();

    var picked: ?[]const u8 = null;

    if (drawRefDropZone(@src(), .asset_ref, current_guid, id)) |new_guid| picked = new_guid;

    const picker_id = dvui.parentGet().extendId(@src(), id);
    if (dvui.button(@src(), "...", .{}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 24 },
        .id_extra = id,
    })) {
        dvui.dataSet(null, picker_id, "picker_open", true);
    }
    const picker_open = dvui.dataGet(null, picker_id, "picker_open", bool) orelse false;
    if (picker_open) {
        var fw = dvui.floatingMenu(@src(), .{ .from = row.data().rectScale().r.toNatural() }, .{ .id_extra = id });
        defer fw.deinit();

        if (pickerAsset(filter, fw)) |g| {
            picked = g;
            dvui.dataSet(null, picker_id, "picker_open", false);
        }

        // Close on focus loss — skip the floatingMenu's first frame (dvui only
        // focuses a new floatingMenu on its second frame via minSizeGet).
        if (dvui.minSizeGet(fw.data().id) != null and fw.data().id != dvui.focusedSubwindowId()) {
            dvui.dataSet(null, picker_id, "picker_open", false);
        }
    }

    return picked;
}

fn pickerSceneObject(
    kind: engine.api.FieldType,
    ptr: anytype,
    fw: *dvui.FloatingMenuWidget,
) bool {
    _ = kind;
    var changed = false;
    if (EditorState.object_count == 0) {
        dvui.label(@src(), "(no scene objects)", .{}, .{});
        return false;
    }
    for (EditorState.objects[0..EditorState.object_count], 0..) |*obj, oi| {
        const gs = obj.guidSlice();
        if (gs.len == 0) continue;
        if (dvui.menuItemLabel(@src(), obj.nameSlice(), .{}, .{ .expand = .horizontal, .id_extra = oi })) |_| {
            ptr.set(gs);
            changed = true;
            fw.close();
        }
    }
    return changed;
}

// ─── Utilities ────────────────────────────────────────────────────────────────

fn canHaveDecls(comptime T: type) bool {
    return switch (@typeInfo(T)) {
        .@"struct", .@"union", .@"enum", .@"opaque" => true,
        else => false,
    };
}

fn fieldHint(comptime T: type, comptime name: []const u8) FieldHint {
    if (@hasDecl(T, "turian_hints") and @hasDecl(T.turian_hints, name))
        return @field(T.turian_hints, name);
    return .{};
}

fn castHintBound(comptime T: type, val: f64) T {
    return switch (@typeInfo(T)) {
        .float => @floatCast(val),
        .int => @intFromFloat(@trunc(val)),
        else => unreachable,
    };
}

fn quatToEulerDeg(q: math.Quaternion) [3]f32 {
    const sinr = 2.0 * (q.w * q.x + q.y * q.z);
    const cosr = 1.0 - 2.0 * (q.x * q.x + q.y * q.y);
    const pitch = std.math.atan2(sinr, cosr) * (180.0 / std.math.pi);

    const sinp = 2.0 * (q.w * q.y - q.z * q.x);
    const yaw = if (@abs(sinp) >= 1.0)
        std.math.copysign(@as(f32, 90.0), sinp)
    else
        std.math.asin(sinp) * (180.0 / std.math.pi);

    const siny = 2.0 * (q.w * q.z + q.x * q.y);
    const cosy = 1.0 - 2.0 * (q.y * q.y + q.z * q.z);
    const roll = std.math.atan2(siny, cosy) * (180.0 / std.math.pi);

    return .{ pitch, yaw, roll };
}

fn guidDisplayName(kind: engine.api.FieldType, guid_str: []const u8) []const u8 {
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

fn tooltipIfAny(
    src: std.builtin.SourceLocation,
    active_rect: dvui.Rect.Physical,
    hint: FieldHint,
    id: usize,
) void {
    if (hint.tooltip) |tip| {
        dvui.tooltip(src, .{ .active_rect = active_rect }, "{s}", .{tip}, .{ .id_extra = id });
    }
}
