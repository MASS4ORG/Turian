//! MCP (Model Context Protocol) wire helpers.
//!
//! Implements the 2024-11-05 MCP spec over the JSON-RPC 2.0 stdio transport.
//! Messages are newline-delimited JSON, one per line — identical framing to
//! the Turian Remote Debug Protocol.

const std = @import("std");

pub const VERSION = "2024-11-05";
pub const SERVER_NAME = "turian-mcp";
pub const SERVER_VERSION = "1.0";

// ── Request parsing ───────────────────────────────────────────────────────────

/// A parsed inbound MCP JSON-RPC message.
pub const Request = struct {
    id_buf: [64]u8 = std.mem.zeroes([64]u8),
    id_len: usize = 0,
    id_is_number: bool = false,
    id_number: i64 = 0,
    method_buf: [128]u8 = std.mem.zeroes([128]u8),
    method_len: usize = 0,
    /// Raw JSON of the `params` value, empty if absent.
    params_buf: [65536]u8 = std.mem.zeroes([65536]u8),
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

    pub fn parse(line: []const u8) error{ ParseError, InvalidRequest }!Request {
        var req = Request{};

        const parsed = std.json.parseFromSlice(
            std.json.Value,
            std.heap.page_allocator,
            line,
            .{},
        ) catch return error.ParseError;
        defer parsed.deinit();

        if (parsed.value != .object) return error.InvalidRequest;
        const obj = parsed.value.object;

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

        const meth = switch (obj.get("method") orelse return error.InvalidRequest) {
            .string => |s| s,
            else => return error.InvalidRequest,
        };
        const mlen = @min(meth.len, req.method_buf.len);
        @memcpy(req.method_buf[0..mlen], meth[0..mlen]);
        req.method_len = mlen;

        if (obj.get("params")) |p| {
            var out: std.Io.Writer.Allocating = .init(std.heap.page_allocator);
            defer out.deinit();
            var jw = std.json.Stringify{ .writer = &out.writer, .options = .{} };
            jw.write(p) catch return error.ParseError;
            const raw = out.written();
            const plen = @min(raw.len, req.params_buf.len);
            @memcpy(req.params_buf[0..plen], raw[0..plen]);
            req.params_len = plen;
        }

        return req;
    }
};

// ── Response writers ──────────────────────────────────────────────────────────

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

/// `{"jsonrpc":"2.0","id":<id>,"result":<result_json>}\n`
pub fn writeResult(w: *std.Io.Writer, req: *const Request, result_json: []const u8) !void {
    try w.writeAll("{\"jsonrpc\":\"2.0\",\"id\":");
    try writeId(w, req);
    try w.writeAll(",\"result\":");
    try w.writeAll(result_json);
    try w.writeAll("}\n");
}

/// `{"jsonrpc":"2.0","id":<id>,"error":{"code":<c>,"message":<m>}}\n`
pub fn writeError(w: *std.Io.Writer, req: *const Request, code: i32, msg: []const u8) !void {
    try w.writeAll("{\"jsonrpc\":\"2.0\",\"id\":");
    try writeId(w, req);
    try w.print(",\"error\":{{\"code\":{d},\"message\":", .{code});
    var jw = std.json.Stringify{ .writer = w, .options = .{} };
    try jw.write(msg);
    try w.writeAll("}}\n");
}

/// MCP tool result: `{"content":[{"type":"text","text":<text_json>}]}`
/// `is_error` sets the optional `isError` flag (tool-level error, not JSON-RPC error).
pub fn writeToolResult(w: *std.Io.Writer, req: *const Request, text: []const u8, is_error: bool) !void {
    var out: std.Io.Writer.Allocating = .init(std.heap.page_allocator);
    defer out.deinit();
    var jw = std.json.Stringify{ .writer = &out.writer, .options = .{} };
    try jw.beginObject();
    try jw.objectField("content");
    try jw.beginArray();
    try jw.beginObject();
    try jw.objectField("type");
    try jw.write("text");
    try jw.objectField("text");
    try jw.write(text);
    try jw.endObject();
    try jw.endArray();
    if (is_error) {
        try jw.objectField("isError");
        try jw.write(true);
    }
    try jw.endObject();
    try writeResult(w, req, out.written());
}

/// Standard MCP initialize result.
pub fn writeInitialize(w: *std.Io.Writer, req: *const Request) !void {
    const body =
        \\{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"turian-mcp","version":"1.0"}}
    ;
    try writeResult(w, req, body);
}

// ── Error codes ───────────────────────────────────────────────────────────────

pub const E_PARSE = -32700;
pub const E_INVALID = -32600;
pub const E_METHOD = -32601;
pub const E_PARAMS = -32602;
pub const E_INTERNAL = -32603;
