//! Inspector panel for `.asset` (DataAsset) instances.
//!
//! In-memory state uses engine.ScriptFieldValue (fixed buffers, safe for dvui
//! text entry widgets). Conversion to/from SceneScriptField happens only on
//! load and save.
const std = @import("std");
const dvui = @import("dvui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const Inspector = @import("Inspector.zig");

const ComponentDef = editor.ComponentDef;
const DataAssetFile = editor.DataAssetFile;
const SceneScriptField = editor.SceneScriptField;

const MAX_FIELDS = editor.scanner.MAX_COMP_FIELDS;

// ── Persistent frame state ─────────────────────────────────────────────────────

var loaded_path_buf: [1024]u8 = undefined;
var loaded_path_len: usize = 0;

var type_name_buf: [editor.scanner.MAX_COMP_NAME]u8 = undefined;
var type_name_len: usize = 0;

var fields: [MAX_FIELDS]engine.ScriptFieldValue = undefined;
var field_count: usize = 0;
var dirty: bool = false;

fn loadedPath() []const u8 {
    return loaded_path_buf[0..loaded_path_len];
}

fn loadedTypeName() []const u8 {
    return type_name_buf[0..type_name_len];
}

/// Draw the data-asset editor for the asset at `asset_path`.
/// Loads (or reloads) when the selection changes.
pub fn draw(asset_path: []const u8) void {
    if (!std.mem.eql(u8, asset_path, loadedPath())) load(asset_path);

    const def = findDef(loadedTypeName());

    {
        var info = dvui.box(@src(), .{}, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 } });
        defer info.deinit();
        if (def) |d| {
            dvui.label(@src(), "Type:  {s}", .{d.displayName()}, .{});
        } else {
            dvui.label(@src(), "Type:  {s} (definition not found)", .{loadedTypeName()}, .{});
        }
    }

    _ = dvui.separator(@src(), .{ .expand = .horizontal });

    if (field_count == 0) {
        dvui.label(@src(), "(no fields)", .{}, .{ .expand = .horizontal, .padding = .all(8) });
    } else {
        drawFields();
    }

    _ = dvui.separator(@src(), .{ .expand = .horizontal, .id_extra = 9001 });
    {
        var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .all(6) });
        defer row.deinit();

        if (dirty) {
            dvui.label(@src(), "Unsaved changes", .{}, .{ .gravity_y = 0.5, .expand = .horizontal });
        } else {
            dvui.label(@src(), "Saved", .{}, .{ .gravity_y = 0.5, .expand = .horizontal });
        }

        if (dvui.button(@src(), "Save", .{}, .{ .gravity_y = 0.5, .style = if (dirty) .highlight else .control })) {
            saveCurrent();
        }
    }
}

// ── Internal helpers ───────────────────────────────────────────────────────────

fn drawFields() void {
    var fi: usize = 0;
    while (fi < field_count) {
        const name = fields[fi].nameSlice();
        const dot = std.mem.indexOfScalar(u8, name, '.');
        if (dot) |d| {
            const prefix = name[0..d];
            const run_start = fi;
            fi += 1;
            while (fi < field_count) {
                const n2 = fields[fi].nameSlice();
                const d2 = std.mem.indexOfScalar(u8, n2, '.') orelse break;
                if (!std.mem.eql(u8, n2[0..d2], prefix)) break;
                fi += 1;
            }
            const group_id = run_start * 10 + 1;
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
                for (run_start..fi) |gfi| {
                    if (Inspector.drawScriptFieldValue(&fields[gfi], gfi + 20000)) dirty = true;
                }
            }
        } else {
            if (Inspector.drawScriptFieldValue(&fields[fi], fi + 20000)) dirty = true;
            fi += 1;
        }
    }
}

fn findDef(type_name: []const u8) ?*const ComponentDef {
    if (type_name.len == 0) return null;
    for (EditorState.discovered_components[0..EditorState.discovered_count]) |*d| {
        if (d.kind == .data_asset and std.mem.eql(u8, d.typeName(), type_name)) return d;
    }
    return null;
}

fn load(asset_path: []const u8) void {
    const n = @min(asset_path.len, loaded_path_buf.len);
    @memcpy(loaded_path_buf[0..n], asset_path[0..n]);
    loaded_path_len = n;
    dirty = false;
    field_count = 0;
    type_name_len = 0;

    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const file: ?DataAssetFile = editor.data_asset_io.load(arena, dvui.io, asset_path) catch null;
    if (file == null) return;
    const f = file.?;

    const tn = f.type_name;
    const tl = @min(tn.len, type_name_buf.len);
    @memcpy(type_name_buf[0..tl], tn[0..tl]);
    type_name_len = tl;

    // Merge stored fields onto schema (if def found), then convert to ScriptFieldValue.
    var scene_fields: [MAX_FIELDS]SceneScriptField = undefined;
    const nf: usize = if (findDef(f.type_name)) |def|
        editor.data_asset_io.mergeFields(def, f.fields, &scene_fields)
    else blk: {
        const count = @min(f.fields.len, MAX_FIELDS);
        for (f.fields[0..count], 0..) |sf, i| scene_fields[i] = sf;
        break :blk count;
    };

    field_count = nf;
    for (scene_fields[0..nf], 0..) |*sf, i| {
        fields[i] = sceneFieldToValue(sf);
    }
}

fn saveCurrent() void {
    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const scene_fields = arena.alloc(SceneScriptField, field_count) catch return;
    for (fields[0..field_count], 0..) |*fv, i| {
        scene_fields[i] = valueToSceneField(fv);
    }

    const source_file: []const u8 = if (findDef(loadedTypeName())) |d| d.sourceFile() else "";
    const file = DataAssetFile{
        .version = 1,
        .type_name = loadedTypeName(),
        .source_file = source_file,
        .fields = scene_fields,
    };

    editor.data_asset_io.save(dvui.io, loadedPath(), file) catch return;
    dirty = false;

    if (EditorState.project_path) |proj| {
        editor.asset_importer.importAssetForce(dvui.io, arena, proj, loadedPath());
    }
}

// ── Type conversion (SceneScriptField ↔ engine.ScriptFieldValue) ──────────────

fn sceneFieldToValue(sf: *const SceneScriptField) engine.ScriptFieldValue {
    var fv = engine.ScriptFieldValue{};
    fv.setName(sf.name);
    fv.kind = sf.kind;
    fv.as_f32 = sf.as_f32;
    fv.as_f64 = sf.as_f64;
    fv.as_i32 = sf.as_i32;
    fv.as_i64 = sf.as_i64;
    fv.as_u32 = sf.as_u32;
    fv.as_bool = sf.as_bool;
    fv.as_vec2_x = sf.as_vec2_x;
    fv.as_vec2_y = sf.as_vec2_y;
    fv.as_vec3_x = sf.as_vec3_x;
    fv.as_vec3_y = sf.as_vec3_y;
    fv.as_vec3_z = sf.as_vec3_z;
    fv.as_vec4_x = sf.as_vec4_x;
    fv.as_vec4_y = sf.as_vec4_y;
    fv.as_vec4_z = sf.as_vec4_z;
    fv.as_vec4_w = sf.as_vec4_w;
    fv.setRef(sf.as_ref_guid);
    fv.setString(sf.as_string);
    return fv;
}

fn valueToSceneField(fv: *const engine.ScriptFieldValue) SceneScriptField {
    return .{
        .name = fv.nameSlice(),
        .kind = fv.kind,
        .as_f32 = fv.as_f32,
        .as_f64 = fv.as_f64,
        .as_i32 = fv.as_i32,
        .as_i64 = fv.as_i64,
        .as_u32 = fv.as_u32,
        .as_bool = fv.as_bool,
        .as_vec2_x = fv.as_vec2_x,
        .as_vec2_y = fv.as_vec2_y,
        .as_vec3_x = fv.as_vec3_x,
        .as_vec3_y = fv.as_vec3_y,
        .as_vec3_z = fv.as_vec3_z,
        .as_vec4_x = fv.as_vec4_x,
        .as_vec4_y = fv.as_vec4_y,
        .as_vec4_z = fv.as_vec4_z,
        .as_vec4_w = fv.as_vec4_w,
        .as_ref_guid = fv.refSlice(),
        .as_string = fv.stringSlice(),
    };
}
