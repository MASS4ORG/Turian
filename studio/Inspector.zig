const std = @import("std");
const dvui = @import("dvui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const PropDraw = @import("PropDraw.zig");
const MaterialEditor = @import("MaterialEditor.zig");
const DataAssetEditor = @import("DataAssetEditor.zig");

/// Draw the inspector panel for the selected object or asset.
pub fn draw() void {
    var outer = dvui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer outer.deinit();

    {
        var header = dvui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(6),
        });
        defer header.deinit();
        dvui.label(@src(), "Inspector", .{}, .{ .font = .theme(.heading) });
    }

    const sel = EditorState.selected_object orelse {
        if (EditorState.selected_asset_path) |asset_path| {
            drawAssetInspector(asset_path);
            return;
        }
        dvui.label(@src(), "No object selected.", .{}, .{ .gravity_x = 0.5, .padding = .all(12) });
        return;
    };

    if (sel >= EditorState.object_count) {
        EditorState.selected_object = null;
        return;
    }

    const obj = &EditorState.objects[sel];

    var scroll = dvui.scrollArea(@src(), .{}, .{ .expand = .both });
    defer scroll.deinit();

    {
        var row = dvui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .padding = .all(6),
        });
        defer row.deinit();

        _ = dvui.checkbox(@src(), &obj.active, null, .{ .gravity_y = 0.5 });
        dvui.label(@src(), "{s}", .{obj.nameSlice()}, .{
            .font = .theme(.heading),
            .gravity_y = 0.5,
            .expand = .horizontal,
        });
    }

    _ = dvui.separator(@src(), .{ .expand = .horizontal });

    if (dvui.expander(@src(), "Transform", .{ .default_expanded = true }, .{
        .expand = .horizontal,
        .padding = .all(4),
    })) {
        var comp_box = dvui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 12, .y = 4 },
        });
        defer comp_box.deinit();

        const obj_before = obj.*;
        if (PropDraw.drawComponent(EditorState.Transform, &obj.transform, 0, false)) {
            EditorState.pushCommand(dvui.frameTimeNS(), &.{ .modify_object = .{
                .idx = sel,
                .before = obj_before,
                .after = obj.*,
            } });
            EditorState.scene_dirty = true;
        }
    }

    _ = dvui.separator(@src(), .{ .expand = .horizontal, .id_extra = 0 });

    var remove_idx: ?usize = null;

    for (obj.components[0..obj.component_count], 0..) |*comp, ci| {
        if (dvui.expander(@src(), comp.displayName(), .{ .default_expanded = true }, .{
            .expand = .horizontal,
            .padding = .all(4),
            .id_extra = ci,
        })) {
            var fields_box = dvui.box(@src(), .{ .dir = .horizontal }, .{
                .expand = .horizontal,
                .padding = .{ .x = 12, .y = 4 },
                .id_extra = ci,
            });
            defer fields_box.deinit();

            var data_box = dvui.box(@src(), .{}, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = ci,
            });
            defer data_box.deinit();

            switch (comp.*) {
                .user_script => |*s| {
                    if (s.field_count == 0) {
                        dvui.label(@src(), "(no fields)", .{}, .{
                            .expand = .horizontal,
                            .id_extra = ci,
                        });
                    } else {
                        drawScriptFields(sel, obj, ci);
                    }
                },
                inline else => |*field_data| {
                    const obj_before_field = obj.*;
                    if (PropDraw.drawComponent(@TypeOf(field_data.*), field_data, ci + 1, false)) {
                        EditorState.pushCommand(dvui.frameTimeNS(), &.{ .modify_object = .{
                            .idx = sel,
                            .before = obj_before_field,
                            .after = obj.*,
                        } });
                        EditorState.scene_dirty = true;
                    }
                },
            }

            if (dvui.button(@src(), "Remove", .{}, .{
                .gravity_y = 0.5,
                .gravity_x = 1.0,
                .id_extra = ci,
                .style = .err,
            })) {
                remove_idx = ci;
            }
        }
        _ = dvui.separator(@src(), .{ .expand = .horizontal, .id_extra = ci + 1 });
    }

    if (remove_idx) |ri| {
        const removed_comp = obj.components[ri];
        obj.removeComponent(ri);
        EditorState.pushCommand(dvui.frameTimeNS(), &.{ .remove_component = .{
            .obj_idx = sel,
            .comp = removed_comp,
            .rem_idx = ri,
        } });
        EditorState.scene_dirty = true;
    }

    {
        var add_menu = dvui.menu(@src(), .vertical, .{
            .expand = .horizontal,
            .padding = .all(6),
        });
        defer add_menu.deinit();

        if (dvui.menuItemLabel(@src(), "Add Component  \u{25BE}", .{ .submenu = true }, .{
            .expand = .horizontal,
        })) |r| {
            var fw = dvui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();
            if (addComponentMenu(obj, fw)) {
                EditorState.pushCommand(dvui.frameTimeNS(), &.{ .add_component = .{
                    .obj_idx = sel,
                    .comp = obj.components[obj.component_count - 1],
                    .ins_idx = obj.component_count - 1,
                } });
                EditorState.scene_dirty = true;
            }
        }
    }
}

/// Iterate script fields, grouping consecutive dotted-prefix fields (from flattened
/// nested structs) under a collapsible expander labelled by the struct field name.
fn drawScriptFields(sel: usize, obj: *EditorState.SceneNode, ci: usize) void {
    const s = &obj.components[ci].user_script;
    var fi: usize = 0;
    while (fi < s.field_count) {
        const name = s.field_values[fi].nameSlice();
        const dot = std.mem.indexOfScalar(u8, name, '.');
        if (dot) |d| {
            // Collect the run of fields sharing this first-level prefix.
            const prefix = name[0..d];
            const run_start = fi;
            fi += 1;
            while (fi < s.field_count) {
                const n2 = s.field_values[fi].nameSlice();
                const d2 = std.mem.indexOfScalar(u8, n2, '.') orelse break;
                if (!std.mem.eql(u8, n2[0..d2], prefix)) break;
                fi += 1;
            }
            // Draw the whole run under an expander.
            const group_id = ci * 100000 + run_start * 10 + 1;
            if (dvui.expander(@src(), prefix, .{ .default_expanded = true }, .{
                .expand = .horizontal,
                .padding = .all(2),
                .id_extra = group_id,
            })) {
                var indent = dvui.box(@src(), .{}, .{
                    .expand = .horizontal,
                    .padding = .{ .x = 12, .y = 0 },
                    .id_extra = group_id,
                });
                defer indent.deinit();
                for (run_start..fi) |gfi| drawScriptField(sel, obj, ci, gfi);
            }
        } else {
            drawScriptField(sel, obj, ci, fi);
            fi += 1;
        }
    }
}

/// Draw a labelled row for a single ScriptFieldValue. Returns true if changed.
/// `id` must be unique among siblings at the same nesting level.
/// Used by both the scene inspector and the DataAssetEditor.
pub fn drawScriptFieldValue(fv: *engine.ScriptFieldValue, id: usize) bool {
    const full_name = fv.nameSlice();
    const display_name = if (std.mem.lastIndexOfScalar(u8, full_name, '.')) |d|
        full_name[d + 1 ..]
    else
        full_name;

    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();

    dvui.label(@src(), "{s}", .{display_name}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 80 },
        .id_extra = id,
    });

    var changed = false;
    switch (fv.kind) {
        .f32 => {
            const r = dvui.textEntryNumber(@src(), f32, .{ .value = &fv.as_f32 }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            if (r.changed) changed = true;
        },
        .i32 => {
            const r = dvui.textEntryNumber(@src(), i32, .{ .value = &fv.as_i32 }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            if (r.changed) changed = true;
        },
        .bool => {
            if (dvui.checkbox(@src(), &fv.as_bool, null, .{ .gravity_y = 0.5, .id_extra = id })) {
                changed = true;
            }
        },
        .vec3 => {
            var v = engine.Vector3{ .x = fv.as_vec3_x, .y = fv.as_vec3_y, .z = fv.as_vec3_z };
            if (PropDraw.drawVec3Row(@src(), &v)) {
                fv.as_vec3_x = v.x;
                fv.as_vec3_y = v.y;
                fv.as_vec3_z = v.z;
                changed = true;
            }
        },
        .f64 => {
            const r = dvui.textEntryNumber(@src(), f64, .{ .value = &fv.as_f64 }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            if (r.changed) changed = true;
        },
        .i64 => {
            const r = dvui.textEntryNumber(@src(), i64, .{ .value = &fv.as_i64 }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            if (r.changed) changed = true;
        },
        .u32 => {
            const r = dvui.textEntryNumber(@src(), u32, .{ .value = &fv.as_u32 }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            if (r.changed) changed = true;
        },
        .vec2 => {
            var v = engine.Vector2{ .x = fv.as_vec2_x, .y = fv.as_vec2_y };
            if (PropDraw.drawVec2Row(@src(), &v)) {
                fv.as_vec2_x = v.x;
                fv.as_vec2_y = v.y;
                changed = true;
            }
        },
        .vec4 => {
            var v = engine.Vector4{ .x = fv.as_vec4_x, .y = fv.as_vec4_y, .z = fv.as_vec4_z, .w = fv.as_vec4_w };
            if (PropDraw.drawVec4Row(@src(), &v)) {
                fv.as_vec4_x = v.x;
                fv.as_vec4_y = v.y;
                fv.as_vec4_z = v.z;
                fv.as_vec4_w = v.w;
                changed = true;
            }
        },
        .string => {
            var te = dvui.textEntry(@src(), .{
                .text = .{ .buffer = fv.as_string[0..] },
            }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            defer te.deinit();
            if (te.text_changed) changed = true;
        },
        .game_object_ref, .component_ref, .asset_ref => {
            if (PropDraw.drawRefDropZone(@src(), fv.kind, fv.refSlice(), id)) |new_guid| {
                if (!std.mem.eql(u8, fv.refSlice(), new_guid)) {
                    fv.setRef(new_guid);
                    changed = true;
                }
            }
        },
    }
    return changed;
}

fn drawScriptField(sel: usize, obj: *EditorState.SceneNode, ci: usize, fi: usize) void {
    const s = &obj.components[ci].user_script;
    const fv = &s.field_values[fi];
    const id = ci * 10000 + fi;

    const obj_before = obj.*;
    if (drawScriptFieldValue(fv, id)) {
        EditorState.pushCommand(dvui.frameTimeNS(), &.{ .modify_object = .{
            .idx = sel,
            .before = obj_before,
            .after = obj.*,
        } });
        EditorState.scene_dirty = true;
    }
}

fn drawAssetInspector(asset_path: []const u8) void {
    const asset_type = editor.asset_registry.lookupByFilename(asset_path);
    const desc = editor.asset_registry.get(asset_type);

    const file_name = if (std.mem.lastIndexOfScalar(u8, asset_path, '/')) |sep|
        asset_path[sep + 1 ..]
    else
        asset_path;

    var scroll = dvui.scrollArea(@src(), .{}, .{ .expand = .both });
    defer scroll.deinit();

    {
        var row = dvui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .padding = .all(6),
        });
        defer row.deinit();
        dvui.label(@src(), "{s}", .{file_name}, .{ .font = .theme(.heading), .expand = .horizontal });
    }

    _ = dvui.separator(@src(), .{ .expand = .horizontal });

    {
        var info = dvui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 8, .y = 6 },
        });
        defer info.deinit();

        dvui.label(@src(), "Type:  {s}", .{desc.name}, .{});
        dvui.label(@src(), "Path:  {s}", .{asset_path}, .{});
    }

    if (asset_type == .material) {
        _ = dvui.separator(@src(), .{ .expand = .horizontal, .id_extra = 1 });
        MaterialEditor.draw(asset_path);
    }

    if (asset_type == .data_asset) {
        _ = dvui.separator(@src(), .{ .expand = .horizontal, .id_extra = 2 });
        DataAssetEditor.draw(asset_path);
    }
}

fn addComponentMenu(obj: *EditorState.SceneNode, fw: *dvui.FloatingMenuWidget) bool {
    var added = false;
    var prev_is_builtin: ?bool = null;

    for (EditorState.discovered_components[0..EditorState.discovered_count], 0..) |*def, di| {
        if (prev_is_builtin) |prev| {
            if (prev and !def.is_builtin) {
                _ = dvui.separator(@src(), .{ .expand = .horizontal, .margin = dvui.Rect.all(4) });
                dvui.label(@src(), "Scripts", .{}, .{
                    .padding = .{ .x = 8, .y = 4 },
                    .font = .theme(.body),
                });
            }
        } else {
            dvui.label(@src(), "Components", .{}, .{
                .padding = .{ .x = 8, .y = 4 },
                .font = .theme(.body),
            });
        }
        prev_is_builtin = def.is_builtin;

        if (dvui.menuItemLabel(@src(), def.displayName(), .{}, .{
            .expand = .horizontal,
            .id_extra = di,
        }) != null) {
            if (EditorState.makeComponent(def)) |c| {
                _ = obj.addComponent(c);
                added = true;
            }
            fw.close();
        }
    }
    return added;
}
