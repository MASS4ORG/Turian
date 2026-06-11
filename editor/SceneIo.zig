const std = @import("std");
const engine = @import("engine");
const serde = @import("serde");
const Guid = @import("guid").Guid;
const SceneScriptField = @import("types/SceneScriptField.zig").SceneScriptField;
const SceneUserScript = @import("types/SceneUserScript.zig").SceneUserScript;
const SceneMeshRenderer = @import("types/SceneMeshRenderer.zig").SceneMeshRenderer;
const SceneComponent = @import("types/SceneComponent.zig").SceneComponent;
const SceneObject = @import("types/SceneObject.zig").SceneObject;
const SceneFile = @import("types/SceneFile.zig").SceneFile;

fn engineCompToScene(c: *const engine.Component) SceneComponent {
    return switch (c.*) {
        .camera => |v| .{ .camera = v },
        .light => |v| .{ .light = v },
        .mesh_renderer => |*v| .{ .mesh_renderer = .{
            .cast_shadows = v.cast_shadows,
            .receive_shadows = v.receive_shadows,
            .mesh_guid = v.mesh.slice(),
            .material_guid = v.material.slice(),
        } },
        .rigid_body => |v| .{ .rigid_body = v },
        .collider => |v| .{ .collider = v },
        .audio_source => |v| .{ .audio_source = v },
        .animator => |v| .{ .animator = v },
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
            mr.material.set(v.material_guid);
            break :blk .{ .mesh_renderer = mr };
        },
        .rigid_body => |v| .{ .rigid_body = v },
        .collider => |v| .{ .collider = v },
        .audio_source => |v| .{ .audio_source = v },
        .animator => |v| .{ .animator = v },
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

/// Save the current scene to a .json file.
pub fn saveScene(
    io: std.Io,
    path: []const u8,
    objects: []const engine.SceneNode,
    count: usize,
    allocator: std.mem.Allocator,
) void {
    const scene_objects = allocator.alloc(SceneObject, count) catch return;
    defer allocator.free(scene_objects);

    var total_comps: usize = 0;
    var total_script_fields: usize = 0;
    for (objects[0..count]) |*obj| {
        total_comps += obj.component_count;
        for (obj.components[0..obj.component_count]) |*c| {
            if (c.* == .user_script) total_script_fields += c.user_script.field_count;
        }
    }

    const all_comps = allocator.alloc(SceneComponent, total_comps) catch return;
    defer allocator.free(all_comps);
    const all_script_fields = allocator.alloc(SceneScriptField, total_script_fields) catch return;
    defer allocator.free(all_script_fields);

    var comp_offset: usize = 0;
    var sf_offset: usize = 0;
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
            } else {
                comp_slice[ci] = engineCompToScene(c);
            }
        }
        comp_offset += cc;

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
        };
    }

    const scene_data = SceneFile{ .version = 1, .objects = scene_objects };

    const content = serde.json.toSliceWith(allocator, scene_data, .{ .pretty = true }) catch return;
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
        out_objects[i] = obj;
    }
    out_count.* = max;
    return true;
}
