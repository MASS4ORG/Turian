//! JSON-RPC 2.0 framing for the Turian Remote Debug Protocol.
//!
//! Wire format: each message is one line of JSON terminated by `\n`.
//! The transport (TCP/Unix socket) is chosen by the caller; this module only
//! handles serialisation and deserialisation.

const std = @import("std");

// ── Constants ────────────────────────────────────────────────────────────────

pub const VERSION = "2.0";
pub const DEFAULT_PORT: u16 = 7777;

/// Default buffer size for a single JSON-RPC message (64 KiB). This is the
/// *initial* read/write buffer size, **not** a ceiling: a full `snapshot` (or a
/// large `scene.inspect`) easily serialises past 64 KiB, so the wire readers
/// grow as needed up to `MAX_LINE_BYTES`.
pub const MAX_MESSAGE_BYTES: usize = 65536;

/// Hard ceiling for a single newline-framed message read off the wire. Lines may
/// grow past `MAX_MESSAGE_BYTES`, but are still bounded so a hostile or buggy
/// peer cannot exhaust memory with a delimiter-less stream (16 MiB).
pub const MAX_LINE_BYTES: usize = 16 * 1024 * 1024;

pub const ReadLineError = error{ StreamTooLong, ReadFailed, EndOfStream, OutOfMemory };

/// Reads one `\n`-terminated line into a heap buffer that grows past the
/// reader's fixed buffer, up to `max_bytes`. Returns the line WITHOUT the
/// trailing `\n` (a trailing `\r` is stripped); the caller frees it.
///
/// Unlike a fixed-buffer `takeDelimiterInclusive`, this does not fail on lines
/// larger than the reader's buffer — only when a single line exceeds
/// `max_bytes` (`error.StreamTooLong`). A clean close returns `error.EndOfStream`.
pub fn readLine(r: *std.Io.Reader, allocator: std.mem.Allocator, max_bytes: usize) ReadLineError![]u8 {
    var out: std.Io.Writer.Allocating = .init(allocator);
    errdefer out.deinit();
    _ = r.streamDelimiterLimit(&out.writer, '\n', std.Io.Limit.limited(max_bytes)) catch |err| switch (err) {
        error.StreamTooLong => return error.StreamTooLong,
        error.WriteFailed => return error.OutOfMemory,
        error.ReadFailed => return error.ReadFailed,
    };
    // `streamDelimiterLimit` leaves the delimiter buffered; its absence means
    // the stream ended (a partial, unterminated final line is dropped to match
    // `takeDelimiterInclusive`).
    const b = r.takeByte() catch return error.EndOfStream;
    std.debug.assert(b == '\n');
    // Strip a trailing CR so CRLF-framed peers parse identically to LF peers.
    if (out.writer.end > 0 and out.writer.buffer[out.writer.end - 1] == '\r')
        out.writer.end -= 1;
    return out.toOwnedSlice();
}

// ── Error codes (JSON-RPC 2.0 standard + Turian extensions) ─────────────────

pub const ErrorCode = struct {
    pub const PARSE_ERROR: i32 = -32700;
    pub const INVALID_REQUEST: i32 = -32600;
    pub const METHOD_NOT_FOUND: i32 = -32601;
    pub const INVALID_PARAMS: i32 = -32602;
    pub const INTERNAL_ERROR: i32 = -32603;
    // Turian-specific (range -32000 to -32099)
    pub const NOT_FOUND: i32 = -32000;
    pub const READONLY: i32 = -32001;
    pub const RATE_LIMITED: i32 = -32002;
};

// ── Wire types ───────────────────────────────────────────────────────────────

/// A parsed inbound JSON-RPC 2.0 request or notification.
/// `id_buf` is empty for notifications (no response expected).
pub const Request = struct {
    id_buf: [64]u8 = std.mem.zeroes([64]u8),
    id_len: usize = 0,
    id_is_number: bool = false,
    id_number: i64 = 0,
    method_buf: [128]u8 = std.mem.zeroes([128]u8),
    method_len: usize = 0,
    /// Raw JSON of the `params` value, or empty if absent.
    params_buf: [MAX_MESSAGE_BYTES]u8 = std.mem.zeroes([MAX_MESSAGE_BYTES]u8),
    params_len: usize = 0,

    pub fn method(self: *const Request) []const u8 {
        return self.method_buf[0..self.method_len];
    }

    pub fn params(self: *const Request) []const u8 {
        return self.params_buf[0..self.params_len];
    }

    pub fn isNotification(self: *const Request) bool {
        return self.id_len == 0 and !self.id_is_number;
    }

    /// Parse a newline-terminated JSON-RPC line.
    pub fn parse(line: []const u8) error{ ParseError, InvalidRequest }!Request {
        var req = Request{};

        const parsed = std.json.parseFromSlice(std.json.Value, std.heap.page_allocator, line, .{}) catch
            return error.ParseError;
        defer parsed.deinit();
        const root = parsed.value;

        if (root != .object) return error.InvalidRequest;
        const obj = root.object;

        // id (optional — absent means notification)
        if (obj.get("id")) |id_val| {
            switch (id_val) {
                .integer => |n| {
                    req.id_is_number = true;
                    req.id_number = n;
                },
                .string => |s| {
                    const len = @min(s.len, req.id_buf.len);
                    @memcpy(req.id_buf[0..len], s[0..len]);
                    req.id_len = len;
                },
                else => {},
            }
        }

        // method (required)
        const meth = switch (obj.get("method") orelse return error.InvalidRequest) {
            .string => |s| s,
            else => return error.InvalidRequest,
        };
        const mlen = @min(meth.len, req.method_buf.len);
        @memcpy(req.method_buf[0..mlen], meth[0..mlen]);
        req.method_len = mlen;

        // params (optional — store as raw JSON for lazy parsing)
        if (obj.get("params")) |p| {
            var out: std.Io.Writer.Allocating = .init(std.heap.page_allocator);
            defer out.deinit();
            var jw = std.json.Stringify{ .writer = &out.writer, .options = .{} };
            jw.write(p) catch return error.ParseError;
            const raw = out.written();
            // Reject loudly rather than silently truncate: a value that overflows
            // the fixed params buffer would otherwise corrupt the request.
            if (raw.len > req.params_buf.len) return error.InvalidRequest;
            @memcpy(req.params_buf[0..raw.len], raw);
            req.params_len = raw.len;
        }

        return req;
    }
};

// ── Response helpers ─────────────────────────────────────────────────────────

/// Writes a success response: `{"jsonrpc":"2.0","id":<id>,"result":<result_json>}\n`
/// `result_json` must be a valid JSON fragment.
pub fn writeSuccess(w: *std.Io.Writer, req: *const Request, result_json: []const u8) !void {
    try w.writeAll("{\"jsonrpc\":\"2.0\",\"id\":");
    try writeId(w, req);
    try w.writeAll(",\"result\":");
    try w.writeAll(result_json);
    try w.writeAll("}\n");
}

/// Writes an error response.
pub fn writeError(w: *std.Io.Writer, req: *const Request, code: i32, msg: []const u8) !void {
    try w.writeAll("{\"jsonrpc\":\"2.0\",\"id\":");
    try writeId(w, req);
    try w.print(",\"error\":{{\"code\":{d},\"message\":", .{code});
    var jw = std.json.Stringify{ .writer = w, .options = .{} };
    try jw.write(msg);
    try w.writeAll("}}\n");
}

/// Writes a notification (no id): `{"jsonrpc":"2.0","method":<m>,"params":<p>}\n`
pub fn writeNotification(w: *std.Io.Writer, method_name: []const u8, params_json: []const u8) !void {
    try w.writeAll("{\"jsonrpc\":\"2.0\",\"method\":");
    var jw = std.json.Stringify{ .writer = w, .options = .{} };
    try jw.write(method_name);
    try w.writeAll(",\"params\":");
    try w.writeAll(params_json);
    try w.writeAll("}\n");
}

fn writeId(w: *std.Io.Writer, req: *const Request) !void {
    if (req.id_is_number) {
        try w.print("{d}", .{req.id_number});
    } else if (req.id_len > 0) {
        var jw = std.json.Stringify{ .writer = w, .options = .{} };
        try jw.write(req.id_buf[0..req.id_len]);
    } else {
        try w.writeAll("null");
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

test "parse numeric id + method + params" {
    const line =
        \\{"jsonrpc":"2.0","id":1,"method":"entity.inspect","params":{"name":"Player"}}
    ;
    const req = try Request.parse(line);
    try std.testing.expect(req.id_is_number);
    try std.testing.expectEqual(@as(i64, 1), req.id_number);
    try std.testing.expectEqualStrings("entity.inspect", req.method());
    try std.testing.expect(req.params_len > 0);
    try std.testing.expect(!req.isNotification());
}

test "parse string id" {
    const line =
        \\{"jsonrpc":"2.0","id":"abc","method":"scene.list"}
    ;
    const req = try Request.parse(line);
    try std.testing.expect(!req.id_is_number);
    try std.testing.expectEqualStrings("abc", req.id_buf[0..req.id_len]);
    try std.testing.expectEqualStrings("scene.list", req.method());
}

test "parse notification (no id)" {
    const line =
        \\{"jsonrpc":"2.0","method":"subscribe","params":{"event":"fps.changed"}}
    ;
    const req = try Request.parse(line);
    try std.testing.expect(req.isNotification());
    try std.testing.expectEqualStrings("subscribe", req.method());
}

test "parse error on invalid JSON" {
    try std.testing.expectError(error.ParseError, Request.parse("{not json}"));
}

test "parse error on missing method" {
    try std.testing.expectError(error.InvalidRequest, Request.parse(
        \\{"jsonrpc":"2.0","id":1}
    ));
}

test "parse rejects params larger than the params buffer instead of truncating" {
    const a = std.testing.allocator;
    // A params value whose serialised form exceeds MAX_MESSAGE_BYTES.
    var line: std.ArrayList(u8) = .empty;
    defer line.deinit(a);
    try line.appendSlice(a, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"component.set\",\"params\":{\"v\":\"");
    try line.appendNTimes(a, 'x', MAX_MESSAGE_BYTES);
    try line.appendSlice(a, "\"}}");
    try std.testing.expectError(error.InvalidRequest, Request.parse(line.items));
}

test "readLine reads a normal line and strips CRLF" {
    var r = std.Io.Reader.fixed("hello\r\nworld\n");
    const a = std.testing.allocator;
    const l1 = try readLine(&r, a, MAX_LINE_BYTES);
    defer a.free(l1);
    try std.testing.expectEqualStrings("hello", l1);
    const l2 = try readLine(&r, a, MAX_LINE_BYTES);
    defer a.free(l2);
    try std.testing.expectEqualStrings("world", l2);
    try std.testing.expectError(error.EndOfStream, readLine(&r, a, MAX_LINE_BYTES));
}

test "readLine grows past the reader buffer for a >64 KiB line" {
    const a = std.testing.allocator;
    const big = 200 * 1024;
    var src: std.ArrayList(u8) = .empty;
    defer src.deinit(a);
    try src.appendNTimes(a, 'A', big);
    try src.append(a, '\n');
    // A small fixed reader buffer proves the line is reassembled on the heap,
    // not in the reader's buffer.
    var buf: [4096]u8 = undefined;
    var fixed = std.Io.Reader.fixed(src.items);
    var limited = fixed.limited(.unlimited, &buf);
    const line = try readLine(&limited.interface, a, MAX_LINE_BYTES);
    defer a.free(line);
    try std.testing.expectEqual(@as(usize, big), line.len);
    try std.testing.expect(std.mem.allEqual(u8, line, 'A'));
}

test "readLine enforces the max-bytes ceiling" {
    const a = std.testing.allocator;
    var src: std.ArrayList(u8) = .empty;
    defer src.deinit(a);
    try src.appendNTimes(a, 'A', 1024); // no newline within the limit
    var r = std.Io.Reader.fixed(src.items);
    try std.testing.expectError(error.StreamTooLong, readLine(&r, a, 256));
}
