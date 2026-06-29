//! Debug protocol client — connects to a running Turian debug server and
//! sends JSON-RPC 2.0 requests, reading back responses.
//! Used by `turian-cli debug` subcommands.

const std = @import("std");
const Protocol = @import("Protocol.zig");
const net = std.Io.net;

pub const ConnectError = net.IpAddress.ConnectError;

pub const Client = struct {
    stream: net.Stream,
    io: std.Io,
    read_buf: [Protocol.MAX_MESSAGE_BYTES]u8,
    write_buf: [Protocol.MAX_MESSAGE_BYTES]u8,
    next_id: i64 = 1,
    /// Persistent reader/writer over the socket. They are created lazily on the
    /// first request — *not* in `connect` — because `connect` returns the Client
    /// by value, so the buffers only settle at their final address once the
    /// caller has stored the struct. Re-creating a reader per request would drop
    /// bytes already pulled off the socket into `read_buf`, which deadlocks any
    /// follow-up read on the same connection.
    reader: ?net.Stream.Reader = null,
    writer: ?net.Stream.Writer = null,

    const Self = @This();

    /// Connect to a Turian debug server at `host:port`.
    pub fn connect(io: std.Io, host: []const u8, port: u16) ConnectError!Client {
        const addr: net.IpAddress = net.IpAddress.parse(host, port) catch
            .{ .ip4 = net.Ip4Address.loopback(port) };
        const stream = try net.IpAddress.connect(&addr, io, .{ .mode = .stream });
        var c: Client = undefined;
        c.stream = stream;
        c.io = io;
        c.next_id = 1;
        c.reader = null;
        c.writer = null;
        return c;
    }

    pub fn close(self: *Self) void {
        self.stream.close(self.io);
    }

    fn io_reader(self: *Self) *std.Io.Reader {
        if (self.reader == null) self.reader = self.stream.reader(self.io, &self.read_buf);
        return &self.reader.?.interface;
    }

    fn io_writer(self: *Self) *std.Io.Writer {
        if (self.writer == null) self.writer = self.stream.writer(self.io, &self.write_buf);
        return &self.writer.?.interface;
    }

    /// Send a JSON-RPC request and receive the response JSON string (caller frees).
    /// Returns the raw `result` or `error` object as a string.
    pub fn call(self: *Self, allocator: std.mem.Allocator, method: []const u8, params_json: ?[]const u8) ![]u8 {
        const id = self.next_id;
        self.next_id += 1;

        // Build request line. Method is a plain ASCII identifier, so it is
        // quoted directly; params is already a JSON value (object), embedded raw.
        var req_buf: [Protocol.MAX_MESSAGE_BYTES]u8 = undefined;
        const req_line = if (params_json) |p|
            try std.fmt.bufPrint(&req_buf, "{{\"jsonrpc\":\"2.0\",\"id\":{d},\"method\":\"{s}\",\"params\":{s}}}\n", .{ id, method, p })
        else
            try std.fmt.bufPrint(&req_buf, "{{\"jsonrpc\":\"2.0\",\"id\":{d},\"method\":\"{s}\"}}\n", .{ id, method });

        const w = self.io_writer();
        try w.writeAll(req_line);
        try w.flush();

        // Heap-growing read so responses larger than the 64 KiB buffer (a full
        // `snapshot` easily is) arrive intact rather than failing StreamTooLong.
        // readLine consumes the trailing '\n' and strips a trailing '\r'.
        return Protocol.readLine(self.io_reader(), allocator, Protocol.MAX_LINE_BYTES);
    }

    /// Subscribes to runtime events and streams each notification line to `w`
    /// until the connection closes. `events` is a list of event method names
    /// (e.g. "entity.created"); an empty list subscribes to all events ("*").
    /// Blocks indefinitely — the caller stops it by closing the process.
    pub fn watch(self: *Self, allocator: std.mem.Allocator, events: []const []const u8, w: *std.Io.Writer) !void {
        if (events.len == 0) {
            const resp = try self.call(allocator, "subscribe", "{\"event\":\"*\"}");
            allocator.free(resp);
        } else {
            for (events) |ev| {
                var pbuf: [128]u8 = undefined;
                const p = try std.fmt.bufPrint(&pbuf, "{{\"event\":\"{s}\"}}", .{ev});
                const resp = try self.call(allocator, "subscribe", p);
                allocator.free(resp);
            }
        }
        const r = self.io_reader();
        while (true) {
            const line = Protocol.readLine(r, allocator, Protocol.MAX_LINE_BYTES) catch break;
            defer allocator.free(line);
            if (line.len == 0) continue;
            try w.writeAll(line);
            try w.writeAll("\n");
            try w.flush();
        }
    }

    /// Records a session: subscribes to all events and appends every received
    /// notification line (newline-delimited JSON) to `file_writer` until the
    /// connection closes. A simple JSONL session log.
    pub fn record(self: *Self, allocator: std.mem.Allocator, file_writer: *std.Io.Writer) !void {
        const resp = try self.call(allocator, "subscribe", "{\"event\":\"*\"}");
        allocator.free(resp);
        const r = self.io_reader();
        while (true) {
            const line = Protocol.readLine(r, allocator, Protocol.MAX_LINE_BYTES) catch break;
            defer allocator.free(line);
            if (line.len == 0) continue;
            try file_writer.writeAll(line);
            try file_writer.writeAll("\n");
            try file_writer.flush();
        }
    }

    /// Replays a recorded JSONL file: for each line carrying a `"method"`, sends
    /// it as a request and writes the server's response to `out`. Lines that are
    /// notifications (events) are skipped. Returns the number of requests sent.
    pub fn replay(self: *Self, allocator: std.mem.Allocator, jsonl: []const u8, out: *std.Io.Writer) !usize {
        var sent: usize = 0;
        var it = std.mem.tokenizeScalar(u8, jsonl, '\n');
        while (it.next()) |raw| {
            const line = std.mem.trim(u8, raw, " \t\r");
            if (line.len == 0) continue;
            const method = extractMethod(allocator, line) orelse continue;
            defer allocator.free(method);
            const params = extractRawField(allocator, line, "params");
            defer if (params) |p| allocator.free(p);
            const resp = try self.call(allocator, method, params);
            defer allocator.free(resp);
            try out.writeAll(resp);
            try out.writeAll("\n");
            try out.flush();
            sent += 1;
        }
        return sent;
    }

    /// Authenticate with the server. Returns true on success.
    pub fn auth(self: *Self, allocator: std.mem.Allocator, token: []const u8) !bool {
        var p_buf: [512]u8 = undefined;
        const params = try std.fmt.bufPrint(&p_buf, "{{\"token\":\"{s}\"}}", .{token});
        const resp = try self.call(allocator, "auth", params);
        defer allocator.free(resp);
        return std.mem.indexOf(u8, resp, "\"ok\"") != null;
    }
};

// ── JSONL replay helpers ──────────────────────────────────────────────────────

/// Returns the `"method"` string of a JSON-RPC line (caller frees), or null if
/// absent (e.g. the line is an event notification with no method we replay).
fn extractMethod(allocator: std.mem.Allocator, line: []const u8) ?[]u8 {
    const parsed = std.json.parseFromSlice(std.json.Value, allocator, line, .{}) catch return null;
    defer parsed.deinit();
    if (parsed.value != .object) return null;
    const m = parsed.value.object.get("method") orelse return null;
    if (m != .string) return null;
    return allocator.dupe(u8, m.string) catch null;
}

/// Returns the raw JSON of a named field (caller frees), or null if absent.
fn extractRawField(allocator: std.mem.Allocator, line: []const u8, field: []const u8) ?[]u8 {
    const parsed = std.json.parseFromSlice(std.json.Value, allocator, line, .{}) catch return null;
    defer parsed.deinit();
    if (parsed.value != .object) return null;
    const v = parsed.value.object.get(field) orelse return null;
    var out: std.Io.Writer.Allocating = .init(allocator);
    defer out.deinit();
    var jw = std.json.Stringify{ .writer = &out.writer, .options = .{} };
    jw.write(v) catch return null;
    return allocator.dupe(u8, out.written()) catch null;
}

// ── Pretty-print helper ──────────────────────────────────────────────────────

/// Parse a JSON-RPC response line and pretty-print the `result` field to `w`.
/// If the response contains an `error` field, prints that instead.
pub fn printResponse(allocator: std.mem.Allocator, resp_line: []const u8, w: anytype) !void {
    const parsed = std.json.parseFromSlice(std.json.Value, allocator, resp_line, .{}) catch {
        try w.print("{s}\n", .{resp_line});
        return;
    };
    defer parsed.deinit();

    const obj = if (parsed.value == .object) parsed.value.object else {
        try w.print("{s}\n", .{resp_line});
        return;
    };

    if (obj.get("error")) |err_val| {
        if (err_val == .object) {
            const msg = if (err_val.object.get("message")) |m|
                if (m == .string) m.string else "(no message)"
            else
                "(no message)";
            const code = if (err_val.object.get("code")) |c|
                if (c == .integer) c.integer else 0
            else
                @as(i64, 0);
            try w.print("Error {d}: {s}\n", .{ code, msg });
        } else {
            try w.print("Error: {s}\n", .{resp_line});
        }
        return;
    }

    if (obj.get("result")) |result| {
        var out: std.Io.Writer.Allocating = .init(allocator);
        defer out.deinit();
        var jw = std.json.Stringify{ .writer = &out.writer, .options = .{ .whitespace = .indent_2 } };
        try jw.write(result);
        try w.print("{s}\n", .{out.written()});
    }
}
