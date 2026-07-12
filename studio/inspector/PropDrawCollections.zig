const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const PropDrawMath = @import("PropDrawMath.zig");

const FieldHint = engine.FieldHint;
const DrawCtx = PropDrawMath.DrawCtx;
const tooltipIfAny = PropDrawMath.tooltipIfAny;

pub fn drawStringArray(
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
        gui.label(@src(), "{s}", .{label}, .{ .id_extra = id });
        if (ro) return false;
        var te = gui.textEntry(@src(), .{
            .text = .{ .buffer = buf },
            .multiline = true,
        }, .{ .expand = .horizontal, .min_size_content = .{ .h = 80 }, .id_extra = id });
        defer te.deinit();
        return te.text_changed;
    }

    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
    tooltipIfAny(@src(), row.data().rectScale().r, hint, id);
    var aligned = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .margin = ctx.al.margin(row.data().id),
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer aligned.deinit();
    ctx.al.record(row.data().id, aligned.data());

    if (ro) {
        const end = std.mem.indexOfScalar(u8, buf, 0) orelse buf.len;
        gui.label(@src(), "{s}", .{buf[0..end]}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
        return false;
    }
    var te = gui.textEntry(@src(), .{
        .text = .{ .buffer = buf },
    }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
    defer te.deinit();
    return te.text_changed;
}

pub fn drawGenericArray(
    comptime T: type,
    comptime Child: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
    drawValue: fn (comptime type, []const u8, anytype, FieldHint, *DrawCtx, usize) bool,
) bool {
    const len = @typeInfo(T).array.len;
    if (gui.expander(@src(), label, .{ .default_expanded = false }, .{
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
        var arr_al = gui.Alignment.init(@src(), id);
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

pub fn drawGenericSlice(
    comptime T: type,
    comptime Child: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
    drawValue: fn (comptime type, []const u8, anytype, FieldHint, *DrawCtx, usize) bool,
) bool {
    const effective_ro = ctx.read_only or hint.read_only;
    const can_mutate = !effective_ro and ctx.allocator != null;

    var add = false;
    {
        var hdr = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .padding = .{ .x = 0, .y = 2 },
            .id_extra = id,
        });
        defer hdr.deinit();

        gui.label(@src(), "{s}", .{label}, .{ .gravity_y = 0.5, .margin = .{ .y = 4 }, .id_extra = id });
        gui.label(@src(), "[{d}]", .{ptr.*.len}, .{
            .expand = .horizontal,
            .gravity_y = 0.5,
            .id_extra = id,
        });
        if (can_mutate) {
            if (gui.button(@src(), "+", .{}, .{ .gravity_y = 0.5, .id_extra = id })) add = true;
        }
    }

    var changed = false;
    var remove_idx: ?usize = null;

    if (ptr.*.len > 0) {
        var indent = gui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 12, .y = 0 },
            .id_extra = id,
        });
        defer indent.deinit();

        var elem_al = gui.Alignment.init(@src(), id);
        defer elem_al.deinit();
        var elem_ctx = DrawCtx{
            .al = &elem_al,
            .depth = ctx.depth + 1,
            .read_only = effective_ro,
            .allocator = ctx.allocator,
        };

        for (0..ptr.*.len) |ei| {
            var row = gui.box(@src(), .{ .dir = .horizontal }, .{
                .expand = .horizontal,
                .id_extra = id * 10000 + ei + 1,
            });
            defer row.deinit();

            if (can_mutate) {
                if (gui.button(@src(), "×", .{}, .{
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
