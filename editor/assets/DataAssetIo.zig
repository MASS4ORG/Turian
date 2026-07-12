/// Load, save, and default-construct DataAsset instance files (.asset JSON).
const std = @import("std");
const serde = @import("serde");
const engine = @import("engine");
const ComponentDef = @import("Scanner.zig").ComponentDef;
const SceneScriptField = @import("../types/SceneScriptField.zig").SceneScriptField;
pub const DataAssetFile = @import("../types/DataAssetFile.zig").DataAssetFile;

/// Load a `.asset` file from `path`. Returns an owned DataAssetFile; caller frees
/// via `serde.fromJson.free(allocator, DataAssetFile, result)`.
pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !DataAssetFile {
    var file = try std.Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const data = try reader.interface.allocRemaining(allocator, .unlimited);
    defer allocator.free(data);

    return serde.json.fromSlice(DataAssetFile, allocator, data);
}

/// Write a DataAssetFile to `path` as pretty-printed JSON.
pub fn save(io: std.Io, path: []const u8, file: DataAssetFile) !void {
    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const json = try serde.json.toSliceWith(arena, file, .{ .pretty = true });
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = json });
}

/// Build a default DataAssetFile from a ComponentDef, populating field values
/// from FieldDef defaults. Mirrors EditorState.makeComponent for data assets.
pub fn defaultFromDef(def: *const ComponentDef) DataAssetFile {
    return .{
        .version = 1,
        .type_name = def.typeName(),
        .source_file = def.sourceFile(),
        .fields = &.{},
    };
}

/// Merge stored field values onto the current def schema — same algorithm as
/// EditorState.syncSceneWithDefinitions. Returns the field count written into `out`.
/// `out` must have at least def.field_count capacity.
pub fn mergeFields(
    def: *const ComponentDef,
    stored: []const SceneScriptField,
    out: []SceneScriptField,
) usize {
    var ordered_count: usize = 0;
    for (def.fields[0..def.field_count]) |*fd| {
        if (ordered_count >= out.len) break;
        const fname = fd.nameSlice();

        const existing: ?SceneScriptField = blk: {
            for (stored) |*sf| {
                if (sf.kind == fd.kind and std.mem.eql(u8, sf.name, fname))
                    break :blk sf.*;
            }
            break :blk null;
        };

        if (existing) |ev| {
            out[ordered_count] = ev;
        } else {
            out[ordered_count] = .{
                .name = fname,
                .kind = fd.kind,
                .as_f32 = fd.default_f32,
                .as_f64 = fd.default_f64,
                .as_i32 = fd.default_i32,
                .as_i64 = fd.default_i64,
                .as_u32 = fd.default_u32,
                .as_bool = fd.default_bool,
                .as_vec2_x = fd.default_vec2_x,
                .as_vec2_y = fd.default_vec2_y,
                .as_vec3_x = fd.default_vec3_x,
                .as_vec3_y = fd.default_vec3_y,
                .as_vec3_z = fd.default_vec3_z,
                .as_vec4_x = fd.default_vec4_x,
                .as_vec4_y = fd.default_vec4_y,
                .as_vec4_z = fd.default_vec4_z,
                .as_vec4_w = fd.default_vec4_w,
            };
        }
        ordered_count += 1;
    }
    return ordered_count;
}

test "defaultFromDef produces correct type_name and source_file" {
    var def = ComponentDef{};
    def.setTypeName("EnemyStats");
    def.setSourceFile("assets/EnemyStats.zig");

    const f = defaultFromDef(&def);
    try std.testing.expectEqual(@as(u32, 1), f.version);
    try std.testing.expectEqualStrings("EnemyStats", f.type_name);
    try std.testing.expectEqualStrings("assets/EnemyStats.zig", f.source_file);
    try std.testing.expectEqual(@as(usize, 0), f.fields.len);
}

test "mergeFields prefers stored values and fills defaults for new fields" {
    const FieldDef = @import("../types/FieldDef.zig").FieldDef;

    var def = ComponentDef{};
    def.setTypeName("Stats");
    def.setSourceFile("assets/Stats.zig");

    // Two fields: "health" (f32, default 100) and "damage" (i32, default 10).
    {
        var fd = &def.fields[0];
        fd.* = .{};
        fd.setName("health");
        fd.kind = .f32;
        fd.default_f32 = 100;
    }
    {
        var fd = &def.fields[1];
        fd.* = .{};
        fd.setName("damage");
        fd.kind = .i32;
        fd.default_i32 = 10;
    }
    def.field_count = 2;

    // Stored has "health" with a custom value; "damage" is absent (new field).
    const stored = [_]SceneScriptField{.{
        .name = "health",
        .kind = .f32,
        .as_f32 = 50,
    }};

    var out: [16]SceneScriptField = undefined;
    const n = mergeFields(&def, &stored, &out);

    try std.testing.expectEqual(@as(usize, 2), n);
    try std.testing.expectEqualStrings("health", out[0].name);
    try std.testing.expectEqual(@as(f32, 50), out[0].as_f32);
    try std.testing.expectEqualStrings("damage", out[1].name);
    try std.testing.expectEqual(@as(i32, 10), out[1].as_i32);
    _ = FieldDef{};
}
