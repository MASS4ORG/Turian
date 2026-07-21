const std = @import("std");
const engine = @import("engine");
const serde = @import("serde");
const Guid = @import("guid").Guid;
const SceneScriptField = @import("../types/SceneScriptField.zig").SceneScriptField;
const SceneUserScript = @import("../types/SceneUserScript.zig").SceneUserScript;
const SceneMeshRenderer = @import("../types/SceneMeshRenderer.zig").SceneMeshRenderer;
const SceneComponent = @import("../types/SceneComponent.zig").SceneComponent;
const SceneObject = @import("../types/SceneObject.zig").SceneObject;
const SceneFile = @import("../types/SceneFile.zig").SceneFile;

/// Current scene format version written by `serializeScene`.
pub const CURRENT_VERSION = @import("../types/SceneFile.zig").CURRENT_VERSION;

const log = std.log.scoped(.scene_io);

fn engineCompToScene(c: *const engine.Component, material_guids: []const []const u8) SceneComponent {
    return switch (c.*) {
        .camera => |v| .{ .camera = v },
        .light => |v| .{ .light = v },
        .mesh_renderer => |*v| .{ .mesh_renderer = .{
            .cast_shadows = v.cast_shadows,
            .receive_shadows = v.receive_shadows,
            .mesh_guid = v.mesh.slice(),
            .material_guids = material_guids,
        } },
        .rigid_body => |v| .{ .rigid_body = v },
        .collider => |v| .{ .collider = v },
        .audio_source => |v| .{ .audio_source = v },
        .animator => |v| .{ .animator = v },
        .ui_document => |*v| .{ .ui_document = .{
            .document_guid = v.document.slice(),
        } },
        .user_script => |*s| .{ .user_script = .{
            .type_name = s.typeName(),
            .source_file = s.sourceFile(),
        } },
    };
}

fn sceneCompToEngine(sc: SceneComponent) engine.Component {
    return switch (sc) {
        .camera => |v| .{ .camera = v },
        .light => |v| .{ .light = v },
        .mesh_renderer => |v| blk: {
            var mr = engine.MeshRendererComponent{
                .cast_shadows = v.cast_shadows,
                .receive_shadows = v.receive_shadows,
            };
            mr.mesh.set(v.mesh_guid);
            if (v.material_guids.len > 0) {
                const n = @min(v.material_guids.len, engine.MeshRendererComponent.MAX_MATERIALS);
                for (0..n) |i| mr.materials[i].set(v.material_guids[i]);
                mr.material_count = @intCast(n);
            } else if (v.material_guid.len > 0) {
                log.warn("mesh_renderer uses deprecated \"material_guid\" — migrated to \"material_guids\" in memory; re-save the scene to persist", .{});
                mr.materials[0].set(v.material_guid);
                mr.material_count = 1;
            }
            break :blk .{ .mesh_renderer = mr };
        },
        .rigid_body => |v| .{ .rigid_body = v },
        .collider => |v| .{ .collider = v },
        .audio_source => |v| .{ .audio_source = v },
        .animator => |v| .{ .animator = v },
        .ui_document => |v| blk: {
            var ud = engine.UiDocumentComponent{};
            ud.document.set(v.document_guid);
            break :blk .{ .ui_document = ud };
        },
        .user_script => |s| blk: {
            var ref = engine.UserScriptRef{};
            ref.setTypeName(s.type_name);
            ref.setSourceFile(s.source_file);
            for (s.fields) |sf| {
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
                ref.addField(fv);
            }
            break :blk .{ .user_script = ref };
        },
    };
}

/// Serialize a scene to JSON bytes owned by `allocator` (caller frees).
/// Returns null on any allocation/serialization failure. This is the in-memory
/// half of `saveScene`, reused by Play mode to snapshot the live scene without
/// touching the filesystem.
pub fn serializeScene(
    allocator: std.mem.Allocator,
    objects: []const engine.SceneNode,
    count: usize,
) ?[]u8 {
    const scene_objects = allocator.alloc(SceneObject, count) catch return null;
    defer allocator.free(scene_objects);

    var total_comps: usize = 0;
    var total_script_fields: usize = 0;
    var total_material_refs: usize = 0;
    var total_overrides: usize = 0;
    for (objects[0..count]) |*obj| {
        total_comps += obj.component_count;
        total_overrides += obj.override_count;
        for (obj.components[0..obj.component_count]) |*c| {
            if (c.* == .user_script) total_script_fields += c.user_script.field_count;
            if (c.* == .mesh_renderer) total_material_refs += @min(c.mesh_renderer.material_count, engine.MeshRendererComponent.MAX_MATERIALS);
        }
    }

    const all_comps = allocator.alloc(SceneComponent, total_comps) catch return null;
    defer allocator.free(all_comps);
    const all_script_fields = allocator.alloc(SceneScriptField, total_script_fields) catch return null;
    defer allocator.free(all_script_fields);
    const all_material_guids = allocator.alloc([]const u8, total_material_refs) catch return null;
    defer allocator.free(all_material_guids);
    // Override group keys point straight into the node buffers (objects outlives us).
    const all_overrides = allocator.alloc([]const u8, total_overrides) catch return null;
    defer allocator.free(all_overrides);
    var ovr_offset: usize = 0;

    var comp_offset: usize = 0;
    var sf_offset: usize = 0;
    var mg_offset: usize = 0;
    for (objects[0..count], 0..) |*obj, i| {
        const t = obj.transform;
        const cc = obj.component_count;
        const comp_slice = all_comps[comp_offset .. comp_offset + cc];
        for (obj.components[0..cc], 0..) |*c, ci| {
            if (c.* == .user_script) {
                const s = &c.user_script;
                const fc = s.field_count;
                const sf_slice = all_script_fields[sf_offset .. sf_offset + fc];
                for (s.field_values[0..fc], 0..) |*fv, fi| {
                    sf_slice[fi] = .{
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
                sf_offset += fc;
                comp_slice[ci] = .{ .user_script = .{
                    .type_name = s.typeName(),
                    .source_file = s.sourceFile(),
                    .fields = sf_slice,
                } };
            } else if (c.* == .mesh_renderer) {
                const mr = &c.mesh_renderer;
                const n = @min(mr.material_count, engine.MeshRendererComponent.MAX_MATERIALS);
                const mg_slice = all_material_guids[mg_offset .. mg_offset + n];
                for (0..n) |mi| mg_slice[mi] = mr.materials[mi].slice();
                mg_offset += n;
                comp_slice[ci] = engineCompToScene(c, mg_slice);
            } else {
                comp_slice[ci] = engineCompToScene(c, &.{});
            }
        }
        comp_offset += cc;

        const ovr_slice = all_overrides[ovr_offset .. ovr_offset + obj.override_count];
        for (0..obj.override_count) |oi| {
            ovr_slice[oi] = obj.overrides[oi][0..obj.override_lens[oi]];
        }
        ovr_offset += obj.override_count;

        scene_objects[i] = .{
            .name = obj.nameSlice(),
            .guid = obj.guidSlice(),
            .parent = obj.parent,
            .active = obj.active,
            .transform = .{
                .position = .{ .x = t.position.x, .y = t.position.y, .z = t.position.z },
                .rotation = .{ .x = t.rotation.x, .y = t.rotation.y, .z = t.rotation.z },
                .scale = .{ .x = t.scale.x, .y = t.scale.y, .z = t.scale.z },
            },
            .components = comp_slice,
            .prefab_source = obj.prefabSourceSlice(),
            .prefab_node = obj.prefabNodeSlice(),
            .overrides = ovr_slice,
        };
    }

    const scene_data = SceneFile{ .version = @import("../types/SceneFile.zig").CURRENT_VERSION, .objects = scene_objects };
    return serde.json.toSliceWith(allocator, scene_data, .{ .pretty = true }) catch null;
}

/// Save the current scene to a .json file.
pub fn saveScene(
    io: std.Io,
    path: []const u8,
    objects: []const engine.SceneNode,
    count: usize,
    allocator: std.mem.Allocator,
) void {
    const content = serializeScene(allocator, objects, count) orelse return;
    defer allocator.free(content);
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = content }) catch {};
}

/// Load a scene from a .json file. Returns false on failure.
pub fn loadScene(
    io: std.Io,
    allocator: std.mem.Allocator,
    path: []const u8,
    out_objects: []engine.SceneNode,
    out_count: *usize,
) bool {
    out_count.* = 0;

    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return false;
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var file_reader = file.reader(io, &fbuf);
    const content = file_reader.interface.allocRemainingAlignedSentinel(allocator, .unlimited, .@"1", 0) catch return false;
    defer allocator.free(content);

    return loadSceneFromBytes(allocator, content, out_objects, out_count);
}

/// Read just the `version` field from raw scene JSON without a full parse.
/// Returns 1 (the pre-versioned default) when the field is absent or malformed.
/// Used to decide whether a scene needs material-slot migration before saving.
pub fn parseSceneVersion(content: []const u8) u32 {
    const key = "\"version\":";
    const at = std.mem.indexOf(u8, content, key) orelse return 1;
    var i = at + key.len;
    while (i < content.len and (content[i] == ' ' or content[i] == '\t')) i += 1;
    var end = i;
    while (end < content.len and content[end] >= '0' and content[end] <= '9') end += 1;
    return std.fmt.parseInt(u32, content[i..end], 10) catch 1;
}

/// Parse a scene from in-memory JSON bytes (e.g. supplied by an asset package
/// instead of a loose file).
pub fn loadSceneFromBytes(
    allocator: std.mem.Allocator,
    content: []const u8,
    out_objects: []engine.SceneNode,
    out_count: *usize,
) bool {
    out_count.* = 0;

    var arena = std.heap.ArenaAllocator.init(allocator);
    defer arena.deinit();

    const parsed = serde.json.fromSlice(SceneFile, arena.allocator(), content) catch return false;

    const max = @min(parsed.objects.len, out_objects.len);
    for (parsed.objects[0..max], 0..) |so, i| {
        var obj = engine.SceneNode{};
        obj.setName(so.name);
        obj.setGuidStr(so.guid);
        obj.parent = so.parent;
        obj.active = so.active;
        obj.transform = .{
            .position = .{ .x = so.transform.position.x, .y = so.transform.position.y, .z = so.transform.position.z },
            .rotation = .{ .x = so.transform.rotation.x, .y = so.transform.rotation.y, .z = so.transform.rotation.z },
            .scale = .{ .x = so.transform.scale.x, .y = so.transform.scale.y, .z = so.transform.scale.z },
        };
        for (so.components) |sc| {
            _ = obj.addComponent(sceneCompToEngine(sc));
        }
        obj.setPrefabSource(so.prefab_source);
        obj.setPrefabNode(so.prefab_node);
        for (so.overrides) |key| obj.addOverrideKey(key);
        out_objects[i] = obj;
    }
    out_count.* = max;
    return true;
}
