const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const PropDraw = @import("PropDraw.zig");
const MaterialEditor = @import("MaterialEditor.zig");
const DataAssetEditor = @import("DataAssetEditor.zig");
const InputActionsEditor = @import("InputActionsEditor.zig");
const ProjectSettingsEditor = @import("ProjectSettingsEditor.zig");
const ImportSettingsEditor = @import("ImportSettingsEditor.zig");
const PreviewSystem = @import("PreviewSystem.zig");
const Preview3D = @import("Preview3D.zig");
const MeshBounds = @import("MeshBounds.zig");

/// Draw the inspector panel for the selected object or asset.
pub fn draw() void {
    var outer = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer outer.deinit();

    {
        var header = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(6),
        });
        defer header.deinit();
        gui.label(@src(), "Inspector", .{}, .{ .font = .theme(.heading) });
    }

    const sel = EditorState.selected_object orelse {
        if (EditorState.selected_asset_path) |asset_path| {
            drawAssetInspector(asset_path);
        }
        // Empty/blank state when nothing is selected (cleaner than a message).
        return;
    };

    if (sel >= EditorState.object_count) {
        EditorState.selected_object = null;
        return;
    }

    const obj = &EditorState.objects[sel];
    const prefab_root = EditorState.prefabInstanceRoot(sel);

    var scroll = gui.scrollArea(@src(), .{}, .{
        .expand = .both,
        .min_size_content = .{ .h = 0 },
        .max_size_content = .height(0),
    });
    defer scroll.deinit();

    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .padding = .all(6),
        });
        defer row.deinit();

        _ = gui.checkbox(@src(), &obj.active, null, .{ .gravity_y = 0.5 });
        gui.label(@src(), "{s}", .{obj.nameSlice()}, .{
            .font = .theme(.heading),
            .gravity_y = 0.5,
            .expand = .horizontal,
        });
    }

    if (prefab_root) |root| drawPrefabBanner(obj, root);

    _ = gui.separator(@src(), .{ .expand = .horizontal });

    if (gui.expander(@src(), if (obj.hasOverride(.transform)) "Transform  (overridden)" else "Transform", .{ .default_expanded = true }, .{
        .expand = .horizontal,
        .padding = .all(4),
    })) {
        var comp_box = gui.box(@src(), .{}, .{
            .expand = .horizontal,
            .padding = .{ .x = 12, .y = 4 },
        });
        defer comp_box.deinit();

        const obj_before = obj.*;
        if (PropDraw.drawComponent(EditorState.Transform, &obj.transform, 0, false)) {
            EditorState.pushCommand(gui.frameTimeNS(), &.{ .modify_object = .{
                .idx = sel,
                .before = obj_before,
                .after = obj.*,
            } });
            EditorState.scene_dirty = true;
        }
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 0 });

    var remove_idx: ?usize = null;

    for (obj.components[0..obj.component_count], 0..) |*comp, ci| {
        if (gui.expander(@src(), comp.displayName(), .{ .default_expanded = true }, .{
            .expand = .horizontal,
            .padding = .all(4),
            .id_extra = ci,
        })) {
            var fields_box = gui.box(@src(), .{ .dir = .horizontal }, .{
                .expand = .horizontal,
                .padding = .{ .x = 12, .y = 4 },
                .id_extra = ci,
            });
            defer fields_box.deinit();

            var data_box = gui.box(@src(), .{}, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = ci,
            });
            defer data_box.deinit();

            switch (comp.*) {
                .user_script => |*s| {
                    if (s.field_count == 0) {
                        gui.label(@src(), "(no fields)", .{}, .{
                            .expand = .horizontal,
                            .id_extra = ci,
                        });
                    } else {
                        drawScriptFields(sel, obj, ci);
                    }
                },
                .mesh_renderer => |*mr| {
                    const obj_before_field = obj.*;
                    var changed = PropDraw.drawComponent(@TypeOf(mr.*), mr, ci + 1, false);

                    // When the mesh is set to a model and no material is bound
                    // yet, default to the model's generated (primary) material.
                    const prev_mesh = obj_before_field.components[ci].mesh_renderer.mesh.slice();
                    const mesh_changed = !std.mem.eql(u8, prev_mesh, mr.mesh.slice());
                    if (mesh_changed and mr.material.slice().len == 0) {
                        var guid_buf: [36]u8 = undefined;
                        if (EditorState.modelPrimaryMaterial(gui.io, mr.mesh.slice(), &guid_buf)) |g| {
                            mr.material.set(g);
                            changed = true;
                        }
                    }

                    if (changed) {
                        EditorState.pushCommand(gui.frameTimeNS(), &.{ .modify_object = .{
                            .idx = sel,
                            .before = obj_before_field,
                            .after = obj.*,
                        } });
                        EditorState.scene_dirty = true;
                    }
                },
                inline else => |*field_data| {
                    const obj_before_field = obj.*;
                    if (PropDraw.drawComponent(@TypeOf(field_data.*), field_data, ci + 1, false)) {
                        EditorState.pushCommand(gui.frameTimeNS(), &.{ .modify_object = .{
                            .idx = sel,
                            .before = obj_before_field,
                            .after = obj.*,
                        } });
                        EditorState.scene_dirty = true;
                    }
                },
            }

            if (gui.button(@src(), "Remove", .{}, .{
                .gravity_y = 0.5,
                .gravity_x = 1.0,
                .id_extra = ci,
                .style = .err,
            })) {
                remove_idx = ci;
            }
        }
        _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = ci + 1 });
    }

    if (remove_idx) |ri| {
        const removed_comp = obj.components[ri];
        obj.removeComponent(ri);
        EditorState.pushCommand(gui.frameTimeNS(), &.{ .remove_component = .{
            .obj_idx = sel,
            .comp = removed_comp,
            .rem_idx = ri,
        } });
        EditorState.scene_dirty = true;
    }

    {
        var add_menu = gui.menu(@src(), .vertical, .{
            .expand = .horizontal,
            .padding = .all(6),
        });
        defer add_menu.deinit();

        if (gui.menuItemLabel(@src(), "Add Component...", .{ .submenu = true }, .{
            .expand = .horizontal,
        })) |r| {
            var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
            defer fw.deinit();
            if (addComponentMenu(obj, fw)) {
                EditorState.pushCommand(gui.frameTimeNS(), &.{ .add_component = .{
                    .obj_idx = sel,
                    .comp = obj.components[obj.component_count - 1],
                    .ins_idx = obj.component_count - 1,
                } });
                EditorState.scene_dirty = true;
            }
        }
    }

    // Keep override highlights live: re-derive this instance's overrides from
    // its (stable) source template each frame, after any edits above.
    if (prefab_root) |root| EditorState.recomputePrefabOverrides(gui.io, root);
}

/// Banner shown atop the inspector for a prefab-instance node: identifies it as
/// an instance, lists the node's overridden groups, and offers Revert / Apply.
fn drawPrefabBanner(obj: *EditorState.SceneNode, root: usize) void {
    var banner = gui.box(@src(), .{ .dir = .horizontal }, .{
        .expand = .horizontal,
        .padding = .all(6),
        .margin = .{ .x = 4, .y = 2 },
        .background = true,
        .border = .all(1),
        .corner_radius = gui.Rect.all(4),
        .style = .highlight,
    });
    defer banner.deinit();

    gui.icon(@src(), "prefab", gui.entypo.box, .{}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 16, .h = 16 } });

    const groups = [_]engine.scene.OverrideGroup{ .name, .active, .transform, .components };
    var ovr_buf: [96]u8 = undefined;
    var w = std.Io.Writer.fixed(&ovr_buf);
    var any = false;
    for (groups) |g| {
        if (obj.hasOverride(g)) {
            if (any) w.writeAll(", ") catch {};
            w.writeAll(g.key()) catch {};
            any = true;
        }
    }
    gui.label(@src(), "Prefab Instance", .{}, .{ .gravity_y = 0.5, .font = .theme(.body) });
    if (any) {
        gui.label(@src(), "  overrides: {s}", .{w.buffered()}, .{ .gravity_y = 0.5, .expand = .horizontal });
    } else {
        gui.label(@src(), "", .{}, .{ .gravity_y = 0.5, .expand = .horizontal });
    }

    if (gui.button(@src(), "Revert", .{}, .{ .gravity_y = 0.5 })) {
        _ = EditorState.revertPrefabInstance(gui.frameTimeNS(), gui.io, root);
    }
    if (gui.button(@src(), "Apply", .{}, .{ .gravity_y = 0.5 })) {
        _ = EditorState.applyPrefabInstance(gui.frameTimeNS(), gui.io, root);
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
            if (gui.expander(@src(), prefix, .{ .default_expanded = true }, .{
                .expand = .horizontal,
                .padding = .all(2),
                .id_extra = group_id,
            })) {
                var indent = gui.box(@src(), .{}, .{
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

    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = id });
    defer row.deinit();

    gui.label(@src(), "{s}", .{display_name}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 80 },
        .id_extra = id,
    });

    var changed = false;
    switch (fv.kind) {
        .f32 => {
            const r = gui.textEntryNumber(@src(), f32, .{ .value = &fv.as_f32 }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            if (r.changed) changed = true;
        },
        .i32 => {
            const r = gui.textEntryNumber(@src(), i32, .{ .value = &fv.as_i32 }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            if (r.changed) changed = true;
        },
        .bool => {
            if (gui.checkbox(@src(), &fv.as_bool, null, .{ .gravity_y = 0.5, .id_extra = id })) {
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
            const r = gui.textEntryNumber(@src(), f64, .{ .value = &fv.as_f64 }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            if (r.changed) changed = true;
        },
        .i64 => {
            const r = gui.textEntryNumber(@src(), i64, .{ .value = &fv.as_i64 }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            if (r.changed) changed = true;
        },
        .u32 => {
            const r = gui.textEntryNumber(@src(), u32, .{ .value = &fv.as_u32 }, .{
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
            var te = gui.textEntry(@src(), .{
                .text = .{ .buffer = fv.as_string[0..] },
            }, .{
                .expand = .horizontal,
                .gravity_y = 0.5,
                .id_extra = id,
            });
            defer te.deinit();
            if (te.text_changed) changed = true;
        },
        .asset_ref => {
            // Drop zone + typed asset picker (e.g. `.scene` shows only scenes).
            if (PropDraw.drawScriptAssetRef(@src(), fv.refSlice(), fv.asset_filter, id)) |new_guid| {
                if (!std.mem.eql(u8, fv.refSlice(), new_guid)) {
                    fv.setRef(new_guid);
                    changed = true;
                }
            }
        },
        .game_object_ref, .component_ref => {
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
        EditorState.pushCommand(gui.frameTimeNS(), &.{ .modify_object = .{
            .idx = sel,
            .before = obj_before,
            .after = obj.*,
        } });
        EditorState.scene_dirty = true;
    }
}

/// Host a non-scene asset's dedicated editor full-area as an MDI document tab
///. Reuses the same per-type editor dispatch the inspector uses when
/// an asset is merely selected, but as the main editing surface.
pub fn drawAssetDocument(asset_path: []const u8) void {
    var outer = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer outer.deinit();
    drawAssetInspector(asset_path);
}

fn drawAssetInspector(asset_path: []const u8) void {
    const asset_type = editor.asset_registry.lookupByFilename(asset_path);

    const file_name = if (std.mem.lastIndexOfScalar(u8, asset_path, '/')) |sep|
        asset_path[sep + 1 ..]
    else
        asset_path;

    var scroll = gui.scrollArea(@src(), .{}, .{
        .expand = .both,
        .min_size_content = .{ .h = 0 },
        .max_size_content = .height(0),
    });
    defer scroll.deinit();

    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .padding = .all(6),
        });
        defer row.deinit();
        gui.label(@src(), "{s}", .{file_name}, .{ .font = .theme(.heading), .expand = .horizontal });
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal });

    if (asset_type == .material) {
        _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 1 });
        MaterialEditor.draw(asset_path);
    }

    if (asset_type == .data_asset) {
        _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 2 });
        DataAssetEditor.draw(asset_path);
    }

    if (asset_type == .input_actions) {
        _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 3 });
        InputActionsEditor.draw(asset_path);
    }

    if (asset_type == .project_settings) {
        _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 4 });
        ProjectSettingsEditor.draw(asset_path);
    }

    if (ImportSettingsEditor.handles(asset_type)) {
        _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 5 });
        ImportSettingsEditor.draw(asset_path, asset_type);
    }

    // A model's generated sub-assets (materials/textures) are NOT listed here:
    // they're shown as their own selectable tiles in the Asset Browser when the
    // model tile is expanded (its ▸ toggle), Unity-style — see
    // `AssetBrowser.drawExpandedSubAssets`. Duplicating them as an Inspector
    // list only confused which surface "owns" them.

    // Shared preview panel (issue #19), Unity-style: bottom of the panel,
    // with a Show/Hide toggle. Materials get their own *interactive* preview
    // (mesh swap + orbit) at the top of `MaterialEditor` instead, since
    // editing a material wants a live, unsaved-edits-aware render rather than
    // the cached static one used here.
    if (asset_type != .material) {
        _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 6 });
        drawPreviewPanel(asset_path, asset_type);
    }
}

var preview_enabled: bool = true;
var model_preview_panel: Preview3D.Panel = .{};

/// Bottom preview panel for non-material assets. Models get an interactive
/// orbit-drag preview (like the material editor's); texture/audio get the
/// cached static thumbnail from `PreviewSystem`. Toggleable — hidden state
/// persists across selections for the session (not saved to disk).
fn drawPreviewPanel(asset_path: []const u8, asset_type: editor.AssetType) void {
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 } });
        defer row.deinit();
        gui.label(@src(), "Preview", .{}, .{ .gravity_y = 0.5, .expand = .horizontal, .font = .theme(.heading) });
        _ = gui.checkbox(@src(), &preview_enabled, "Show", .{ .gravity_y = 0.5 });
    }
    if (!preview_enabled) return;

    if (asset_type == .model) {
        drawModelPreview(asset_path);
        return;
    }

    const source = PreviewSystem.imageSourceFor(asset_path) orelse return;
    var box = gui.box(@src(), .{}, .{
        .expand = .horizontal,
        .gravity_x = 0.5,
        .padding = .all(8),
    });
    defer box.deinit();
    _ = gui.image(@src(), .{ .source = source, .shrink = .ratio }, .{
        .min_size_content = .{ .w = 160, .h = 160 },
        .gravity_x = 0.5,
    });
}

/// Interactive model preview: the mesh with its resolved default material
/// (mirrors the auto-assign logic in the mesh_renderer field above),
/// auto-framed to its bounds, orbit-drag-to-look — the same
/// `Preview3D.Panel` the material editor uses.
fn drawModelPreview(asset_path: []const u8) void {
    if (!EditorState.assetDbReady()) return;
    const info = EditorState.asset_db.findByPath(asset_path) orelse return;
    var guid_buf: [36]u8 = undefined;
    const guid = info.guid.toString(&guid_buf);

    const bounds = MeshBounds.local(guid) orelse return;
    const center = engine.Vector3{
        .x = (bounds.min.x + bounds.max.x) * 0.5,
        .y = (bounds.min.y + bounds.max.y) * 0.5,
        .z = (bounds.min.z + bounds.max.z) * 0.5,
    };
    const ext = engine.Vector3{ .x = bounds.max.x - bounds.min.x, .y = bounds.max.y - bounds.min.y, .z = bounds.max.z - bounds.min.z };
    const radius = @sqrt(ext.x * ext.x + ext.y * ext.y + ext.z * ext.z) * 0.5;

    var mat_buf: [36]u8 = undefined;
    const mat_guid = EditorState.modelPrimaryMaterial(gui.io, guid, &mat_buf) orelse engine.Material.presets[0].guid;

    model_preview_panel.ensureFramed(guid, center, if (radius > 0.001) radius else 0.5);
    const lights = Preview3D.keyFillLights();
    const nodes = [_]engine.SceneNode{ lights[0], lights[1], Preview3D.meshNode(guid, mat_guid) };
    model_preview_panel.draw(&nodes, 220);
}

fn addComponentMenu(obj: *EditorState.SceneNode, fw: *gui.FloatingMenuWidget) bool {
    var added = false;
    var prev_is_builtin: ?bool = null;

    for (EditorState.discovered_components[0..EditorState.discovered_count], 0..) |*def, di| {
        if (prev_is_builtin) |prev| {
            if (prev and !def.is_builtin) {
                _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });
                gui.label(@src(), "Scripts", .{}, .{
                    .padding = .{ .x = 8, .y = 4 },
                    .font = .theme(.body),
                });
            }
        } else {
            gui.label(@src(), "Components", .{}, .{
                .padding = .{ .x = 8, .y = 4 },
                .font = .theme(.body),
            });
        }
        prev_is_builtin = def.is_builtin;

        if (gui.menuItemLabel(@src(), def.displayName(), .{}, .{
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
