//! Per-connection transport for the debug server: connection state, the
//! background accept/reader/writer threads, and connection teardown.
const std = @import("std");
const Protocol = @import("Protocol.zig");
const Server = @import("Server.zig").Server;
const net = std.Io.net;
const log = std.log.scoped(.debug_server);

/// One queued outbound line. `is_event` marks lossy notifications, which the
/// outbound-cap policy may drop under backpressure; responses are never dropped.
pub const OutItem = struct { line: []u8, is_event: bool };
pub const OutQueue = std.ArrayList(OutItem);

pub const Conn = struct {
    server: *Server,
    io: std.Io,
    id: u32,
    stream: net.Stream,
    authenticated: bool,
    /// Per-session read-only flag (a client may drop its own write rights).
    readonly: bool = false,

    read_buf: [Protocol.MAX_MESSAGE_BYTES]u8 = undefined,
    write_buf: [Protocol.MAX_MESSAGE_BYTES]u8 = undefined,

    out_mutex: std.Io.Mutex = .init,
    out_cond: std.Io.Condition = .init,
    out_queue: OutQueue = .empty,
    /// Sum of `line.len` for everything currently in `out_queue` (guarded by
    /// `out_mutex`). Drives the byte-based outbound cap.
    out_bytes: usize = 0,
    closing: std.atomic.Value(bool) = .{ .raw = false },

    reader_thread: ?std.Thread = null,
    writer_thread: ?std.Thread = null,

    /// Subscription bitset over `introspect.Event` ordinals.
    subs: u32 = 0,

    /// Token-bucket rate limiter.
    tokens: f64 = 0,
    last_refill_ns: i128 = 0,

    pub fn pushOut(self: *Conn, line: []u8, is_event: bool) void {
        self.out_mutex.lockUncancelable(self.io);
        defer self.out_mutex.unlock(self.io);
        self.enforceOutboundCap(line.len);
        self.out_queue.append(self.server.allocator, .{ .line = line, .is_event = is_event }) catch {
            self.server.allocator.free(line);
            return;
        };
        self.out_bytes += line.len;
        self.out_cond.signal(self.io);
    }

    /// Bounds the outbound backlog of a slow / non-reading client. Drops the
    /// oldest queued *events* (lossy by nature) to make room; if the head of the
    /// queue is a response we cannot drop, the client is hopelessly behind, so
    /// mark the connection for disconnect. Must hold `out_mutex`.
    fn enforceOutboundCap(self: *Conn, incoming_len: usize) void {
        const max_count = self.server.options.max_outbound_queue;
        const max_bytes = self.server.options.max_outbound_bytes;
        while (self.out_queue.items.len > 0 and
            (self.out_queue.items.len >= max_count or self.out_bytes + incoming_len > max_bytes))
        {
            const oldest = self.out_queue.items[0];
            if (!oldest.is_event) {
                self.closing.store(true, .release);
                return;
            }
            const n = oldest.line.len;
            self.server.allocator.free(oldest.line);
            self.out_bytes -= n;
            _ = self.out_queue.orderedRemove(0);
        }
    }
};

pub const InboundNode = struct { conn: *Conn, line: []u8 };

pub fn acceptLoop(srv: *Server) void {
    const addr: net.IpAddress = if (srv.options.localhost_only)
        .{ .ip4 = net.Ip4Address.loopback(srv.options.port) }
    else
        .{ .ip4 = net.Ip4Address.unspecified(srv.options.port) };

    var listener = net.IpAddress.listen(&addr, srv.io, .{ .reuse_address = true }) catch |err| {
        log.warn("listen failed on port {d}: {s}", .{ srv.options.port, @errorName(err) });
        return;
    };

    srv.listener_mutex.lockUncancelable(srv.io);
    srv.listener = listener;
    srv.listener_mutex.unlock(srv.io);

    log.info("listening on 127.0.0.1:{d} ({s})", .{ srv.options.port, if (srv.options.allow_write) "read-write" else "read-only" });

    while (!srv.stop_flag.load(.acquire)) {
        const stream = listener.accept(srv.io) catch break;
        srv.onAccept(stream);
    }

    log.info("stopped (port {d})", .{srv.options.port});
}

pub fn connReader(conn: *Conn) void {
    var reader = conn.stream.reader(conn.io, &conn.read_buf);
    const alloc = conn.server.allocator;
    while (!conn.closing.load(.acquire)) {
        // A big `component.set` value can exceed the 64 KiB stack buffer, so use the heap-growing read.
        const line = Protocol.readLine(&reader.interface, alloc, Protocol.MAX_LINE_BYTES) catch |err| {
            if (err == error.StreamTooLong)
                conn.server.enqueueError(conn, Protocol.ErrorCode.INVALID_REQUEST, "Request line exceeds maximum size");
            break;
        };
        if (line.len == 0) {
            alloc.free(line);
            continue;
        }
        conn.server.pushInbound(conn, line);
    }
    conn.closing.store(true, .release);
    // Wake the writer so it can observe the close and exit.
    conn.out_mutex.lockUncancelable(conn.io);
    conn.out_cond.signal(conn.io);
    conn.out_mutex.unlock(conn.io);
}

pub fn connWriter(conn: *Conn) void {
    var writer = conn.stream.writer(conn.io, &conn.write_buf);
    while (true) {
        conn.out_mutex.lockUncancelable(conn.io);
        while (conn.out_queue.items.len == 0 and !conn.closing.load(.acquire)) {
            conn.out_cond.waitUncancelable(conn.io, &conn.out_mutex);
        }
        if (conn.out_queue.items.len == 0 and conn.closing.load(.acquire)) {
            conn.out_mutex.unlock(conn.io);
            return;
        }
        const batch = conn.out_queue.toOwnedSlice(conn.server.allocator) catch {
            conn.out_mutex.unlock(conn.io);
            continue;
        };
        conn.out_bytes = 0;
        conn.out_mutex.unlock(conn.io);

        var failed = false;
        for (batch) |item| {
            if (!failed) {
                writer.interface.writeAll(item.line) catch {
                    failed = true;
                };
            }
            conn.server.allocator.free(item.line);
        }
        conn.server.allocator.free(batch);
        if (failed) {
            conn.closing.store(true, .release);
            return;
        }
        writer.interface.flush() catch {
            conn.closing.store(true, .release);
            return;
        };
    }
}

/// Writes a JSON-RPC error to a connection that will not be served, then
/// half-closes so the peer sees the message followed by a clean end-of-stream.
///
/// The caller must not close the socket straight after: Windows turns a close
/// with unread inbound data into an RST, which discards the error line the
/// client is still about to read. `Server.retire` defers the close instead.
pub fn rejectStream(io: std.Io, stream: net.Stream, msg: []const u8) void {
    var buf: [256]u8 = undefined;
    var writer = stream.writer(io, &buf);
    const dummy = Protocol.Request{};
    Protocol.writeError(&writer.interface, &dummy, Protocol.ErrorCode.INTERNAL_ERROR, msg) catch return;
    writer.interface.flush() catch {};
    stream.shutdown(io, .send) catch {};
}

/// Joins both threads, closes the socket, frees the connection.
pub fn shutdownConn(srv: *Server, conn: *Conn) void {
    conn.closing.store(true, .release);
    // Wake the writer in case it is waiting on an empty queue.
    conn.out_mutex.lockUncancelable(srv.io);
    conn.out_cond.signal(srv.io);
    conn.out_mutex.unlock(srv.io);
    // Shutting down the socket unblocks a reader stuck in a blocking recv
    // (a plain close does not reliably wake it).
    conn.stream.shutdown(conn.io, .both) catch {};
    if (conn.reader_thread) |t| t.join();
    if (conn.writer_thread) |t| t.join();
    conn.stream.close(conn.io);
    conn.out_mutex.lockUncancelable(srv.io);
    for (conn.out_queue.items) |item| srv.allocator.free(item.line);
    conn.out_queue.deinit(srv.allocator);
    conn.out_bytes = 0;
    conn.out_mutex.unlock(srv.io);
    srv.allocator.destroy(conn);
}

pub fn reapClosed(srv: *Server) void {
    srv.conns_mutex.lockUncancelable(srv.io);
    defer srv.conns_mutex.unlock(srv.io);
    for (&srv.conns) |*slot| {
        if (slot.*) |conn| {
            if (conn.closing.load(.acquire)) {
                shutdownConn(srv, conn);
                slot.* = null;
            }
        }
    }
}
