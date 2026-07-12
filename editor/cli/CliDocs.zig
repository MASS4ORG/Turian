const std = @import("std");
const editor = @import("editor");
const engine = @import("engine");
const rmcp = @import("mcp");
const build_options = @import("turian_build_options");

pub fn printUsageDocs() void {
    std.debug.print(
        \\Usage:  turian-cli docs <subcommand> [--out <dir>]
        \\
        \\Subcommands:
        \\  export-ai-context   Generate a self-contained AI knowledge pack (no game needed)
        \\
        \\Flags:
        \\  --out <dir>   Output directory (default: .turian)
        \\
    , .{});
}

pub fn cmdDocs(io: std.Io, gpa: std.mem.Allocator, sub: []const u8, args: *std.process.Args.Iterator) !void {
    if (std.mem.eql(u8, sub, "export-ai-context")) {
        return cmdDocsExportAiContext(io, gpa, args);
    }
    printUsageDocs();
    return error.UnknownSubcommand;
}

fn cmdDocsExportAiContext(io: std.Io, gpa: std.mem.Allocator, args: *std.process.Args.Iterator) !void {
    var out_dir_buf: [512]u8 = std.mem.zeroes([512]u8);
    var out_dir_len: usize = 8;
    @memcpy(out_dir_buf[0..out_dir_len], ".turian/");

    while (args.next()) |a| {
        if (std.mem.eql(u8, a, "--out")) {
            const v = args.next() orelse return error.MissingArg;
            const n = @min(v.len, out_dir_buf.len - 2);
            @memcpy(out_dir_buf[0..n], v[0..n]);
            if (out_dir_buf[n - 1] != '/') {
                out_dir_buf[n] = '/';
                out_dir_len = n + 1;
            } else {
                out_dir_len = n;
            }
        }
    }

    const out_path = out_dir_buf[0..out_dir_len];
    std.debug.print("Exporting AI context to {s}\n", .{out_path});

    const cwd = std.Io.Dir.cwd();
    try cwd.createDirPath(io, std.mem.trimEnd(u8, out_path, "/"));

    try writeAiContextFile(io, gpa, cwd, out_path, "engine-overview.md", aiContextOverview);
    try writeAiContextFile(io, gpa, cwd, out_path, "component-schema.json", null);
    try writeAiContextFile(io, gpa, cwd, out_path, "protocol-reference.json", aiContextProtocol);
    try writeAiContextFile(io, gpa, cwd, out_path, "mcp-tools.json", null);

    const examples_path = try std.fmt.allocPrint(gpa, "{s}examples/", .{out_path});
    defer gpa.free(examples_path);
    try cwd.createDirPath(io, std.mem.trimEnd(u8, examples_path, "/"));
    try writeAiContextFile(io, gpa, cwd, examples_path, "list-scenes.json", exampleListScenes);
    try writeAiContextFile(io, gpa, cwd, examples_path, "inspect-entity.json", exampleInspectEntity);

    try writeComponentSchema(io, gpa, cwd, out_path);
    try writeMcpTools(io, gpa, cwd, out_path);
    try writeAssetSchema(io, gpa, cwd, out_path);
    try writeEventCatalog(io, gpa, cwd, out_path);

    std.debug.print("Done. Add to CLAUDE.md:\n  @{s}engine-overview.md\n", .{out_path});
}

fn writeAiContextFile(
    io: std.Io,
    gpa: std.mem.Allocator,
    cwd: std.Io.Dir,
    dir: []const u8,
    name: []const u8,
    content: ?[]const u8,
) !void {
    if (content == null) return;
    const path = try std.fmt.allocPrint(gpa, "{s}{s}", .{ dir, name });
    defer gpa.free(path);
    try cwd.writeFile(io, .{ .sub_path = path, .data = content.? });
    std.debug.print("  wrote {s}{s}\n", .{ dir, name });
}

fn writeComponentSchema(io: std.Io, gpa: std.mem.Allocator, cwd: std.Io.Dir, dir: []const u8) !void {
    var buf: std.Io.Writer.Allocating = .init(gpa);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{ .whitespace = .indent_2 } };
    try engine.introspect.writeSchema(&jw);
    const path = try std.fmt.allocPrint(gpa, "{s}component-schema.json", .{dir});
    defer gpa.free(path);
    try cwd.writeFile(io, .{ .sub_path = path, .data = buf.written() });
    std.debug.print("  wrote {s}component-schema.json\n", .{dir});
}

fn writeMcpTools(io: std.Io, gpa: std.mem.Allocator, cwd: std.Io.Dir, dir: []const u8) !void {
    var buf: std.Io.Writer.Allocating = .init(gpa);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{ .whitespace = .indent_2 } };
    try jw.beginObject();
    try jw.objectField("tools");
    try jw.beginArray();
    for (rmcp.Tools.ALL) |t| {
        try jw.beginObject();
        try jw.objectField("name");
        try jw.write(t.name);
        try jw.objectField("description");
        try jw.write(t.description);
        try jw.objectField("debug_method");
        try jw.write(t.debug_method orelse "");
        try jw.objectField("mutates");
        try jw.write(t.mutates);
        try jw.objectField("inputSchema");
        try jw.beginWriteRaw();
        try jw.writer.writeAll(t.input_schema);
        jw.endWriteRaw();
        try jw.endObject();
    }
    try jw.endArray();
    try jw.endObject();
    const path = try std.fmt.allocPrint(gpa, "{s}mcp-tools.json", .{dir});
    defer gpa.free(path);
    try cwd.writeFile(io, .{ .sub_path = path, .data = buf.written() });
    std.debug.print("  wrote {s}mcp-tools.json\n", .{dir});
}

fn writeAssetSchema(io: std.Io, gpa: std.mem.Allocator, cwd: std.Io.Dir, dir: []const u8) !void {
    var buf: std.Io.Writer.Allocating = .init(gpa);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{ .whitespace = .indent_2 } };
    try jw.beginObject();
    try jw.objectField("asset_types");
    try jw.beginArray();
    inline for (@typeInfo(editor.AssetType).@"enum".fields) |f| try jw.write(f.name);
    try jw.endArray();
    try jw.endObject();
    const path = try std.fmt.allocPrint(gpa, "{s}asset-schema.json", .{dir});
    defer gpa.free(path);
    try cwd.writeFile(io, .{ .sub_path = path, .data = buf.written() });
    std.debug.print("  wrote {s}asset-schema.json\n", .{dir});
}

fn writeEventCatalog(io: std.Io, gpa: std.mem.Allocator, cwd: std.Io.Dir, dir: []const u8) !void {
    var buf: std.Io.Writer.Allocating = .init(gpa);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{ .whitespace = .indent_2 } };
    try engine.introspect.writeEventCatalog(&jw);
    const path = try std.fmt.allocPrint(gpa, "{s}events.json", .{dir});
    defer gpa.free(path);
    try cwd.writeFile(io, .{ .sub_path = path, .data = buf.written() });
    std.debug.print("  wrote {s}events.json\n", .{dir});
}

const aiContextOverview =
    \\# Turian Engine — AI Context Overview
    \\
    \\Turian is a Zig game engine. This document is machine-generated for LLM context.
    \\
    \\## Architecture
    \\
    \\- **engine/** — core types: SceneNode, Component, Transform, assets, ECS
    \\- **debug/** — Remote Debug Protocol: JSON-RPC 2.0 over TCP (port 7777 game, 7778 Studio)
    \\- **mcp/** — MCP adapter: stdio JSON-RPC 2.0, version 2024-11-05
    \\- **editor/** — CLI tools: `turian-cli`
    \\- **studio/** — GUI editor: `turian-studio`
    \\- **render/** — GPU renderer (SDL3-GPU, SPIRV shaders)
    \\
    \\## Connecting to a Live Session
    \\
    \\Add to `.mcp.json`:
    \\```json
    \\{
    \\  "mcpServers": {
    \\    "turian-game":   { "command": "turian-cli", "args": ["mcp"] },
    \\    "turian-studio": { "command": "turian-cli", "args": ["mcp", "--port", "7778"] }
    \\  }
    \\}
    \\```
    \\
    \\The game must link the `debug` module and call `debug.Server.start()`.
    \\The Studio starts its server automatically on port 7778.
    \\
    \\## Key Concepts
    \\
    \\### SceneNode
    \\Every object in a scene. Has: name (max 64 chars), transform (position/rotation/scale),
    \\active flag, parent index, and up to 8 components.
    \\
    \\### Component
    \\Tagged union over all built-in types (Camera, Light, MeshRenderer, RigidBody,
    \\Collider, AudioSource, Animator) plus user scripts. See component-schema.json for fields.
    \\
    \\### Transform
    \\position: [3]f32, rotation: Quaternion ([4]f32 xyzw), scale: [3]f32.
    \\
    \\### Metrics
    \\fps, frame_time_ms, frame_count, memory_bytes, allocation_count,
    \\draw_calls, triangles, gpu_time_ms, scene_count, entity_count, component_count.
    \\
    \\## Available MCP Tools
    \\
    \\See mcp-tools.json for the full list with schemas.
    \\Quick reference:
    \\- Read: list_scenes, inspect_scene, find_entities, scene_summary,
    \\  inspect_entity, get_component, get_metrics, get_schema,
    \\  list_assets, inspect_material, capture_profiler, inspect_memory, list_errors
    \\- Write (read-write mode + confirm): modify_component, set_transform,
    \\  spawn_entity, destroy_entity, reload_asset
    \\
    \\## Events
    \\
    \\Clients can `subscribe` to runtime events and receive JSON-RPC notifications.
    \\See events.json for the catalog (entity.created, entity.destroyed,
    \\scene.loaded, scene.unloaded, resource.reloaded, fps.changed).
    \\
    \\## Debug Protocol Methods
    \\
    \\See protocol-reference.json. All MCP tools map 1:1 to debug methods.
    \\
    \\## Safety
    \\
    \\Reads are always available. Mutating methods require the debug server started
    \\in read-write mode (CLI `--rw` / `allow_write`); otherwise they return a
    \\READONLY (-32001) error. The Studio runs read-write so LLM tools can edit the
    \\open scene (all edits go through the editor's undo stack). The MCP layer adds a
    \\confirmation gate: a mutating tool first returns a preview and only applies on
    \\a second call with `confirm: true`. A per-session `session.readonly` request
    \\and an optional per-connection rate limit provide further guardrails.
    \\
;

const aiContextProtocol =
    \\{
    \\  "protocol": "Turian Remote Debug Protocol",
    \\  "transport": "JSON-RPC 2.0 over TCP, newline-delimited",
    \\  "default_port_game": 7777,
    \\  "default_port_studio": 7778,
    \\  "methods": {
    \\    "ping": { "params": null, "result": "\"pong\"" },
    \\    "auth": { "params": { "token": "string" }, "result": "\"ok\"" },
    \\    "scene.list": { "params": null, "result": "array of scene objects" },
    \\    "scene.inspect": { "params": { "name": "string?" }, "result": "scene with nodes" },
    \\    "entity.find": { "params": { "name": "string?", "component": "string?" }, "result": "array of entity summaries" },
    \\    "entity.inspect": { "params": { "name": "string | index: integer" }, "result": "entity detail with transform + components" },
    \\    "component.get": { "params": { "entity": "string", "component": "string" }, "result": "component fields object" },
    \\    "component.set": { "params": { "entity": "string", "component": "string", "field": "string", "value": "any" }, "result": "{ ok, message }", "note": "mutating; requires read-write server" },
    \\    "transform.set": { "params": { "entity": "string", "channel": "position|rotation|scale", "value": "[x,y,z]" }, "result": "{ ok, message }", "note": "mutating" },
    \\    "entity.spawn": { "params": { "name": "string" }, "result": "{ ok, message }", "note": "mutating" },
    \\    "entity.destroy": { "params": { "entity": "string" }, "result": "{ ok, message }", "note": "mutating" },
    \\    "asset.list": { "params": null, "result": "array of { guid, path, type }" },
    \\    "asset.inspect": { "params": { "guid": "string" }, "result": "{ guid, path, type }" },
    \\    "asset.reload": { "params": { "guid": "string" }, "result": "{ ok, message }", "note": "mutating" },
    \\    "snapshot": { "params": null, "result": "full world snapshot" },
    \\    "schema": { "params": null, "result": "component type catalog" },
    \\    "metrics": { "params": null, "result": "runtime performance counters" },
    \\    "profiler.capture": { "params": null, "result": "latest profiler frame (counters + zones)" },
    \\    "memory": { "params": null, "result": "{ memory_bytes, allocation_count }" },
    \\    "errors": { "params": null, "result": "array of recent warn/err log entries" },
    \\    "subscribe": { "params": { "event": "string | \"*\"" }, "result": "\"ok\"", "note": "streams notifications" },
    \\    "unsubscribe": { "params": { "event": "string | \"*\"" }, "result": "\"ok\"" },
    \\    "session.readonly": { "params": null, "result": "\"ok\"", "note": "drops this session's write rights" }
    \\  },
    \\  "error_codes": {
    \\    "-32700": "PARSE_ERROR",
    \\    "-32600": "INVALID_REQUEST",
    \\    "-32601": "METHOD_NOT_FOUND",
    \\    "-32602": "INVALID_PARAMS",
    \\    "-32603": "INTERNAL_ERROR",
    \\    "-32000": "NOT_FOUND",
    \\    "-32001": "READONLY",
    \\    "-32002": "RATE_LIMITED"
    \\  }
    \\}
    \\
;

const exampleListScenes =
    \\{
    \\  "request":  { "jsonrpc": "2.0", "id": 1, "method": "scene.list" },
    \\  "response": { "jsonrpc": "2.0", "id": 1, "result": [
    \\    { "name": "Main", "id": "main.scene", "active": true, "node_count": 12 }
    \\  ]}
    \\}
    \\
;

const exampleInspectEntity =
    \\{
    \\  "request":  { "jsonrpc": "2.0", "id": 2, "method": "entity.inspect", "params": { "name": "Player" } },
    \\  "response": { "jsonrpc": "2.0", "id": 2, "result": {
    \\    "index": 3,
    \\    "name": "Player",
    \\    "active": true,
    \\    "transform": { "position": [0,1,0], "rotation": [0,0,0,1], "scale": [1,1,1] },
    \\    "components": [
    \\      { "type": "RigidBody", "tag": "rigid_body", "fields": { "mass": 1.0, "use_gravity": true } }
    \\    ]
    \\  }}
    \\}
    \\
;
