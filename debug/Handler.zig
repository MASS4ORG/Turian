//! JSON-RPC method dispatch for the Remote Debug Protocol.
//!
//! Each method receives the raw `params` JSON, accesses the world through the
//! provided `World`, writes its result (or error) into `out`, and returns.
//! All methods are synchronous and allocation-free where possible; a small
//! arena is created per-call for intermediate JSON work.

const std = @import("std");
const engine = @import("engine");
const Protocol = @import("Protocol.zig");
const Server = @import("Server.zig");
const introspect = engine.introspect;
const SceneNode = engine.SceneNode;

const Request = Protocol.Request;
const World = introspect.World;
const Stringify = std.json.Stringify;

// ── Method registry ──────────────────────────────────────────────────────────

/// Dispatch a JSON-RPC request and write the response into `out`.
/// `world` is the current engine state snapshot.
pub fn dispatch(
    allocator: std.mem.Allocator,
    req: *const Request,
    world: World,
    out: *std.Io.Writer,
) void {
    const m = req.method();

    if (std.mem.eql(u8, m, "scene.list")) return callSceneList(allocator, req, world, out);
    if (std.mem.eql(u8, m, "scene.inspect")) return callSceneInspect(allocator, req, world, out);
    if (std.mem.eql(u8, m, "entity.find")) return callEntityFind(allocator, req, world, out);
    if (std.mem.eql(u8, m, "entity.inspect")) return callEntityInspect(allocator, req, world, out);
    if (std.mem.eql(u8, m, "component.get")) return callComponentGet(allocator, req, world, out);
    if (std.mem.eql(u8, m, "snapshot")) return callSnapshot(allocator, req, world, out);
    if (std.mem.eql(u8, m, "asset.list")) return callAssetList(allocator, req, world, out);
    if (std.mem.eql(u8, m, "asset.inspect")) return callAssetInspect(allocator, req, world, out);
    if (std.mem.eql(u8, m, "schema")) return callSchema(allocator, req, out);
    if (std.mem.eql(u8, m, "metrics")) return callMetrics(allocator, req, world, out);
    if (std.mem.eql(u8, m, "profiler.capture")) return callProfilerCapture(allocator, req, out);
    if (std.mem.eql(u8, m, "memory")) return callMemory(allocator, req, world, out);
    if (std.mem.eql(u8, m, "errors")) return callErrors(allocator, req, out);
    if (std.mem.eql(u8, m, "ping")) return callPing(req, out);

    errResponse(req, out, Protocol.ErrorCode.METHOD_NOT_FOUND, "Method not found");
}

// ── Helpers ──────────────────────────────────────────────────────────────────

fn errResponse(req: *const Request, out: *std.Io.Writer, code: i32, msg: []const u8) void {
    Protocol.writeError(out, req, code, msg) catch {};
}

fn okJson(req: *const Request, out: *std.Io.Writer, json: []const u8) void {
    Protocol.writeSuccess(out, req, json) catch {};
}

fn withResult(
    allocator: std.mem.Allocator,
    req: *const Request,
    out: *std.Io.Writer,
    writeFn: fn (*Stringify, World) anyerror!void,
    world: World,
) void {
    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
    writeFn(&jw, world) catch {
        errResponse(req, out, Protocol.ErrorCode.INTERNAL_ERROR, "Serialisation error");
        return;
    };
    const json = allocator.dupe(u8, buf.written()) catch {
        errResponse(req, out, Protocol.ErrorCode.INTERNAL_ERROR, "Out of memory");
        return;
    };
    defer allocator.free(json);
    okJson(req, out, json);
}

fn getParamString(params_json: []const u8, key: []const u8, dst: []u8) ?[]u8 {
    if (params_json.len == 0) return null;
    const parsed = std.json.parseFromSlice(std.json.Value, std.heap.page_allocator, params_json, .{}) catch return null;
    defer parsed.deinit();
    if (parsed.value != .object) return null;
    const val = parsed.value.object.get(key) orelse return null;
    if (val != .string) return null;
    const len = @min(val.string.len, dst.len);
    @memcpy(dst[0..len], val.string[0..len]);
    return dst[0..len];
}

fn getParamInt(params_json: []const u8, key: []const u8) ?i64 {
    if (params_json.len == 0) return null;
    const parsed = std.json.parseFromSlice(std.json.Value, std.heap.page_allocator, params_json, .{}) catch return null;
    defer parsed.deinit();
    if (parsed.value != .object) return null;
    const val = parsed.value.object.get(key) orelse return null;
    return switch (val) {
        .integer => |n| n,
        else => null,
    };
}

fn findScene(world: World, name: []const u8) ?introspect.SceneView {
    for (world.scenes) |v| {
        if (std.mem.eql(u8, v.name, name) or std.mem.eql(u8, v.id, name)) return v;
    }
    return null;
}

fn activeScene(world: World) ?introspect.SceneView {
    for (world.scenes) |v| if (v.active) return v;
    if (world.scenes.len > 0) return world.scenes[0];
    return null;
}

// ── Method implementations ───────────────────────────────────────────────────

fn callPing(req: *const Request, out: *std.Io.Writer) void {
    okJson(req, out, "\"pong\"");
}

fn sceneListWriter(jw: *Stringify, world: World) anyerror!void {
    try introspect.writeSceneList(jw, world);
}

fn callSceneList(allocator: std.mem.Allocator, req: *const Request, world: World, out: *std.Io.Writer) void {
    withResult(allocator, req, out, sceneListWriter, world);
}

fn callSceneInspect(allocator: std.mem.Allocator, req: *const Request, world: World, out: *std.Io.Writer) void {
    var name_buf: [256]u8 = undefined;
    const view = blk: {
        if (getParamString(req.params(), "name", &name_buf)) |n|
            break :blk findScene(world, n) orelse {
                errResponse(req, out, Protocol.ErrorCode.NOT_FOUND, "Scene not found");
                return;
            }
        else
            break :blk activeScene(world) orelse {
                errResponse(req, out, Protocol.ErrorCode.NOT_FOUND, "No scenes loaded");
                return;
            };
    };

    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
    introspect.writeScene(&jw, view, true) catch {
        errResponse(req, out, Protocol.ErrorCode.INTERNAL_ERROR, "Serialisation error");
        return;
    };
    const json = allocator.dupe(u8, buf.written()) catch return;
    defer allocator.free(json);
    okJson(req, out, json);
}

fn callEntityFind(allocator: std.mem.Allocator, req: *const Request, world: World, out: *std.Io.Writer) void {
    const scene_view = activeScene(world) orelse {
        errResponse(req, out, Protocol.ErrorCode.NOT_FOUND, "No scenes loaded");
        return;
    };

    var name_buf: [256]u8 = undefined;
    var comp_buf: [128]u8 = undefined;

    var indices: [128]usize = undefined;
    var count: usize = 0;

    if (getParamString(req.params(), "name", &name_buf)) |n| {
        count = introspect.findByName(scene_view.nodes, n, &indices);
    } else if (getParamString(req.params(), "component", &comp_buf)) |c| {
        count = introspect.findByComponent(scene_view.nodes, c, &indices);
    } else {
        count = @min(scene_view.nodes.len, indices.len);
        for (0..count) |i| indices[i] = i;
    }

    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
    jw.beginArray() catch return;
    for (indices[0..count]) |idx| {
        introspect.writeEntitySummary(&jw, &scene_view.nodes[idx], idx) catch return;
    }
    jw.endArray() catch return;
    const json = allocator.dupe(u8, buf.written()) catch return;
    defer allocator.free(json);
    okJson(req, out, json);
}

fn callEntityInspect(allocator: std.mem.Allocator, req: *const Request, world: World, out: *std.Io.Writer) void {
    const scene_view = activeScene(world) orelse {
        errResponse(req, out, Protocol.ErrorCode.NOT_FOUND, "No scenes loaded");
        return;
    };

    const node: *const SceneNode = blk: {
        var name_buf: [256]u8 = undefined;
        if (getParamString(req.params(), "name", &name_buf)) |n| {
            var indices: [1]usize = undefined;
            if (introspect.findByName(scene_view.nodes, n, &indices) == 0) {
                errResponse(req, out, Protocol.ErrorCode.NOT_FOUND, "Entity not found");
                return;
            }
            break :blk &scene_view.nodes[indices[0]];
        } else if (getParamInt(req.params(), "index")) |idx| {
            if (idx < 0 or @as(usize, @intCast(idx)) >= scene_view.nodes.len) {
                errResponse(req, out, Protocol.ErrorCode.NOT_FOUND, "Index out of range");
                return;
            }
            break :blk &scene_view.nodes[@as(usize, @intCast(idx))];
        } else {
            errResponse(req, out, Protocol.ErrorCode.INVALID_PARAMS, "Provide 'name' or 'index'");
            return;
        }
    };

    const index: usize = @as(usize, @intFromPtr(node) - @intFromPtr(scene_view.nodes.ptr)) / @sizeOf(SceneNode);

    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
    introspect.writeEntityDetail(&jw, node, index) catch {
        errResponse(req, out, Protocol.ErrorCode.INTERNAL_ERROR, "Serialisation error");
        return;
    };
    const json = allocator.dupe(u8, buf.written()) catch return;
    defer allocator.free(json);
    okJson(req, out, json);
}

fn callComponentGet(allocator: std.mem.Allocator, req: *const Request, world: World, out: *std.Io.Writer) void {
    const scene_view = activeScene(world) orelse {
        errResponse(req, out, Protocol.ErrorCode.NOT_FOUND, "No scenes loaded");
        return;
    };

    var name_buf: [256]u8 = undefined;
    var comp_buf: [128]u8 = undefined;

    const entity_name = getParamString(req.params(), "entity", &name_buf) orelse {
        errResponse(req, out, Protocol.ErrorCode.INVALID_PARAMS, "Missing 'entity'");
        return;
    };
    const comp_name = getParamString(req.params(), "component", &comp_buf) orelse {
        errResponse(req, out, Protocol.ErrorCode.INVALID_PARAMS, "Missing 'component'");
        return;
    };

    var indices: [1]usize = undefined;
    if (introspect.findByName(scene_view.nodes, entity_name, &indices) == 0) {
        errResponse(req, out, Protocol.ErrorCode.NOT_FOUND, "Entity not found");
        return;
    }
    const node = &scene_view.nodes[indices[0]];
    const ci = introspect.componentIndex(node, comp_name) orelse {
        errResponse(req, out, Protocol.ErrorCode.NOT_FOUND, "Component not found on entity");
        return;
    };

    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
    introspect.writeComponent(&jw, &node.components[ci]) catch {
        errResponse(req, out, Protocol.ErrorCode.INTERNAL_ERROR, "Serialisation error");
        return;
    };
    const json = allocator.dupe(u8, buf.written()) catch return;
    defer allocator.free(json);
    okJson(req, out, json);
}

fn callProfilerCapture(allocator: std.mem.Allocator, req: *const Request, out: *std.Io.Writer) void {
    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
    engine.Profiler.writeFrameJson(&jw) catch {
        errResponse(req, out, Protocol.ErrorCode.INTERNAL_ERROR, "Serialisation error");
        return;
    };
    const json = allocator.dupe(u8, buf.written()) catch return;
    defer allocator.free(json);
    okJson(req, out, json);
}

fn callMemory(allocator: std.mem.Allocator, req: *const Request, world: World, out: *std.Io.Writer) void {
    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
    jw.beginObject() catch return;
    jw.objectField("memory_bytes") catch return;
    jw.write(if (world.metrics) |m| m.memory_bytes else 0) catch return;
    jw.objectField("allocation_count") catch return;
    jw.write(if (world.metrics) |m| m.allocation_count else 0) catch return;
    jw.endObject() catch return;
    const json = allocator.dupe(u8, buf.written()) catch return;
    defer allocator.free(json);
    okJson(req, out, json);
}

fn callErrors(allocator: std.mem.Allocator, req: *const Request, out: *std.Io.Writer) void {
    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
    engine.DiagLog.writeJson(&jw) catch {
        errResponse(req, out, Protocol.ErrorCode.INTERNAL_ERROR, "Serialisation error");
        return;
    };
    const json = allocator.dupe(u8, buf.written()) catch return;
    defer allocator.free(json);
    okJson(req, out, json);
}

// ── Mutating methods (validated here, applied by the host MutationApplier) ────

/// Wire names of the methods that mutate live engine state. The server routes
/// these through `buildMutation` + the host applier instead of `dispatch`.
const mutating_methods = [_][]const u8{
    "component.set",
    "transform.set",
    "entity.spawn",
    "entity.destroy",
    "asset.reload",
};

/// True if `method` is a mutating method (handled via the applier, not dispatch).
pub fn isMutation(method: []const u8) bool {
    for (mutating_methods) |mm| {
        if (std.mem.eql(u8, method, mm)) return true;
    }
    return false;
}

const MutationError = error{ InvalidParams, OutOfMemory };

/// Parses and validates a mutating request into a `Server.Mutation`. Every
/// borrowed string is duped into `arena`, so the result is valid for as long as
/// `arena` lives (the caller frees it after the applier returns).
pub fn buildMutation(arena: std.mem.Allocator, req: *const Request) MutationError!Server.Mutation {
    const parsed = std.json.parseFromSlice(std.json.Value, arena, req.params(), .{}) catch
        return error.InvalidParams;
    defer parsed.deinit();
    const obj = if (parsed.value == .object) parsed.value.object else return error.InvalidParams;
    const m = req.method();

    if (std.mem.eql(u8, m, "component.set")) {
        const value = try jsonToValue(arena, obj.get("value") orelse return error.InvalidParams);
        return .{ .set_component = .{
            .entity = try dupField(arena, obj, "entity"),
            .component = try dupField(arena, obj, "component"),
            .field = try dupField(arena, obj, "field"),
            .value = value,
        } };
    }
    if (std.mem.eql(u8, m, "transform.set")) {
        return .{ .set_transform = .{
            .channel = try dupField(arena, obj, "channel"),
            .value = try jsonToVec3(obj.get("value") orelse return error.InvalidParams),
            .entity = try dupField(arena, obj, "entity"),
        } };
    }
    if (std.mem.eql(u8, m, "entity.spawn")) {
        return .{ .spawn = .{ .name = try dupField(arena, obj, "name") } };
    }
    if (std.mem.eql(u8, m, "entity.destroy")) {
        return .{ .destroy = .{ .entity = try dupField(arena, obj, "entity") } };
    }
    if (std.mem.eql(u8, m, "asset.reload")) {
        return .{ .reload_asset = .{ .guid = try dupField(arena, obj, "guid") } };
    }
    return error.InvalidParams;
}

fn dupField(arena: std.mem.Allocator, obj: std.json.ObjectMap, key: []const u8) MutationError![]const u8 {
    const v = obj.get(key) orelse return error.InvalidParams;
    if (v != .string) return error.InvalidParams;
    return try arena.dupe(u8, v.string);
}

/// Maps a JSON value to an `introspect.Value`. Numbers → number, bools →
/// boolean, strings → text (duped), 3-element numeric arrays → vec3.
fn jsonToValue(arena: std.mem.Allocator, v: std.json.Value) MutationError!introspect.Value {
    return switch (v) {
        .integer => |n| .{ .number = @floatFromInt(n) },
        .float => |f| .{ .number = f },
        .number_string => |s| .{ .number = std.fmt.parseFloat(f64, s) catch return error.InvalidParams },
        .bool => |b| .{ .boolean = b },
        .string => |s| .{ .text = try arena.dupe(u8, s) },
        .array => .{ .vec3 = try jsonToVec3(v) },
        else => error.InvalidParams,
    };
}

fn jsonToVec3(v: std.json.Value) MutationError![3]f32 {
    if (v != .array or v.array.items.len != 3) return error.InvalidParams;
    var out: [3]f32 = undefined;
    for (v.array.items, 0..) |item, i| {
        out[i] = switch (item) {
            .integer => |n| @floatFromInt(n),
            .float => |f| @floatCast(f),
            .number_string => |s| std.fmt.parseFloat(f32, s) catch return error.InvalidParams,
            else => return error.InvalidParams,
        };
    }
    return out;
}

fn assetListWriter(jw: *Stringify, world: World) anyerror!void {
    try introspect.writeAssetList(jw, world);
}

fn callAssetList(allocator: std.mem.Allocator, req: *const Request, world: World, out: *std.Io.Writer) void {
    withResult(allocator, req, out, assetListWriter, world);
}

fn callAssetInspect(allocator: std.mem.Allocator, req: *const Request, world: World, out: *std.Io.Writer) void {
    var guid_buf: [128]u8 = undefined;
    const guid = getParamString(req.params(), "guid", &guid_buf) orelse {
        errResponse(req, out, Protocol.ErrorCode.INVALID_PARAMS, "Missing 'guid'");
        return;
    };
    const view = for (world.assets) |a| {
        if (std.mem.eql(u8, a.guid, guid)) break a;
    } else {
        errResponse(req, out, Protocol.ErrorCode.NOT_FOUND, "Asset not found");
        return;
    };

    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
    introspect.writeAsset(&jw, view) catch {
        errResponse(req, out, Protocol.ErrorCode.INTERNAL_ERROR, "Serialisation error");
        return;
    };
    const json = allocator.dupe(u8, buf.written()) catch return;
    defer allocator.free(json);
    okJson(req, out, json);
}

fn snapshotWriter(jw: *Stringify, world: World) anyerror!void {
    try introspect.writeSnapshot(jw, world);
}

fn callSnapshot(allocator: std.mem.Allocator, req: *const Request, world: World, out: *std.Io.Writer) void {
    withResult(allocator, req, out, snapshotWriter, world);
}

fn schemaWriter(jw: *Stringify, _: World) anyerror!void {
    try introspect.writeSchema(jw);
}

fn callSchema(allocator: std.mem.Allocator, req: *const Request, out: *std.Io.Writer) void {
    withResult(allocator, req, out, schemaWriter, .{});
}

fn callMetrics(allocator: std.mem.Allocator, req: *const Request, world: World, out: *std.Io.Writer) void {
    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
    if (world.metrics) |m|
        jw.write(m.*) catch {}
    else
        jw.write(null) catch {};
    const json = allocator.dupe(u8, buf.written()) catch return;
    defer allocator.free(json);
    okJson(req, out, json);
}

// ── Tests ────────────────────────────────────────────────────────────────────

const testing = std.testing;

fn makeNode(name: []const u8) SceneNode {
    var n = SceneNode{};
    n.setName(name);
    return n;
}

fn dispatchStr(allocator: std.mem.Allocator, line: []const u8, world: World) ![]u8 {
    const req = try Request.parse(line);
    var out: std.Io.Writer.Allocating = .init(allocator);
    defer out.deinit();
    dispatch(allocator, &req, world, &out.writer);
    return allocator.dupe(u8, out.written());
}

test "ping returns pong" {
    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":1,"method":"ping"}
    , .{});
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "\"pong\"") != null);
}

test "scene.list returns empty array when no scenes" {
    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":1,"method":"scene.list"}
    , .{});
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "\"result\":[]") != null);
}

test "entity.inspect by name returns entity detail" {
    var nodes = [_]SceneNode{makeNode("Player")};
    _ = nodes[0].addComponent(.{ .light = engine.LightComponent{} });
    const views = [_]introspect.SceneView{.{ .name = "Main", .active = true, .nodes = &nodes }};
    const world = World{ .scenes = &views };

    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":2,"method":"entity.inspect","params":{"name":"Player"}}
    , world);
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "\"name\":\"Player\"") != null);
    try testing.expect(std.mem.indexOf(u8, resp, "\"type\":\"Light\"") != null);
}

test "entity.inspect unknown entity returns error" {
    var nodes = [_]SceneNode{makeNode("X")};
    const views = [_]introspect.SceneView{.{ .name = "M", .active = true, .nodes = &nodes }};
    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":3,"method":"entity.inspect","params":{"name":"Nobody"}}
    , .{ .scenes = &views });
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "\"error\"") != null);
    try testing.expect(std.mem.indexOf(u8, resp, "not found") != null);
}

test "component.get returns component fields" {
    var nodes = [_]SceneNode{makeNode("Cam")};
    _ = nodes[0].addComponent(.{ .camera = engine.CameraComponent{ .fov = 90 } });
    const views = [_]introspect.SceneView{.{ .name = "S", .active = true, .nodes = &nodes }};

    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":4,"method":"component.get","params":{"entity":"Cam","component":"Camera"}}
    , .{ .scenes = &views });
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "\"fov\"") != null);
    try testing.expect(std.mem.indexOf(u8, resp, "90") != null);
}

test "schema returns component catalog" {
    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":5,"method":"schema"}
    , .{});
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "\"components\"") != null);
    try testing.expect(std.mem.indexOf(u8, resp, "\"tag\":\"light\"") != null);
}

test "asset.list serializes the host-supplied asset views" {
    const assets = [_]introspect.AssetView{
        .{ .guid = "11111111-1111-4111-8111-111111111111", .path = "assets/wood.material", .type = "material" },
        .{ .guid = "22222222-2222-4222-8222-222222222222", .path = "assets/crate.glb", .type = "model" },
    };
    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":7,"method":"asset.list"}
    , .{ .assets = &assets });
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "wood.material") != null);
    try testing.expect(std.mem.indexOf(u8, resp, "\"type\":\"model\"") != null);
}

test "asset.inspect returns one asset by guid" {
    const assets = [_]introspect.AssetView{
        .{ .guid = "abc-guid", .path = "assets/wood.material", .type = "material" },
    };
    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":8,"method":"asset.inspect","params":{"guid":"abc-guid"}}
    , .{ .assets = &assets });
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "wood.material") != null);
}

test "errors method returns captured diagnostic log entries" {
    engine.DiagLog.reset();
    engine.DiagLog.record(.err, "render", "shader compile failed");
    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":9,"method":"errors"}
    , .{});
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "shader compile failed") != null);
    try testing.expect(std.mem.indexOf(u8, resp, "\"level\":\"err\"") != null);
}

test "memory method reports metrics memory subset" {
    var metrics = introspect.Metrics{ .memory_bytes = 4096, .allocation_count = 12 };
    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":10,"method":"memory"}
    , .{ .metrics = &metrics });
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "\"memory_bytes\":4096") != null);
}

test "profiler.capture returns a frame object" {
    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":11,"method":"profiler.capture"}
    , .{});
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "\"counters\"") != null);
    try testing.expect(std.mem.indexOf(u8, resp, "\"threads\"") != null);
}

test "unknown method returns METHOD_NOT_FOUND" {
    const resp = try dispatchStr(testing.allocator,
        \\{"jsonrpc":"2.0","id":6,"method":"does.not.exist"}
    , .{});
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "-32601") != null);
}
