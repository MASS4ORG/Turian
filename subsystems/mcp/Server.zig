//! MCP server — stdio transport adapter over the Turian Remote Debug Protocol.
//!
//! Reads JSON-RPC 2.0 from stdin, translates MCP tool calls into debug
//! protocol calls (via a live TCP connection to the debug server), and writes
//! MCP responses to stdout.
//!
//! Usage:
//!   var srv: Server = .{};
//!   try srv.run(io, allocator, "127.0.0.1", 7777, "");

const std = @import("std");
const Protocol = @import("Protocol.zig");
const Tools = @import("Tools.zig");
const rdebug = @import("debug");

pub const Options = struct {
    host: []const u8 = "127.0.0.1",
    port: u16 = rdebug.Protocol.DEFAULT_PORT,
    /// Auth token for the debug server. Empty = no auth.
    token: []const u8 = "",
};

pub fn run(io: std.Io, allocator: std.mem.Allocator, opts: Options) !void {
    // Connect to the debug server.
    var client = rdebug.Client.connect(io, opts.host, opts.port) catch |err| {
        const msg = std.fmt.allocPrint(
            allocator,
            "[turian-mcp] Cannot connect to debug server at {s}:{d}: {s}\n" ++
                "Make sure the game is running with the debug server enabled.\n",
            .{ opts.host, opts.port, @errorName(err) },
        ) catch "";
        defer if (msg.len > 0) allocator.free(msg);
        std.Io.File.stderr().writeStreamingAll(io, msg) catch {};
        return err;
    };
    defer client.close();

    if (opts.token.len > 0) {
        const ok = client.auth(allocator, opts.token) catch false;
        if (!ok) {
            std.Io.File.stderr().writeStreamingAll(io, "[turian-mcp] Auth failed\n") catch {};
            return error.AuthFailed;
        }
    }

    // Stdio transport.
    var read_buf: [65536]u8 = undefined;
    var write_buf: [65536]u8 = undefined;
    var in_reader = std.Io.File.stdin().reader(io, &read_buf);
    var out_writer = std.Io.File.stdout().writer(io, &write_buf);

    while (true) {
        const line = in_reader.interface.takeDelimiterInclusive('\n') catch break;
        const trimmed = std.mem.trimEnd(u8, line, "\r\n");
        if (trimmed.len == 0) continue;

        const req = Protocol.Request.parse(trimmed) catch |err| {
            const code: i32 = switch (err) {
                error.ParseError => Protocol.E_PARSE,
                error.InvalidRequest => Protocol.E_INVALID,
            };
            const dummy = Protocol.Request{};
            Protocol.writeError(&out_writer.interface, &dummy, code, @errorName(err)) catch {};
            out_writer.interface.flush() catch {};
            continue;
        };

        handleRequest(io, allocator, &req, &client, &out_writer.interface);
        out_writer.interface.flush() catch {};
    }
}

fn handleRequest(
    io: std.Io,
    allocator: std.mem.Allocator,
    req: *const Protocol.Request,
    client: *rdebug.Client,
    out: *std.Io.Writer,
) void {
    _ = io;
    const m = req.method();

    // Notifications (no response expected).
    if (std.mem.eql(u8, m, "notifications/initialized") or
        std.mem.eql(u8, m, "notifications/cancelled")) return;

    if (req.isNotification()) return;

    if (std.mem.eql(u8, m, "initialize")) {
        Protocol.writeInitialize(out, req) catch {};
        return;
    }

    if (std.mem.eql(u8, m, "ping")) {
        Protocol.writeResult(out, req, "{}") catch {};
        return;
    }

    if (std.mem.eql(u8, m, "tools/list")) {
        handleToolsList(allocator, req, out);
        return;
    }

    if (std.mem.eql(u8, m, "tools/call")) {
        handleToolCall(allocator, req, client, out);
        return;
    }

    Protocol.writeError(out, req, Protocol.E_METHOD, "Method not found") catch {};
}

fn handleToolsList(allocator: std.mem.Allocator, req: *const Protocol.Request, out: *std.Io.Writer) void {
    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };

    jw.beginObject() catch return;
    jw.objectField("tools") catch return;
    jw.beginArray() catch return;

    for (Tools.ALL) |t| {
        jw.beginObject() catch return;
        jw.objectField("name") catch return;
        jw.write(t.name) catch return;
        jw.objectField("description") catch return;
        jw.write(t.description) catch return;
        jw.objectField("inputSchema") catch return;
        jw.beginWriteRaw() catch return;
        jw.writer.writeAll(t.input_schema) catch return;
        jw.endWriteRaw();
        jw.endObject() catch return;
    }

    jw.endArray() catch return;
    jw.endObject() catch return;

    const json = allocator.dupe(u8, buf.written()) catch return;
    defer allocator.free(json);
    Protocol.writeResult(out, req, json) catch {};
}

fn handleToolCall(
    allocator: std.mem.Allocator,
    req: *const Protocol.Request,
    client: *rdebug.Client,
    out: *std.Io.Writer,
) void {
    // Parse params: { "name": "...", "arguments": {...} }
    if (req.params_len == 0) {
        Protocol.writeError(out, req, Protocol.E_PARAMS, "Missing params") catch {};
        return;
    }

    const parsed = std.json.parseFromSlice(
        std.json.Value,
        allocator,
        req.params(),
        .{},
    ) catch {
        Protocol.writeError(out, req, Protocol.E_PARAMS, "Invalid params JSON") catch {};
        return;
    };
    defer parsed.deinit();

    if (parsed.value != .object) {
        Protocol.writeError(out, req, Protocol.E_PARAMS, "params must be object") catch {};
        return;
    }

    const tool_name = blk: {
        const n = parsed.value.object.get("name") orelse {
            Protocol.writeError(out, req, Protocol.E_PARAMS, "Missing 'name'") catch {};
            return;
        };
        if (n != .string) {
            Protocol.writeError(out, req, Protocol.E_PARAMS, "'name' must be string") catch {};
            return;
        }
        break :blk n.string;
    };

    const tool = Tools.find(tool_name) orelse {
        const msg = std.fmt.allocPrint(allocator, "Unknown tool: {s}", .{tool_name}) catch return;
        defer allocator.free(msg);
        Protocol.writeError(out, req, Protocol.E_METHOD, msg) catch {};
        return;
    };

    // Confirmation gate: mutating tools must be called twice —
    // first returns a preview, then `confirm:true` actually forwards the call.
    // Defense-in-depth on top of the MCP client's own approval prompt.
    if (tool.mutates and !argConfirmed(parsed.value.object)) {
        const preview = buildPreview(allocator, tool.*, parsed.value.object) catch {
            Protocol.writeToolResult(out, req, "This action mutates engine state. Call again with confirm:true to apply.", false) catch {};
            return;
        };
        defer allocator.free(preview);
        Protocol.writeToolResult(out, req, preview, false) catch {};
        return;
    }

    const debug_method = tool.debug_method orelse {
        Protocol.writeToolResult(out, req, "Not implemented", true) catch {};
        return;
    };

    // Build debug params JSON from tool arguments.
    const args_json: ?[]const u8 = blk: {
        const args = parsed.value.object.get("arguments") orelse break :blk null;
        if (args == .null) break :blk null;
        // Re-serialize the arguments object as the debug params.
        var abuf: std.Io.Writer.Allocating = .init(allocator);
        var ajw = std.json.Stringify{ .writer = &abuf.writer, .options = .{} };
        ajw.write(args) catch {
            abuf.deinit();
            break :blk null;
        };
        const s = allocator.dupe(u8, abuf.written()) catch {
            abuf.deinit();
            break :blk null;
        };
        abuf.deinit();
        break :blk s;
    };
    defer if (args_json) |s| allocator.free(s);

    // Call the debug server.
    const resp = client.call(allocator, debug_method, args_json) catch |err| {
        const msg = std.fmt.allocPrint(allocator, "Debug server error: {s}", .{@errorName(err)}) catch return;
        defer allocator.free(msg);
        Protocol.writeToolResult(out, req, msg, true) catch {};
        return;
    };
    defer allocator.free(resp);

    // The debug response is a JSON-RPC envelope; extract the result or error.
    const extracted = extractDebugResult(allocator, resp) catch resp;
    const is_err = std.mem.startsWith(u8, extracted, "Error");
    Protocol.writeToolResult(out, req, extracted, is_err) catch {};
    if (extracted.ptr != resp.ptr) allocator.free(extracted);
}

/// True if the tool-call params carry `arguments.confirm == true`.
fn argConfirmed(params_obj: std.json.ObjectMap) bool {
    const args = params_obj.get("arguments") orelse return false;
    if (args != .object) return false;
    const c = args.object.get("confirm") orelse return false;
    return c == .bool and c.bool;
}

/// Builds a human-readable preview of a pending mutation for the confirmation
/// gate: a concrete one-line description of the edit, the raw arguments, and the
/// instruction to re-call with `confirm:true`. Caller frees.
fn buildPreview(allocator: std.mem.Allocator, tool: Tools.Tool, params_obj: std.json.ObjectMap) ![]u8 {
    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    const w = &buf.writer;
    const args: ?std.json.ObjectMap = if (params_obj.get("arguments")) |a|
        (if (a == .object) a.object else null)
    else
        null;

    try w.writeAll("Confirmation required: ");
    try writeConcreteEdit(w, tool.name, args);
    try w.writeAll("\n");
    if (args) |a| {
        try w.writeAll("Arguments: ");
        var jw = std.json.Stringify{ .writer = w, .options = .{} };
        try jw.write(std.json.Value{ .object = a });
        try w.writeAll("\n");
    }
    try w.writeAll("Re-call this tool with \"confirm\": true to apply it.");
    return allocator.dupe(u8, buf.written());
}

/// Writes a concrete, human-readable description of the pending edit, e.g.
/// `set Player.Light.intensity → 2.5` rather than a generic "will mutate" line.
/// Falls back to the tool name when arguments are missing or malformed.
fn writeConcreteEdit(w: *std.Io.Writer, tool_name: []const u8, args: ?std.json.ObjectMap) !void {
    const a = args orelse {
        try w.print("'{s}' will mutate engine state.", .{tool_name});
        return;
    };
    if (std.mem.eql(u8, tool_name, "modify_component")) {
        try w.print("set {s}.{s}.{s} → ", .{
            argStr(a, "entity"), argStr(a, "component"), argStr(a, "field"),
        });
        try writeArgValue(w, a, "value");
    } else if (std.mem.eql(u8, tool_name, "set_transform")) {
        try w.print("set {s}.{s} → ", .{ argStr(a, "entity"), argStr(a, "channel") });
        try writeArgValue(w, a, "value");
    } else if (std.mem.eql(u8, tool_name, "spawn_entity")) {
        try w.print("spawn entity '{s}'", .{argStr(a, "name")});
    } else if (std.mem.eql(u8, tool_name, "destroy_entity")) {
        try w.print("destroy entity '{s}'", .{argStr(a, "entity")});
    } else if (std.mem.eql(u8, tool_name, "reload_asset")) {
        try w.print("reload asset {s}", .{argStr(a, "guid")});
    } else {
        try w.print("'{s}' will mutate engine state.", .{tool_name});
    }
}

/// String value of `key`, or "?" if missing / not a string.
fn argStr(obj: std.json.ObjectMap, key: []const u8) []const u8 {
    const v = obj.get(key) orelse return "?";
    return if (v == .string) v.string else "?";
}

/// Writes the JSON value at `key` (number, array, string, …) compactly.
fn writeArgValue(w: *std.Io.Writer, obj: std.json.ObjectMap, key: []const u8) !void {
    const v = obj.get(key) orelse {
        try w.writeAll("?");
        return;
    };
    var jw = std.json.Stringify{ .writer = w, .options = .{} };
    try jw.write(v);
}

/// Extract the `result` or `error.message` from a debug server JSON-RPC response.
/// Returns a newly-allocated string, or error if parsing fails (caller uses raw resp).
fn extractDebugResult(allocator: std.mem.Allocator, resp: []const u8) ![]u8 {
    const parsed = try std.json.parseFromSlice(std.json.Value, allocator, resp, .{});
    defer parsed.deinit();

    if (parsed.value != .object) return error.NotObject;
    const obj = parsed.value.object;

    // Error path.
    if (obj.get("error")) |err_val| {
        if (err_val == .object) {
            const msg = if (err_val.object.get("message")) |m|
                if (m == .string) m.string else "(no message)"
            else
                "(no message)";
            const code: i64 = if (err_val.object.get("code")) |c|
                if (c == .integer) c.integer else 0
            else
                0;
            return std.fmt.allocPrint(allocator, "Error {d}: {s}", .{ code, msg });
        }
        return error.MalformedError;
    }

    // Result path — pretty-print for LLM readability.
    if (obj.get("result")) |result| {
        var out: std.Io.Writer.Allocating = .init(allocator);
        var jw = std.json.Stringify{ .writer = &out.writer, .options = .{ .whitespace = .indent_2 } };
        jw.write(result) catch {
            out.deinit();
            return error.SerializeFailed;
        };
        const s = allocator.dupe(u8, out.written()) catch {
            out.deinit();
            return error.OOM;
        };
        out.deinit();
        return s;
    }

    return error.NoResultOrError;
}

// ── Tests ────────────────────────────────────────────────────────────────────

const testing = std.testing;

test "mutating tool without confirm returns a preview and does not forward" {
    var out: std.Io.Writer.Allocating = .init(testing.allocator);
    defer out.deinit();
    // The preview path returns before touching the client, so an undefined
    // client is never dereferenced.
    var client: rdebug.Client = undefined;
    const req = try Protocol.Request.parse(
        \\{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"modify_component","arguments":{"entity":"Player","component":"Light","field":"intensity","value":2}}}
    );
    handleToolCall(testing.allocator, &req, &client, &out.writer);
    try testing.expect(std.mem.indexOf(u8, out.written(), "Confirmation required") != null);
    try testing.expect(std.mem.indexOf(u8, out.written(), "confirm") != null);
}

test "spawn_entity and destroy_entity tools are registered and mutate" {
    const spawn = Tools.find("spawn_entity") orelse return error.MissingTool;
    try testing.expect(spawn.mutates);
    try testing.expectEqualStrings("entity.spawn", spawn.debug_method.?);
    const destroy = Tools.find("destroy_entity") orelse return error.MissingTool;
    try testing.expect(destroy.mutates);
    try testing.expectEqualStrings("entity.destroy", destroy.debug_method.?);
}

test "preview states the concrete edit, not a generic message" {
    var out: std.Io.Writer.Allocating = .init(testing.allocator);
    defer out.deinit();
    var client: rdebug.Client = undefined;
    const req = try Protocol.Request.parse(
        \\{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"modify_component","arguments":{"entity":"Player","component":"Light","field":"intensity","value":2.5}}}
    );
    handleToolCall(testing.allocator, &req, &client, &out.writer);
    // The concrete change is echoed: "set Player.Light.intensity → 2.5".
    try testing.expect(std.mem.indexOf(u8, out.written(), "Player.Light.intensity") != null);
    try testing.expect(std.mem.indexOf(u8, out.written(), "2.5") != null);
}

test "confirmed mutating tool forwards to the debug server (H4)" {
    const Threaded = std.Io.Threaded;
    var threaded: Threaded = .init(testing.allocator, .{});
    defer threaded.deinit();
    const io = threaded.io();

    // Loopback debug server in read-write mode with a recording applier.
    var srv = rdebug.Server.init(testing.allocator, .{ .port = 39230, .allow_write = true });
    try srv.start(io);
    defer srv.deinit(io);
    io.sleep(std.Io.Duration.fromMilliseconds(50), .awake) catch {};

    const Recorded = struct {
        got: bool = false,
        field_buf: [64]u8 = undefined,
        field_len: usize = 0,
        fn field(self: *const @This()) []const u8 {
            return self.field_buf[0..self.field_len];
        }
    };
    var recorded: Recorded = .{};
    const Recorder = struct {
        fn apply(ctx: ?*anyopaque, m: rdebug.Mutation) rdebug.MutationResult {
            const r: *Recorded = @ptrCast(@alignCast(ctx.?));
            if (m == .set_component) {
                r.got = true;
                const f = m.set_component.field;
                const n = @min(f.len, r.field_buf.len);
                @memcpy(r.field_buf[0..n], f[0..n]);
                r.field_len = n;
            }
            return .{ .ok = true, .message = "applied" };
        }
    };
    const applier = rdebug.MutationApplier{ .ctx = &recorded, .applyFn = Recorder.apply };

    const Pumper = struct {
        srv: *rdebug.Server,
        io: std.Io,
        applier: rdebug.MutationApplier,
        stop: std.atomic.Value(bool) = .{ .raw = false },
        fn run(self: *@This()) void {
            while (!self.stop.load(.acquire)) {
                self.srv.pump(.{}, self.applier);
                self.io.sleep(std.Io.Duration.fromMilliseconds(2), .awake) catch {};
            }
        }
    };
    var pumper = Pumper{ .srv = &srv, .io = io, .applier = applier };
    const pump_thread = try std.Thread.spawn(.{}, Pumper.run, .{&pumper});
    defer {
        pumper.stop.store(true, .release);
        pump_thread.join();
    }

    var client = rdebug.Client.connect(io, "127.0.0.1", 39230) catch
        return error.SkipZigTest;
    defer client.close();

    var out: std.Io.Writer.Allocating = .init(testing.allocator);
    defer out.deinit();
    // confirm:true forwards straight to component.set instead of previewing.
    const req = try Protocol.Request.parse(
        \\{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"modify_component","arguments":{"entity":"Player","component":"Light","field":"intensity","value":2.5,"confirm":true}}}
    );
    handleToolCall(testing.allocator, &req, &client, &out.writer);

    try testing.expect(recorded.got);
    try testing.expectEqualStrings("intensity", recorded.field());
    try testing.expect(std.mem.indexOf(u8, out.written(), "Confirmation required") == null);
    try testing.expect(std.mem.indexOf(u8, out.written(), "applied") != null);
}
