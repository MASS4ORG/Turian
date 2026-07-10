const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const PropDrawMath = @import("PropDrawMath.zig");

const FieldHint = engine.FieldHint;
const math = engine.math;

// ─── Re-exports from PropDrawMath ─────────────────────────────────────────────
pub const DrawCtx = PropDrawMath.DrawCtx;
pub const drawVec3Row = PropDrawMath.drawVec3Row;
pub const drawVec2Row = PropDrawMath.drawVec2Row;
pub const drawVec4Row = PropDrawMath.drawVec4Row;
const col_x = PropDrawMath.col_x;
const col_y = PropDrawMath.col_y;
const col_z = PropDrawMath.col_z;
const col_w = PropDrawMath.col_w;
const tooltipIfAny = PropDrawMath.tooltipIfAny;

// ─── Public entry points ──────────────────────────────────────────────────────

/// Draw all inspector fields for a component struct `T`.
/// `id_base` must be unique per component slot (e.g. the component index).
/// Returns true if any field value changed this frame.
pub fn drawComponent(comptime T: type, ptr: *T, id_base: usize, read_only: bool) bool {
    return drawComponentAlloc(T, ptr, id_base, read_only, std.heap.page_allocator);
}

/// Like `drawComponent`, but string/slice mutations are duped into `allocator`
/// instead of the session-lifetime page allocator — used by editors whose
/// model is arena-owned (e.g. `UiDocumentEditor`).
pub fn drawComponentAlloc(comptime T: type, ptr: *T, id_base: usize, read_only: bool, allocator: std.mem.Allocator) bool {
    // Check for full type-level override first.
    if (comptime canHaveDecls(T) and @hasDecl(T, "turian_draw")) {
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

    // Engine types with a dedicated drawer, matched by identity (C2) — these
    // render correctly wherever they appear in any component/struct.
    if (comptime T == engine.ui.EventBinding) return drawEventBinding(label, ptr, hint, ctx, id);

    return switch (comptime @typeInfo(T)) {
        .bool => drawBool(label, ptr, hint, ctx, id),
        .int, .float => drawNumber(T, label, ptr, hint, ctx, id),
        .@"enum" => drawEnum(T, label, ptr, hint, ctx, id),
        .optional => drawOptional(T, label, ptr, hint, ctx, id),
        .@"struct" => drawStructValue(T, label, ptr, hint, ctx, id),
        .pointer => |info| if (info.size == .slice and info.child == u8)
            drawStringSlice(label, ptr, hint, ctx, id)
        else if (info.size == .slice)
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

// drawVec3Row / drawVec2Row / drawVec4Row are re-exported from PropDrawMath above.

/// Drop-zone that accepts dragged asset / game-object references.
/// Returns the accepted GUID string if something was dropped, null otherwise.
var s_accepted_guid_buf: [36]u8 = undefined;

// Double-click tracking for asset-ref drop zones (reveal in browser).
var s_last_ref_click_ns: i128 = 0;
var s_last_ref_click_id: gui.Id = .zero;

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

    var drop_box = gui.box(src, .{}, .{
        .expand = .horizontal,
        .gravity_y = 0.5,
        .border = .all(if (drag_compatible) 2 else 1),
        .style = if (drag_compatible) .highlight else .content,
        .padding = .{ .x = 4, .y = 2 },
        .corners = .all(3),
        .id_extra = id_extra,
    });
    defer drop_box.deinit();

    var accepted: ?[]const u8 = null;
    for (gui.events()) |*e| {
        if (!gui.eventMatchSimple(e, drop_box.data())) continue;
        switch (e.evt) {
            .mouse => |me| {
                // Double-click an asset reference to reveal it in the asset
                // browser (navigate to its folder + highlight it).
                if (kind == .asset_ref and me.action == .press and me.button == .left) {
                    const now = gui.frameTimeNS();
                    const dbl = s_last_ref_click_id == drop_box.data().id and
                        now - s_last_ref_click_ns < 500 * std.time.ns_per_ms;
                    s_last_ref_click_id = drop_box.data().id;
                    s_last_ref_click_ns = now;
                    if (dbl) {
                        if (EditorState.resolveAssetGuid(current_guid)) |path| {
                            e.handle(@src(), drop_box.data());
                            EditorState.revealAsset(path);
                        }
                    }
                }
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
    gui.label(@src(), "{s}", .{display}, .{
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
        const label = comptime displayLabel(field.name, hint);
        if (comptime @hasDecl(T, "turian_drawers") and @hasDecl(T.turian_drawers, field.name)) {
            const drawer = comptime @field(T.turian_drawers, field.name);
            changed = drawer(label, &@field(ptr, field.name), hint, ctx, fi) or changed;
        } else {
            if (drawValue(field.type, label, &@field(ptr, field.name), hint, ctx, fi))
                changed = true;
        }
    }

    // Pass 2 — grouped fields, each group under its own collapsible expander.
    inline for (groups, 0..) |g, gi| {
        if (gui.expander(@src(), g, .{ .default_expanded = true }, .{
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

            // Fresh alignment scope for each group.
            var grp_al = gui.Alignment.init(@src(), gi);
            defer grp_al.deinit();
            var grp_ctx = DrawCtx{ .al = &grp_al, .depth = ctx.depth + 1, .read_only = ctx.read_only, .allocator = ctx.allocator };

            inline for (std.meta.fields(T), 0..) |field, fi| {
                const hint = comptime fieldHint(T, field.name);
                if (comptime hint.hidden) continue;
                if (comptime hint.group == null or !std.mem.eql(u8, hint.group.?, g)) continue;
                const label = comptime displayLabel(field.name, hint);
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
    // Known engine math types matched by identity — delegated to PropDrawMath.
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
    const ro = ctx.read_only or hint.read_only;

    // Scoped so the row closes before the indented inner value below —
    // never deinit a dvui widget twice (use-after-deinit assert).
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
            gui.label(@src(), "{s}", .{if (has_val) "set" else "null"}, .{ .gravity_y = 0.5, .id_extra = id });
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

// ─── Leaf drawers ─────────────────────────────────────────────────────────────

fn drawBool(label: []const u8, ptr: *bool, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
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

fn drawNumber(
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

fn drawStringSlice(label: []const u8, ptr: *[]const u8, hint: FieldHint, ctx: *DrawCtx, id: usize) bool {
    return drawStringEdit(label, ptr, hint, ctx, id);
}

/// Editable `[]const u8` (C2): the text entry keeps its own persistent buffer
/// (dvui `.internal` storage keyed by widget id) and re-syncs from the model
/// whenever the widget is not focused — external mutations (undo, document
/// reload, selection change) show up, but an in-progress edit is never
/// stomped. On change the new text is duped into `ctx.allocator` and assigned
/// to `ptr`; without an allocator the field renders read-only.
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
        // Model -> widget sync (dvui's textSet itself raises text_changed,
        // so remember this wasn't a user edit).
        te.textSet(ptr.*, false);
        synced = true;
    }
    if (te.text_changed and !synced) {
        ptr.* = alloc.dupe(u8, te.getText()) catch return false;
        return true;
    }
    return false;
}

/// `EventBinding` drawer (C2): v1's `named` variant is an event-name picker;
/// future variants get their own row here without touching any component
/// editor.
fn drawEventBinding(
    label: []const u8,
    ptr: *engine.ui.EventBinding,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
    switch (ptr.*) {
        .named => |*name| return drawEventNameDropdown(label, name, hint, ctx, id),
        // #107 reframed on #41's event-channel slice: a plain asset picker,
        // filtered to `.game_event`-kind assets via `_turian_asset_filter` —
        // `drawRef` (the same generic ref/asset-picker every other
        // `TypedAssetRef`/`GameObjectRef` field already uses) needs no new UI.
        .channel => |*ch| return drawRef(@TypeOf(ch.*), label, ch, hint, ctx, id),
    }
}

/// Event-name picker (#112): a dropdown of every `event_name` the project's
/// scripts declare (`EditorState.discovered_events`, via `EventScanner`'s
/// Zig-AST scan), so authors pick a name the game actually defines instead of
/// hand-typing one that might not resolve. Falls back to the plain text entry
/// (v1's hand-typed name, still a valid escape hatch — e.g. before the
/// relevant script exists yet) when read-only, allocator-less, or nothing has
/// been discovered.
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

    // Entries: every discovered event name, plus the current value if it
    // isn't one of them (an unresolved/stale/custom binding) — so picking
    // from the list never silently discards the existing value.
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
        // Show null-terminated portion only.
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

    // ── Element rows (indented) ───────────────────────────────────────────────
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
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();
    gui.label(@src(), "{s}: (unsupported type)", .{label}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
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

// Math type drawers (drawVec2 … drawMatrix4) live in PropDrawMath.zig.

// ─── Reference drawer ─────────────────────────────────────────────────────────

fn drawRef(
    comptime RefT: type,
    label: []const u8,
    ptr: *RefT,
    hint: FieldHint,
    ctx: *DrawCtx,
    id: usize,
) bool {
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
    if (ro) {
        const display = guidDisplayName(RefT._turian_ref_kind, ptr.slice());
        gui.label(@src(), "{s}", .{display}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
        return false;
    }

    var changed = false;

    if (drawRefDropZone(@src(), RefT._turian_ref_kind, ptr.slice(), id)) |new_guid| {
        ptr.set(new_guid);
        changed = true;
    }

    // Picker button — opens a floating list of matching assets or scene objects.
    // picker_id must be computed before the button so dataSet and dataGet share the same ID.
    const picker_id = gui.parentGet().extendId(@src(), id);
    if (gui.button(@src(), "...", .{}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 24 },
        .id_extra = id,
    })) {
        gui.dataSet(null, picker_id, "picker_open", true);
    }
    const picker_open = gui.dataGet(null, picker_id, "picker_open", bool) orelse false;
    if (picker_open) {
        var fw = gui.floatingMenu(@src(), .{ .from = row.data().rectScale().r.toNatural() }, .{ .id_extra = id });
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
                    gui.dataSet(null, picker_id, "picker_open", false);
                }
            },
            .game_object_ref, .component_ref => {
                if (pickerSceneObject(RefT._turian_ref_kind, ptr, fw)) {
                    changed = true;
                    gui.dataSet(null, picker_id, "picker_open", false);
                }
            },
            else => {},
        }

        // Close on focus loss — but skip the check on the floatingMenu's first frame
        // because dvui only focuses a new floatingMenu on its second frame (via minSizeGet).
        if (gui.minSizeGet(fw.data().id) != null and fw.data().id != gui.focusedSubwindowId()) {
            gui.dataSet(null, picker_id, "picker_open", false);
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
    fw: *gui.FloatingMenuWidget,
) ?[]const u8 {
    // Built-in presets (material filter only).
    if (filter == .material) {
        gui.label(@src(), "Built-in", .{}, .{ .expand = .horizontal, .style = .content });
        for (engine.Material.presets, 0..) |preset, pi| {
            if (gui.menuItemLabel(@src(), preset.name, .{}, .{ .expand = .horizontal, .id_extra = pi })) |_| {
                fw.close();
                return preset.guid;
            }
        }
        _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(2) });
    }

    if (!EditorState.assetDbReady()) {
        gui.label(@src(), "(no project open)", .{}, .{});
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
        .ui_document => .ui_document,
        // GameEvent channels are authored as generic `.asset` JSON data
        // assets (#41/#107) — no dedicated file format/extension of their own.
        .game_event => .data_asset,
        .font => .font,
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
        if (gui.menuItemLabel(@src(), basename, .{}, .{ .expand = .horizontal, .id_extra = idx })) |_| {
            const n = @min(guid_str.len, s_picked_guid_buf.len);
            @memcpy(s_picked_guid_buf[0..n], guid_str[0..n]);
            fw.close();
            return s_picked_guid_buf[0..n];
        }
        idx += 1;
    }
    if (!any_shown and filter != .any) gui.label(@src(), "(no project assets)", .{}, .{});
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
    var row = gui.box(src, .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();

    var picked: ?[]const u8 = null;

    if (drawRefDropZone(@src(), .asset_ref, current_guid, id)) |new_guid| picked = new_guid;

    const picker_id = gui.parentGet().extendId(@src(), id);
    if (gui.button(@src(), "...", .{}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 24 },
        .id_extra = id,
    })) {
        gui.dataSet(null, picker_id, "picker_open", true);
    }
    const picker_open = gui.dataGet(null, picker_id, "picker_open", bool) orelse false;
    if (picker_open) {
        var fw = gui.floatingMenu(@src(), .{ .from = row.data().rectScale().r.toNatural() }, .{ .id_extra = id });
        defer fw.deinit();

        if (pickerAsset(filter, fw)) |g| {
            picked = g;
            gui.dataSet(null, picker_id, "picker_open", false);
        }

        // Close on focus loss — skip the floatingMenu's first frame (dvui only
        // focuses a new floatingMenu on its second frame via minSizeGet).
        if (gui.minSizeGet(fw.data().id) != null and fw.data().id != gui.focusedSubwindowId()) {
            gui.dataSet(null, picker_id, "picker_open", false);
        }
    }

    return picked;
}

fn pickerSceneObject(
    kind: engine.api.FieldType,
    ptr: anytype,
    fw: *gui.FloatingMenuWidget,
) bool {
    _ = kind;
    var changed = false;
    if (EditorState.object_count == 0) {
        gui.label(@src(), "(no scene objects)", .{}, .{});
        return false;
    }
    for (EditorState.objects[0..EditorState.object_count], 0..) |*obj, oi| {
        const gs = obj.guidSlice();
        if (gs.len == 0) continue;
        if (gui.menuItemLabel(@src(), obj.nameSlice(), .{}, .{ .expand = .horizontal, .id_extra = oi })) |_| {
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

/// The label to show for a struct field: `hint.label` if the field declared
/// one, otherwise `field_name` title-cased (see `titleCase`). Shared by
/// `drawStructFields` and any caller drawing a single field directly (e.g.
/// `SettingsEditor`'s per-field rows) so labels look the same everywhere.
pub fn displayLabel(comptime field_name: []const u8, hint: FieldHint) []const u8 {
    if (hint.label) |l| return l;
    return comptime titleCase(field_name);
}

/// Converts a `snake_case` identifier into a display label at comptime:
/// underscores become spaces and only the first character is capitalized
/// (e.g. `move_speed` -> `Move speed`, `show_editor_fps` -> `Show editor fps`).
fn titleCase(comptime name: []const u8) []const u8 {
    comptime {
        var buf: [name.len]u8 = undefined;
        for (name, 0..) |c, i| buf[i] = if (c == '_') ' ' else c;
        if (buf.len > 0) buf[0] = std.ascii.toUpper(buf[0]);
        const final = buf;
        return &final;
    }
}

fn castHintBound(comptime T: type, val: f64) T {
    return switch (@typeInfo(T)) {
        .float => @floatCast(val),
        .int => @intFromFloat(@trunc(val)),
        else => unreachable,
    };
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
