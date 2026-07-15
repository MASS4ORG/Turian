const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const PropDrawMath = @import("PropDrawMath.zig");
const PropDrawReflect = @import("PropDrawReflect.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

const FieldHint = engine.FieldHint;
const DrawCtx = PropDrawMath.DrawCtx;
const tooltipIfAny = PropDrawMath.tooltipIfAny;

var s_accepted_guid_buf: [36]u8 = undefined;
var s_last_ref_click_ns: i128 = 0;
var s_last_ref_click_id: gui.Id = .zero;
var s_picked_guid_buf: [36]u8 = undefined;

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

    const display = PropDrawReflect.guidDisplayName(kind, current_guid);
    gui.label(@src(), "{s}", .{display}, .{
        .expand = .horizontal,
        .gravity_y = 0.5,
        .id_extra = id_extra,
    });

    return accepted;
}

pub fn drawRef(
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
        const display = PropDrawReflect.guidDisplayName(RefT._turian_ref_kind, ptr.slice());
        gui.label(@src(), "{s}", .{display}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = id });
        return false;
    }

    var changed = false;

    if (drawRefDropZone(@src(), RefT._turian_ref_kind, ptr.slice(), id)) |new_guid| {
        ptr.set(new_guid);
        changed = true;
    }

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

        if (gui.minSizeGet(fw.data().id) != null and fw.data().id != gui.focusedSubwindowId()) {
            gui.dataSet(null, picker_id, "picker_open", false);
        }
    }

    return changed;
}

pub fn pickerAsset(
    filter: engine.AssetFilter,
    fw: *gui.FloatingMenuWidget,
) ?[]const u8 {
    if (filter == .material) {
        gui.label(@src(), "{s}", .{tr("Built-in")}, .{ .expand = .horizontal, .style = .content });
        for (engine.Material.presets, 0..) |preset, pi| {
            if (gui.menuItemLabel(@src(), preset.name, .{}, .{ .expand = .horizontal, .id_extra = pi })) |_| {
                fw.close();
                return preset.guid;
            }
        }
        _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(2) });
    }

    if (!EditorState.assetDbReady()) {
        gui.label(@src(), "{s}", .{tr("(no project open)")}, .{});
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
        .game_event => .data_asset,
        .font => .font,
        .ui_theme => .ui_theme,
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
    if (!any_shown and filter != .any) gui.label(@src(), "{s}", .{tr("(no project assets)")}, .{});
    return null;
}

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
        gui.label(@src(), "{s}", .{tr("(no scene objects)")}, .{});
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
