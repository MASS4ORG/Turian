const std = @import("std");
const engine = @import("engine");
const editor = @import("editor");

const State = @import("State.zig");
const EditorState = @import("EditorState.zig");
const UndoRedo = @import("UndoRedo.zig");

pub const Vector3 = engine.Vector3;
pub const Transform = engine.Transform;
pub const Component = engine.Component;
pub const UserScriptRef = engine.UserScriptRef;
pub const SceneNode = engine.SceneNode;
pub const Project = engine.Project;
pub const MAX_OBJECTS = engine.scene.MAX_OBJECTS;
pub const NAME_MAX = engine.scene.NAME_MAX;
pub const ComponentDef = editor.ComponentDef;

pub const MAX_DISCOVERED = editor.scanner.MAX_COMPONENTS;

pub fn resolveAssetGuid(guid_str: []const u8) ?[]const u8 {
    if (guid_str.len == 0 or !State.assetDbReady()) return null;
    const guid = editor.Guid.parse(guid_str) catch return null;
    return if (EditorState.asset_db.findByGuid(guid)) |info| info.path else null;
}

/// Resolve the project's "first scene": the scene referenced by
/// `ProjectSettings.first_scene`, falling back to `scene-01.json` or the first
/// scene asset in the project. Returns the asset path (owned by `asset_db`, so
/// it outlives `arena`) or null if no scene exists. Mirrors the build-time
/// boot-scene resolution in `editor/GameBuild.zig`.
pub fn firstScenePath(io: std.Io, arena: std.mem.Allocator) ?[]const u8 {
    if (!State.assetDbReady()) return null;

    var settings_it = EditorState.asset_db.enumerate(.project_settings);
    if (settings_it.next()) |info| {
        if (readFileArena(io, arena, info.path)) |bytes| {
            if (engine.ProjectSettings.loadFromBytes(arena, bytes)) |ps| {
                if (ps.first_scene.len > 0) {
                    if (editor.Guid.parse(ps.first_scene)) |gid| {
                        if (EditorState.asset_db.findByGuid(gid)) |sinfo| {
                            if (sinfo.asset_type == .scene) return sinfo.path;
                        }
                    } else |_| {}
                }
            } else |_| {}
        }
    }

    // Fallback: scene-01.json if present, else the first scene asset.
    var fallback: ?[]const u8 = null;
    var scenes = EditorState.asset_db.enumerate(.scene);
    while (scenes.next()) |sinfo| {
        if (std.mem.endsWith(u8, sinfo.path, "scene-01.json")) return sinfo.path;
        if (fallback == null) fallback = sinfo.path;
    }
    return fallback;
}

fn readFileArena(io: std.Io, arena: std.mem.Allocator, path: []const u8) ?[]u8 {
    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return null;
    defer file.close(io);
    var buf: [4096]u8 = undefined;
    var reader = file.reader(io, &buf);
    return reader.interface.allocRemaining(arena, .unlimited) catch null;
}

/// Resolve the GUID of the material a model mesh should use by default — the
/// material generated for the primitive's glTF material (falling back to the
/// model's first generated material). Returns null for non-model meshes or
/// models without generated materials. Used to auto-assign a MeshRenderer's
/// material when its mesh is set to a model.
pub fn modelPrimaryMaterial(io: std.Io, mesh_guid_str: []const u8, buf: *[36]u8) ?[]const u8 {
    if (mesh_guid_str.len == 0 or !State.assetDbReady()) return null;
    const guid = editor.Guid.parse(mesh_guid_str) catch return null;
    const info = EditorState.asset_db.findByGuid(guid) orelse return null;
    if (info.asset_type != .model) return null;

    const meta = editor.asset_meta.readMeta(io, std.heap.page_allocator, info.path);
    if (meta.sub_assets.len == 0) return null;

    // Prefer the material the first primitive references; else the first one.
    var chosen: ?editor.Guid = null;
    if (engine.assets.GltfLoader.firstMaterialIndex(info.path)) |idx| {
        var key_buf: [32]u8 = undefined;
        const key = std.fmt.bufPrint(&key_buf, "material:{d}", .{idx}) catch return null;
        for (meta.sub_assets) |s| {
            if (s.asset_type == .material and std.mem.eql(u8, s.key, key)) {
                chosen = s.guid;
                break;
            }
        }
    }
    if (chosen == null) {
        for (meta.sub_assets) |s| {
            if (s.asset_type == .material) {
                chosen = s.guid;
                break;
            }
        }
    }
    const g = chosen orelse return null;
    return g.toString(buf);
}

pub fn resolveObjectGuid(guid_str: []const u8) ?[]const u8 {
    if (guid_str.len == 0) return null;
    for (EditorState.objects[0..EditorState.object_count]) |*obj| {
        if (std.mem.eql(u8, obj.guidSlice(), guid_str)) return obj.nameSlice();
    }
    return null;
}

pub fn dragAssetGuidStr(buf: *[36]u8) ?[]const u8 {
    const ClipboardAndDrag = @import("ClipboardAndDrag.zig");
    const path = ClipboardAndDrag.dragAssetPath();
    if (path.len == 0 or !State.assetDbReady()) return null;
    if (EditorState.asset_db.findByPath(path)) |info| return info.guid.toString(buf);
    return null;
}

pub fn setProjectPath(path: []const u8) void {
    var trimmed = path;
    while (trimmed.len > 0 and (trimmed[trimmed.len - 1] == '/' or trimmed[trimmed.len - 1] == '\\')) {
        trimmed = trimmed[0 .. trimmed.len - 1];
    }
    const len = @min(trimmed.len, EditorState.project_path_buf.len);
    @memcpy(EditorState.project_path_buf[0..len], trimmed[0..len]);
    EditorState.project_path = EditorState.project_path_buf[0..len];
}

pub fn refreshComponents(io: std.Io, allocator: std.mem.Allocator) void {
    EditorState.asset_refresh_generation += 1;
    EditorState.discovered_count = 0;
    editor.scanner.populateBuiltins(&EditorState.discovered_components, &EditorState.discovered_count);
    EditorState.discovered_event_count = 0;

    if (EditorState.project_path) |p| {
        var path_buf: [1024]u8 = undefined;
        const assets = std.fmt.bufPrint(&path_buf, "{s}/assets", .{p}) catch return;
        editor.scanner.scanAssetsDir(io, allocator, assets, &EditorState.discovered_components, &EditorState.discovered_count);
        editor.event_scanner.scanEventNames(io, allocator, assets, &EditorState.discovered_events, &EditorState.discovered_event_count);

        if (EditorState.asset_db_initialized) EditorState.asset_db.deinit();
        EditorState.asset_db = editor.AssetDatabase.init(std.heap.page_allocator);
        EditorState.asset_db_initialized = true;
        EditorState.asset_db.scan(io, assets);

        editor.asset_importer.importAll(io, std.heap.page_allocator, p, &EditorState.asset_db, editor.Progress.none);

        // Compiling user script reflection spawns a `zig build` per source
        // file and can take seconds; run it in the background so opening or
        // hot-reloading a project doesn't freeze the editor. `finishReflect`
        // re-syncs the scene once the compiled field data lands.
        const ReflectJob = @import("ReflectJob.zig");
        ReflectJob.launchReflect(io);
    } else if (EditorState.object_count > 0) {
        // No project open: only builtins are known, and those need no
        // compiling, so the scene can be re-synced immediately.
        syncSceneWithDefinitions();
    }
}

pub fn makeComponent(def: *const ComponentDef) ?Component {
    if (def.is_builtin) {
        return Component.fromTypeName(def.typeName());
    }
    var c = Component{ .user_script = .{} };
    c.user_script.setTypeName(def.typeName());
    c.user_script.setSourceFile(def.sourceFile());
    for (def.fields[0..def.field_count]) |*fd| {
        var fv = engine.ScriptFieldValue{};
        fv.setName(fd.nameSlice());
        fv.kind = fd.kind;
        fv.asset_filter = fd.asset_filter;
        fv.as_f32 = fd.default_f32;
        fv.as_f64 = fd.default_f64;
        fv.as_i32 = fd.default_i32;
        fv.as_i64 = fd.default_i64;
        fv.as_u32 = fd.default_u32;
        fv.as_bool = fd.default_bool;
        fv.as_vec2_x = fd.default_vec2_x;
        fv.as_vec2_y = fd.default_vec2_y;
        fv.as_vec3_x = fd.default_vec3_x;
        fv.as_vec3_y = fd.default_vec3_y;
        fv.as_vec3_z = fd.default_vec3_z;
        fv.as_vec4_x = fd.default_vec4_x;
        fv.as_vec4_y = fd.default_vec4_y;
        fv.as_vec4_z = fd.default_vec4_z;
        fv.as_vec4_w = fd.default_vec4_w;
        c.user_script.addField(fv);
    }
    return c;
}

/// Merge each loaded user_script component's fields with the current component
/// definition. The result matches definition order exactly:
///   - Fields present in both: existing saved value is kept.
///   - Fields only in definition: inserted with default values.
///   - Fields only in scene (stale): dropped silently.
/// Call after loadScene and after refreshComponents (hot-reload) to keep the
/// inspector consistent with the source.
pub fn syncSceneWithDefinitions() void {
    for (EditorState.objects[0..EditorState.object_count]) |*obj| {
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .user_script) continue;
            const s = &comp.user_script;
            const type_name = s.typeName();

            const def = blk: {
                for (EditorState.discovered_components[0..EditorState.discovered_count]) |*d| {
                    if (std.mem.eql(u8, d.typeName(), type_name)) break :blk d;
                }
                break :blk null;
            } orelse continue;

            // Rebuild field list in definition order.
            var ordered: [s.field_values.len]engine.ScriptFieldValue = undefined;
            var ordered_count: usize = 0;

            for (def.fields[0..def.field_count]) |*fd| {
                if (ordered_count >= ordered.len) break;
                const fname = fd.nameSlice();

                // Prefer an existing value with matching name AND kind.
                const existing: ?engine.ScriptFieldValue = blk: {
                    for (s.field_values[0..s.field_count]) |*fv| {
                        if (fv.kind == fd.kind and std.mem.eql(u8, fv.nameSlice(), fname))
                            break :blk fv.*;
                    }
                    break :blk null;
                };

                if (existing) |ev| {
                    ordered[ordered_count] = ev;
                } else {
                    var fv = engine.ScriptFieldValue{};
                    fv.setName(fname);
                    fv.kind = fd.kind;
                    fv.as_f32 = fd.default_f32;
                    fv.as_f64 = fd.default_f64;
                    fv.as_i32 = fd.default_i32;
                    fv.as_i64 = fd.default_i64;
                    fv.as_u32 = fd.default_u32;
                    fv.as_bool = fd.default_bool;
                    fv.as_vec2_x = fd.default_vec2_x;
                    fv.as_vec2_y = fd.default_vec2_y;
                    fv.as_vec3_x = fd.default_vec3_x;
                    fv.as_vec3_y = fd.default_vec3_y;
                    fv.as_vec3_z = fd.default_vec3_z;
                    fv.as_vec4_x = fd.default_vec4_x;
                    fv.as_vec4_y = fd.default_vec4_y;
                    fv.as_vec4_z = fd.default_vec4_z;
                    fv.as_vec4_w = fd.default_vec4_w;
                    ordered[ordered_count] = fv;
                }
                // The filter is a static property of the script type (not
                // persisted), so always refresh it from the live definition.
                ordered[ordered_count].asset_filter = fd.asset_filter;
                ordered_count += 1;
            }

            @memcpy(s.field_values[0..ordered_count], ordered[0..ordered_count]);
            s.field_count = ordered_count;
        }
    }
}
