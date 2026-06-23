/// Extracted from PropDraw.zig: DrawCtx, axis-colour palette, legacy vec-row
/// helpers, and all math-type inspector drawers.  PropDraw.zig imports these
/// and re-exports the public surface so callers see no change.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");

const FieldHint = engine.FieldHint;
const math = engine.math;

// ─── Axis colour palette ──────────────────────────────────────────────────────
pub const col_x = gui.Color{ .r = 210, .g = 60, .b = 60 };
pub const col_y = gui.Color{ .r = 60, .g = 180, .b = 60 };
pub const col_z = gui.Color{ .r = 60, .g = 100, .b = 210 };
pub const col_w = gui.Color{ .r = 180, .g = 100, .b = 210 };

// ─── DrawCtx ──────────────────────────────────────────────────────────────────

/// Threaded context for the recursive property drawer.
/// Do not persist across frames — create fresh each draw call.
pub const DrawCtx = struct {
    al: *gui.Alignment,
    depth: u32 = 0,
    read_only: bool = false,
    /// Allocator for slice field mutations (add/remove). Null disables those controls.
    allocator: ?std.mem.Allocator = null,
};

// ─── Tooltip utility ──────────────────────────────────────────────────────────

pub fn tooltipIfAny(
    src: std.builtin.SourceLocation,
    active_rect: gui.Rect.Physical,
    hint: FieldHint,
    id: usize,
) void {
    if (hint.tooltip) |tip| {
        gui.tooltip(src, .{ .active_rect = active_rect }, "{s}", .{tip}, .{ .id_extra = id });
    }
}

// ─── Legacy row helpers (kept for Inspector.drawScriptField) ──────────────────

/// Draw a row of X / Y / Z number inputs for a Vector3 value.
/// Returns true if any component changed.
pub fn drawVec3Row(src: std.builtin.SourceLocation, v: *engine.Vector3) bool {
    var changed = false;
    var row = gui.box(src, .{ .dir = .horizontal }, .{ .expand = .horizontal });
    defer row.deinit();

    gui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rx = gui.textEntryNumber(@src(), f32, .{ .value = &v.x }, .{ .min_size_content = .{ .w = 52, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rx.changed) changed = true;

    gui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const ry = gui.textEntryNumber(@src(), f32, .{ .value = &v.y }, .{ .min_size_content = .{ .w = 52, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (ry.changed) changed = true;

    gui.label(@src(), "Z", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rz = gui.textEntryNumber(@src(), f32, .{ .value = &v.z }, .{ .min_size_content = .{ .w = 52, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rz.changed) changed = true;

    return changed;
}

/// Draw a row of X / Y number inputs for a Vector2 value.
/// Returns true if any component changed.
pub fn drawVec2Row(src: std.builtin.SourceLocation, v: *engine.Vector2) bool {
    var changed = false;
    var row = gui.box(src, .{ .dir = .horizontal }, .{ .expand = .horizontal });
    defer row.deinit();

    gui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rx = gui.textEntryNumber(@src(), f32, .{ .value = &v.x }, .{ .min_size_content = .{ .w = 52, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rx.changed) changed = true;

    gui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const ry = gui.textEntryNumber(@src(), f32, .{ .value = &v.y }, .{ .min_size_content = .{ .w = 52, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (ry.changed) changed = true;

    return changed;
}

/// Draw a row of X / Y / Z / W number inputs for a Vector4 value.
/// Returns true if any component changed.
pub fn drawVec4Row(src: std.builtin.SourceLocation, v: *engine.Vector4) bool {
    var changed = false;
    var row = gui.box(src, .{ .dir = .horizontal }, .{ .expand = .horizontal });
    defer row.deinit();

    gui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rx = gui.textEntryNumber(@src(), f32, .{ .value = &v.x }, .{ .min_size_content = .{ .w = 44, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rx.changed) changed = true;

    gui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const ry = gui.textEntryNumber(@src(), f32, .{ .value = &v.y }, .{ .min_size_content = .{ .w = 44, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (ry.changed) changed = true;

    gui.label(@src(), "Z", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rz = gui.textEntryNumber(@src(), f32, .{ .value = &v.z }, .{ .min_size_content = .{ .w = 44, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rz.changed) changed = true;

    gui.label(@src(), "W", .{}, .{ .color_text = col_w, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    const rw = gui.textEntryNumber(@src(), f32, .{ .value = &v.w }, .{ .min_size_content = .{ .w = 44, .h = 20 }, .expand = .horizontal, .gravity_y = 0.5 });
    if (rw.changed) changed = true;

    return changed;
}

// ─── Math type drawers ────────────────────────────────────────────────────────

pub fn drawVec2(label: []const u8, ptr: *math.Vector2, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    var al_box = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        gui.label(@src(), "({d:.3}, {d:.3})", .{ ptr.x, ptr.y }, .{ .id_extra = id });
        return false;
    }
    var changed = false;
    gui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), f32, .{ .value = &ptr.x }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 2 }).changed) changed = true;
    gui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), f32, .{ .value = &ptr.y }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 2 + 1 }).changed) changed = true;
    return changed;
}

pub fn drawVec3(label: []const u8, ptr: *math.Vector3, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);
    var al_box = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        gui.label(@src(), "({d:.3}, {d:.3}, {d:.3})", .{ ptr.x, ptr.y, ptr.z }, .{ .id_extra = id });
        return false;
    }
    return drawVec3Row(@src(), ptr);
}

pub fn drawVec4(label: []const u8, ptr: *math.Vector4, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    var al_box = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        gui.label(@src(), "({d:.3}, {d:.3}, {d:.3}, {d:.3})", .{ ptr.x, ptr.y, ptr.z, ptr.w }, .{ .id_extra = id });
        return false;
    }
    var changed = false;
    gui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), f32, .{ .value = &ptr.x }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 }).changed) changed = true;
    gui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), f32, .{ .value = &ptr.y }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 1 }).changed) changed = true;
    gui.label(@src(), "Z", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), f32, .{ .value = &ptr.z }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 2 }).changed) changed = true;
    gui.label(@src(), "W", .{}, .{ .color_text = col_w, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), f32, .{ .value = &ptr.w }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 3 }).changed) changed = true;
    return changed;
}

pub fn drawColorVec4(label: []const u8, ptr: *math.Vector4, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);
    var al_box = gui.box(@src(), .{ .dir = .horizontal }, .{
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
    var swatch = gui.box(@src(), .{}, .{
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
        gui.label(@src(), "R", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
        if (gui.sliderEntry(@src(), null, .{ .value = &ptr.x, .min = 0, .max = 1, .interval = 0.01 }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 })) changed = true;
        gui.label(@src(), "G", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
        if (gui.sliderEntry(@src(), null, .{ .value = &ptr.y, .min = 0, .max = 1, .interval = 0.01 }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 1 })) changed = true;
        gui.label(@src(), "B", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
        if (gui.sliderEntry(@src(), null, .{ .value = &ptr.z, .min = 0, .max = 1, .interval = 0.01 }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 2 })) changed = true;
        gui.label(@src(), "A", .{}, .{ .color_text = col_w, .gravity_y = 0.5, .padding = .{ .x = 4 } });
        if (gui.sliderEntry(@src(), null, .{ .value = &ptr.w, .min = 0, .max = 1, .interval = 0.01 }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 3 })) changed = true;
        return changed;
    }
    return false;
}

pub fn drawVec2i(label: []const u8, ptr: *math.Vector2i, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    var al_box = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        gui.label(@src(), "({d}, {d})", .{ ptr.x, ptr.y }, .{ .id_extra = id });
        return false;
    }
    var changed = false;
    gui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), i32, .{ .value = &ptr.x }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 2 }).changed) changed = true;
    gui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), i32, .{ .value = &ptr.y }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 2 + 1 }).changed) changed = true;
    return changed;
}

pub fn drawVec3i(label: []const u8, ptr: *math.Vector3i, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    var al_box = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        gui.label(@src(), "({d}, {d}, {d})", .{ ptr.x, ptr.y, ptr.z }, .{ .id_extra = id });
        return false;
    }
    var changed = false;
    gui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), i32, .{ .value = &ptr.x }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 }).changed) changed = true;
    gui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), i32, .{ .value = &ptr.y }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 + 1 }).changed) changed = true;
    gui.label(@src(), "Z", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), i32, .{ .value = &ptr.z }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 + 2 }).changed) changed = true;
    return changed;
}

pub fn drawVec4i(label: []const u8, ptr: *math.Vector4i, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    var al_box = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer al_box.deinit();
    ctx.al.record(row.data().id, al_box.data());
    if (ctx.read_only or hint.read_only) {
        gui.label(@src(), "({d}, {d}, {d}, {d})", .{ ptr.x, ptr.y, ptr.z, ptr.w }, .{ .id_extra = id });
        return false;
    }
    var changed = false;
    gui.label(@src(), "X", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), i32, .{ .value = &ptr.x }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 }).changed) changed = true;
    gui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), i32, .{ .value = &ptr.y }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 1 }).changed) changed = true;
    gui.label(@src(), "Z", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), i32, .{ .value = &ptr.z }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 2 }).changed) changed = true;
    gui.label(@src(), "W", .{}, .{ .color_text = col_w, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), i32, .{ .value = &ptr.w }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 4 + 3 }).changed) changed = true;
    return changed;
}

pub fn drawQuaternion(label: []const u8, ptr: *math.Quaternion, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);
    var al_box = gui.box(@src(), .{ .dir = .horizontal }, .{
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
        gui.label(@src(), "P:{d:.1} Y:{d:.1} R:{d:.1}", .{ euler[0], euler[1], euler[2] }, .{ .id_extra = id });
        return false;
    }

    // Persist euler angles per widget id to avoid losing precision on unchanged axes.
    const eid = gui.parentGet().extendId(@src(), id);
    const stored = gui.dataGet(null, eid, "euler", [3]f32);
    // Only use stored euler if the underlying quaternion hasn't changed externally.
    const stored_quat = gui.dataGet(null, eid, "quat", math.Quaternion);
    if (stored != null and stored_quat != null and std.meta.eql(stored_quat.?, ptr.*)) {
        euler = stored.?;
    }

    var changed = false;
    gui.label(@src(), "P", .{}, .{ .color_text = col_x, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), f32, .{ .value = &euler[0] }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 }).changed) changed = true;
    gui.label(@src(), "Y", .{}, .{ .color_text = col_y, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), f32, .{ .value = &euler[1] }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 + 1 }).changed) changed = true;
    gui.label(@src(), "R", .{}, .{ .color_text = col_z, .gravity_y = 0.5, .padding = .{ .x = 4 } });
    if (gui.textEntryNumber(@src(), f32, .{ .value = &euler[2] }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id * 3 + 2 }).changed) changed = true;

    if (changed) {
        ptr.* = math.Quaternion.fromEuler(euler[0], euler[1], euler[2]);
        gui.dataSet(null, eid, "euler", euler);
        gui.dataSet(null, eid, "quat", ptr.*);
    } else {
        gui.dataSet(null, eid, "euler", euler);
        gui.dataSet(null, eid, "quat", ptr.*);
    }

    return changed;
}

pub fn drawMatrix4(label: []const u8, ptr: *math.Matrix4, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    _ = ctx;
    _ = hint;
    if (gui.expander(@src(), label, .{ .default_expanded = false }, .{
        .expand = .horizontal,
        .padding = .all(2),
        .id_extra = id,
    })) {
        var grid = gui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 12, .y = 2 },
            .id_extra = id,
        });
        defer grid.deinit();
        // Column-major storage: m[col*4 + row]. Display in row-major visual order.
        inline for (0..4) |row_i| {
            var row_box = gui.box(@src(), .{ .dir = .horizontal }, .{
                .expand = .horizontal,
                .id_extra = row_i,
            });
            defer row_box.deinit();
            inline for (0..4) |col_i| {
                gui.label(@src(), "{d:.3}", .{ptr.m[col_i * 4 + row_i]}, .{
                    .expand = .horizontal,
                    .gravity_x = 0.5,
                    .id_extra = row_i * 4 + col_i,
                });
            }
        }
    }
    return false;
}

// ─── Quaternion utility ───────────────────────────────────────────────────────

pub fn quatToEulerDeg(q: math.Quaternion) [3]f32 {
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
