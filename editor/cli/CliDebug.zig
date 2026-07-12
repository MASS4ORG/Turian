const std = @import("std");
const rdebug = @import("debug");

pub fn printUsageDebug() void {
    std.debug.print(
        \\turian-cli debug — Remote debug client (connects to a running game/studio)
        \\
        \\Usage:  turian-cli debug <subcommand> [--host 127.0.0.1] [--port 7777] [--token <t>]
        \\
        \\Subcommands:
        \\  connect              Test connection and print server info
        \\  scenes               List all loaded scenes
        \\  entities [--scene S] List entities in the active (or named) scene
        \\  inspect  <name>      Full detail for entity <name>
        \\  component <entity> <component>  Dump a single component's fields
        \\  snapshot             Dump a full engine snapshot to stdout
        \\  schema               Print the built-in component schema
        \\  metrics              Print live runtime metrics (FPS, memory, draws)
        \\  profiler             Capture the latest profiler frame
        \\  memory               Print allocator memory usage
        \\  errors               List recent engine warnings/errors
        \\  assets               List project assets (guid, path, type)
        \\  watch [event...]     Subscribe to runtime events and stream them
        \\                       (e.g. entity.created fps.changed; omit = all)
        \\  record <file>        Record all events to a JSONL file (Ctrl-C to stop)
        \\  replay <file>        Re-send recorded requests from a JSONL file
        \\
        \\Mutations (require the server in read-write mode):
        \\  set <entity> <component> <field> <value>   Set a component field
        \\  spawn <name>         Create a new empty entity
        \\  destroy <name>       Remove an entity by name
        \\
        \\Machine-driven UI interaction (Studio only, read-write mode; applied
        \\the frame after the call — dvui needs events before it builds widgets):
        \\  mousemove <x> <y>            Move the synthetic mouse cursor
        \\  click <x> <y> [button]       Move + press + release (button: left/right/middle, default left)
        \\  key <code> [up]              Key down (default) or up — code is a dvui.enums.Key name (e.g. "a", "enter")
        \\  text <str>                   Synthesize a text-input event
        \\  capture                      Schedule a whole-window screenshot (see `screenshot`)
        \\  screenshot                   Poll the last whole-window screenshot's result/path
        \\
    , .{});
}

fn valueLiteral(buf: []u8, raw: []const u8) []const u8 {
    if (std.mem.eql(u8, raw, "true")) return "true";
    if (std.mem.eql(u8, raw, "false")) return "false";
    if (std.fmt.parseFloat(f64, raw)) |_| {
        return raw;
    } else |_| {}
    const out = std.fmt.bufPrint(buf, "\"{s}\"", .{raw}) catch return "\"\"";
    return out;
}

pub fn printResponseStderr(allocator: std.mem.Allocator, resp: []const u8) !void {
    const parsed = std.json.parseFromSlice(std.json.Value, allocator, resp, .{}) catch {
        std.debug.print("{s}\n", .{resp});
        return;
    };
    defer parsed.deinit();
    const obj = if (parsed.value == .object) parsed.value.object else {
        std.debug.print("{s}\n", .{resp});
        return;
    };
    if (obj.get("error")) |err_val| {
        if (err_val == .object) {
            const msg = if (err_val.object.get("message")) |m| (if (m == .string) m.string else "?") else "?";
            const code = if (err_val.object.get("code")) |c| (if (c == .integer) c.integer else 0) else @as(i64, 0);
            std.debug.print("Error {d}: {s}\n", .{ code, msg });
        }
        return;
    }
    if (obj.get("result")) |result| {
        var out: std.Io.Writer.Allocating = .init(allocator);
        defer out.deinit();
        var jw = std.json.Stringify{ .writer = &out.writer, .options = .{ .whitespace = .indent_2 } };
        jw.write(result) catch {};
        std.debug.print("{s}\n", .{out.written()});
    }
}

pub fn cmdDebug(
    io: std.Io,
    gpa: std.mem.Allocator,
    sub: []const u8,
    args: *std.process.Args.Iterator,
) !void {
    var host_buf: [256]u8 = std.mem.zeroes([256]u8);
    @memcpy(host_buf[0..9], "127.0.0.1");
    var host: []u8 = host_buf[0..9];
    var port: u16 = rdebug.Protocol.DEFAULT_PORT;
    var token: []const u8 = "";
    var extra1: []const u8 = "";
    var extra2: []const u8 = "";
    var extra3: []const u8 = "";
    var extra4: []const u8 = "";

    while (args.next()) |arg| {
        if (std.mem.eql(u8, arg, "--host")) {
            const h = args.next() orelse continue;
            const len = @min(h.len, host_buf.len);
            @memcpy(host_buf[0..len], h[0..len]);
            host = host_buf[0..len];
        } else if (std.mem.eql(u8, arg, "--port")) {
            if (args.next()) |ps| port = std.fmt.parseInt(u16, ps, 10) catch port;
        } else if (std.mem.eql(u8, arg, "--token")) {
            token = args.next() orelse "";
        } else if (extra1.len == 0) {
            extra1 = arg;
        } else if (extra2.len == 0) {
            extra2 = arg;
        } else if (extra3.len == 0) {
            extra3 = arg;
        } else if (extra4.len == 0) {
            extra4 = arg;
        }
    }

    var client = rdebug.Client.connect(io, host, port) catch |err| {
        std.debug.print("Cannot connect to {s}:{d}: {s}\n", .{ host, port, @errorName(err) });
        std.debug.print("Is a Turian game or studio running with the debug server enabled?\n", .{});
        return error.ConnectionFailed;
    };
    defer client.close();

    if (token.len > 0) {
        const ok = client.auth(gpa, token) catch false;
        if (!ok) {
            std.debug.print("Authentication failed\n", .{});
            return error.AuthFailed;
        }
    }

    const w = std.Io.File.stderr().writer(io, &[_]u8{});
    _ = w;

    if (std.mem.eql(u8, sub, "connect")) {
        const resp = try client.call(gpa, "ping", null);
        defer gpa.free(resp);
        if (std.mem.indexOf(u8, resp, "pong") != null)
            std.debug.print("Connected to {s}:{d} — server is up\n", .{ host, port })
        else
            std.debug.print("Unexpected response: {s}\n", .{resp});
    } else if (std.mem.eql(u8, sub, "scenes")) {
        const resp = try client.call(gpa, "scene.list", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "entities")) {
        const params: ?[]const u8 = if (extra1.len > 0)
            try std.fmt.allocPrint(gpa, "{{\"name\":\"{s}\"}}", .{extra1})
        else
            null;
        defer if (params) |p| gpa.free(p);
        const resp = try client.call(gpa, "entity.find", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "inspect")) {
        if (extra1.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"name\":\"{s}\"}}", .{extra1});
        defer gpa.free(params);
        const resp = try client.call(gpa, "entity.inspect", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "component")) {
        if (extra1.len == 0 or extra2.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"entity\":\"{s}\",\"component\":\"{s}\"}}", .{ extra1, extra2 });
        defer gpa.free(params);
        const resp = try client.call(gpa, "component.get", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "snapshot")) {
        const resp = try client.call(gpa, "snapshot", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "schema")) {
        const resp = try client.call(gpa, "schema", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "metrics")) {
        const resp = try client.call(gpa, "metrics", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "profiler")) {
        const resp = try client.call(gpa, "profiler.capture", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "memory")) {
        const resp = try client.call(gpa, "memory", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "errors")) {
        const resp = try client.call(gpa, "errors", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "assets")) {
        const resp = try client.call(gpa, "asset.list", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "set")) {
        if (extra1.len == 0 or extra2.len == 0 or extra3.len == 0 or extra4.len == 0) return printUsageDebug();
        var vbuf: [256]u8 = undefined;
        const params = try std.fmt.allocPrint(gpa, "{{\"entity\":\"{s}\",\"component\":\"{s}\",\"field\":\"{s}\",\"value\":{s}}}", .{ extra1, extra2, extra3, valueLiteral(&vbuf, extra4) });
        defer gpa.free(params);
        const resp = try client.call(gpa, "component.set", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "spawn")) {
        if (extra1.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"name\":\"{s}\"}}", .{extra1});
        defer gpa.free(params);
        const resp = try client.call(gpa, "entity.spawn", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "destroy")) {
        if (extra1.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"entity\":\"{s}\"}}", .{extra1});
        defer gpa.free(params);
        const resp = try client.call(gpa, "entity.destroy", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "click")) {
        if (extra1.len == 0 or extra2.len == 0) return printUsageDebug();
        const button = if (extra3.len > 0) extra3 else "left";
        const params = try std.fmt.allocPrint(gpa, "{{\"x\":{s},\"y\":{s},\"button\":\"{s}\"}}", .{ extra1, extra2, button });
        defer gpa.free(params);
        const resp = try client.call(gpa, "input.click", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "mousemove")) {
        if (extra1.len == 0 or extra2.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"x\":{s},\"y\":{s}}}", .{ extra1, extra2 });
        defer gpa.free(params);
        const resp = try client.call(gpa, "input.mouseMove", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "key")) {
        if (extra1.len == 0) return printUsageDebug();
        const down = extra2.len == 0 or !std.mem.eql(u8, extra2, "up");
        const params = try std.fmt.allocPrint(gpa, "{{\"code\":\"{s}\",\"down\":{s}}}", .{ extra1, if (down) "true" else "false" });
        defer gpa.free(params);
        const resp = try client.call(gpa, "input.key", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "text")) {
        if (extra1.len == 0) return printUsageDebug();
        const params = try std.fmt.allocPrint(gpa, "{{\"text\":\"{s}\"}}", .{extra1});
        defer gpa.free(params);
        const resp = try client.call(gpa, "input.text", params);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "capture")) {
        const resp = try client.call(gpa, "screenshot.capture", "{}");
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "screenshot")) {
        const resp = try client.call(gpa, "screenshot.last", null);
        defer gpa.free(resp);
        try printResponseStderr(gpa, resp);
    } else if (std.mem.eql(u8, sub, "watch")) {
        var events: [4][]const u8 = undefined;
        var n: usize = 0;
        for ([_][]const u8{ extra1, extra2, extra3, extra4 }) |e| {
            if (e.len > 0) {
                events[n] = e;
                n += 1;
            }
        }
        std.debug.print("Watching events (Ctrl-C to stop)...\n", .{});
        var wbuf: [4096]u8 = undefined;
        var stdout = std.Io.File.stdout().writer(io, &wbuf);
        try client.watch(gpa, events[0..n], &stdout.interface);
    } else if (std.mem.eql(u8, sub, "record")) {
        if (extra1.len == 0) return printUsageDebug();
        var file = std.Io.Dir.cwd().createFile(io, extra1, .{}) catch {
            std.debug.print("Cannot create file: {s}\n", .{extra1});
            return error.RecordFailed;
        };
        defer file.close(io);
        std.debug.print("Recording session to {s} (Ctrl-C to stop)...\n", .{extra1});
        var fbuf: [4096]u8 = undefined;
        var fw = file.writer(io, &fbuf);
        try client.record(gpa, &fw.interface);
    } else if (std.mem.eql(u8, sub, "replay")) {
        if (extra1.len == 0) return printUsageDebug();
        const jsonl = std.Io.Dir.cwd().readFileAlloc(io, extra1, gpa, .limited(16 * 1024 * 1024)) catch {
            std.debug.print("Cannot read file: {s}\n", .{extra1});
            return error.ReplayFailed;
        };
        defer gpa.free(jsonl);
        var wbuf: [4096]u8 = undefined;
        var stdout = std.Io.File.stdout().writer(io, &wbuf);
        const sent = try client.replay(gpa, jsonl, &stdout.interface);
        std.debug.print("Replayed {d} request(s)\n", .{sent});
    } else {
        printUsageDebug();
        return error.UnknownDebugSubcommand;
    }
}
