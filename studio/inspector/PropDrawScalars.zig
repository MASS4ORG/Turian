const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const PropDrawMath = @import("PropDrawMath.zig");
const PropDrawReflect = @import("PropDrawReflect.zig");

const FieldHint = engine.FieldHint;
const DrawCtx = PropDrawMath.DrawCtx;
const tooltipIfAny = PropDrawMath.tooltipIfAny;

pub fn drawBool(label: []const u8, ptr: *bool, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
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

    const ro = ctx.read_only or hint.read_only;
    if (ro) {
        gui.label(@src(), "{}", .{ptr.*}, .{ .gravity_y = 0.5, .id_extra = id });
        return false;
    }
    return gui.checkbox(@src(), ptr, null, .{ .gravity_y = 0.5, .id_extra = id });
}

pub fn drawNumber(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
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

    const ro = ctx.read_only or hint.read_only;
    if (ro) {
        gui.label(@src(), "{d}", .{ptr.*}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
        return false;
    }

    // f32 with range → slider or sliderEntry
    if (comptime T == f32) {
        const has_range = hint.min != null and hint.max != null;
        if (has_range) {
            const lo: f32 = @floatCast(hint.min.?);
            const hi: f32 = @floatCast(hint.max.?);
            if (hint.widget == .slider_entry) {
                return gui.sliderEntry(@src(), null, .{
                    .value = ptr,
                    .min = lo,
                    .max = hi,
                    .interval = if (hint.step) |s| @floatCast(s) else null,
                }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
            }
            if (hint.widget == .slider) {
                var frac: f32 = if (hi > lo) std.math.clamp((ptr.* - lo) / (hi - lo), 0, 1) else 0;
                if (gui.slider(@src(), .{ .fraction = &frac }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id })) {
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

    const result = gui.textEntryNumber(@src(), T, .{
        .value = ptr,
        .min = if (hint.min) |m| PropDrawReflect.castHintBound(T, m) else null,
        .max = if (hint.max) |m| PropDrawReflect.castHintBound(T, m) else null,
    }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
    return result.changed;
}

pub fn drawEnum(
    comptime T: type,
    label: []const u8,
    ptr: *T,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
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

    const ro = ctx.read_only or hint.read_only;
    if (ro) {
        gui.label(@src(), "{s}", .{@tagName(ptr.*)}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
        return false;
    }
    return gui.dropdownEnum(@src(), T, .{ .choice = ptr }, .{}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
}

pub fn drawStringSlice(label: []const u8, ptr: *[]const u8, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    return drawStringEdit(label, ptr, hint, ctx, id);
}

pub fn drawStringEdit(label: []const u8, ptr: *[]const u8, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
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

    const ro = ctx.read_only or hint.read_only;
    if (ro or ctx.allocator == null) {
        gui.label(@src(), "{s}", .{ptr.*}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
        return false;
    }
    const alloc = ctx.allocator.?;

    var te = gui.textEntry(@src(), .{ .text = .{ .internal = .{} } }, .{
        .expand = .horizontal,
        .gravity_y = 0.5,
        .id_extra = id,
    });
    defer te.deinit();

    const focused = (gui.focusedWidgetId() orelse .zero) == te.data().id;
    var synced = false;
    if (!focused and !std.mem.eql(u8, te.getText(), ptr.*)) {
        te.textSet(ptr.*, false);
        synced = true;
    }
    if (te.text_changed and !synced) {
        ptr.* = alloc.dupe(u8, te.getText()) catch return false;
        return true;
    }
    return false;
}

pub fn drawEventBinding(
    label: []const u8,
    ptr: *engine.ui.EventBinding,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    switch (ptr.*) {
        .named => |*name| return drawEventNameDropdown(label, name, hint, ctx, id),
        .channel => |*ch| return @import("PropDrawRef.zig").drawRef(@TypeOf(ch.*), label, ch, hint, ctx, id),
    }
}

fn drawEventNameDropdown(label: []const u8, ptr: *[]const u8, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    const discovered = EditorState.discovered_events[0..EditorState.discovered_event_count];
    const ro = ctx.read_only or hint.read_only;
    if (ro or ctx.allocator == null or discovered.len == 0) {
        return drawStringEdit(label, ptr, hint, ctx, id);
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

    var entries_buf: [editor.event_scanner.MAX_EVENTS + 1][]const u8 = undefined;
    var n: usize = 0;
    for (discovered) |*e| {
        entries_buf[n] = e.name();
        n += 1;
    }
    var selected: usize = 0;
    var found = false;
    for (entries_buf[0..n], 0..) |e, i| {
        if (std.mem.eql(u8, e, ptr.*)) {
            selected = i;
            found = true;
            break;
        }
    }
    if (!found and ptr.len != 0) {
        entries_buf[n] = ptr.*;
        selected = n;
        n += 1;
    }
    const entries = entries_buf[0..n];

    const changed = gui.dropdown(@src(), entries, .{ .choice = &selected }, .{}, .{
        .expand = .horizontal,
        .gravity_y = 0.5,
        .id_extra = id,
    });
    if (changed) {
        ptr.* = ctx.allocator.?.dupe(u8, entries[selected]) catch return false;
        return true;
    }
    return false;
}
