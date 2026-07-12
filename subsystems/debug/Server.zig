//! Remote Debug Server — TCP listener that serves JSON-RPC 2.0 requests backed
//! by the Runtime Introspection Layer.
//!
//! ## Threading model — the main-thread pump
//!
//! Requests are parsed off the socket on per-connection reader threads, but they
//! are *executed* on the host's main thread, once per frame, in `pump`:
//!
//! ```
//! [conn reader thread] -- line --> [inbound queue] -- drained by --> [pump]
//! [conn writer thread] <- bytes -- [per-conn outbound queue] <-------/ (main)
//! ```
//!
//! Because reads (`Handler.dispatch`) and writes (`MutationApplier.applyFn`) all
//! run on the main thread against live engine state, no locking of engine data
//! is required — the single-threaded execution is the synchronisation. Reads
//! gain at most one frame of latency, which is acceptable for a debugger.
//!
//! Multi-client falls out for free (N reader threads feed one inbound queue) and
//! events (`emit`) are fanned out to subscribed connections' outbound queues.
//!
//! Usage (host side):
//!
//!   var srv = Server.init(allocator, .{ .allow_write = true });
//!   defer srv.deinit(io);
//!   try srv.start(io);
//!   // ... each frame, on the main thread ...
//!   srv.pump(world, applier);
//!   srv.stop(io);

const std = @import("std");
const engine = @import("engine");
const Protocol = @import("Protocol.zig");
const Handler = @import("Handler.zig");
const introspect = engine.introspect;
const net = std.Io.net;
const log = std.log.scoped(.debug_server);

const World = introspect.World;

// ── WorldProvider callback (legacy convenience) ──────────────────────────────

/// Callback the host can register to supply world state. Retained for
/// compatibility; the pump model takes the `World` directly in `pump`.
pub const WorldProvider = struct {
    ctx: ?*anyopaque,
    getFn: *const fn (ctx: ?*anyopaque) World,

    pub fn get(self: WorldProvider) World {
        return self.getFn(self.ctx);
    }
};

// ── Mutation contract (host-applied, main thread) ────────────────────────────

/// A validated, ready-to-apply mutation produced from a JSON-RPC request.
/// The host's `MutationApplier` turns it into real engine state changes.
pub const Mutation = union(enum) {
    set_component: struct { entity: []const u8, component: []const u8, field: []const u8, value: introspect.Value },
    set_transform: struct { entity: []const u8, channel: []const u8, value: [3]f32 },
    spawn: struct { name: []const u8 },
    destroy: struct { entity: []const u8 },
    reload_asset: struct { guid: []const u8 },
    // Machine-driven UI interaction (Studio only — the applier is a no-op for
    // the shipped game): synthesizes real dvui input events so any Studio
    // state (open a document, select a node, open a dropdown) is reachable
    // from an external tool/script, not just a fixed startup capture.
    input_mouse_move: struct { x: f32, y: f32 },
    /// Combined move + press + release at (x, y) — the common case. `button`
    /// is "left"/"right"/"middle".
    input_click: struct { x: f32, y: f32, button: []const u8 },
    input_key: struct { code: []const u8, down: bool },
    input_text: struct { text: []const u8 },
    /// Schedules a whole-window screenshot on the next frame (see
    /// `Screenshots.captureWindow`); poll the `screenshot.last` query for the
    /// resulting path.
    capture_window: struct {},
};

pub const MutationResult = struct {
    ok: bool,
    message: []const u8 = "",
};

/// Host-supplied applier. `applyFn` runs on the main thread inside `pump`, so it
/// may touch live engine state directly (route through undo commands, the scene
/// manager, etc.).
pub const MutationApplier = struct {
    ctx: ?*anyopaque,
    applyFn: *const fn (ctx: ?*anyopaque, m: Mutation) MutationResult,

    pub fn apply(self: MutationApplier, m: Mutation) MutationResult {
        return self.applyFn(self.ctx, m);
    }
};

// ── Options ──────────────────────────────────────────────────────────────────

pub const Options = struct {
    /// Port to listen on (default 7777).
    port: u16 = Protocol.DEFAULT_PORT,
    /// When true (default), bind to 127.0.0.1 so only local processes connect.
    ///
    /// When false the server binds `0.0.0.0` and is reachable from the network.
    /// There is **no TLS** — traffic (including any `auth_token`) is sent in the
    /// clear and the token is the only gate. Enable this only on a trusted LAN,
    /// behind a firewall/VPN; never expose it to the public internet.
    localhost_only: bool = true,
    /// Optional shared-secret token. Empty string disables authentication.
    auth_token: []const u8 = "",
    /// Server-wide read-write gate. When false, all mutating methods return
    /// READONLY regardless of the client.
    allow_write: bool = false,
    /// Maximum simultaneous clients (capped at MAX_CONNS).
    max_clients: usize = 8,
    /// Per-connection request rate limit (requests/second). 0 = unlimited.
    rate_limit_per_sec: u32 = 0,
    /// Max queued outbound lines per connection before the oldest *events* are
    /// dropped (a non-reading subscriber must not grow the server without bound).
    max_outbound_queue: usize = 2048,
    /// Max queued outbound bytes per connection before the oldest events are
    /// dropped (16 MiB). A single in-flight response larger than this is still
    /// allowed; the cap only bounds an accumulating backlog.
    max_outbound_bytes: usize = 16 * 1024 * 1024,
    /// Max pending inbound requests (across all connections) before new request
    /// lines are rejected with a clear "server busy" error.
    max_inbound_queue: usize = 4096,
};

/// Hard cap on simultaneous connections (sizes the connection slot array).
pub const MAX_CONNS = 16;

// ── Per-connection state ─────────────────────────────────────────────────────

/// One queued outbound line. `is_event` marks lossy notifications, which the
/// outbound-cap policy may drop under backpressure; responses are never dropped.
const OutItem = struct { line: []u8, is_event: bool };
const OutQueue = std.ArrayList(OutItem);

const Conn = struct {
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

    /// Subscription bitset over `introspect.Event` ordinals (Phase 3).
    subs: u32 = 0,

    /// Token-bucket rate limiter (Phase 4).
    tokens: f64 = 0,
    last_refill_ns: i128 = 0,

    fn pushOut(self: *Conn, line: []u8, is_event: bool) void {
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

const InboundNode = struct { conn: *Conn, line: []u8 };

// ── Server ───────────────────────────────────────────────────────────────────

pub const Server = struct {
    allocator: std.mem.Allocator,
    options: Options,
    io: std.Io = undefined,

    accept_thread: ?std.Thread = null,
    stop_flag: std.atomic.Value(bool) = .{ .raw = false },

    listener: ?net.Server = null,
    listener_mutex: std.Io.Mutex = .init,

    conns: [MAX_CONNS]?*Conn = .{null} ** MAX_CONNS,
    conns_mutex: std.Io.Mutex = .init,
    next_id: std.atomic.Value(u32) = .{ .raw = 1 },

    inbound: std.ArrayList(InboundNode) = .empty,
    inbound_mutex: std.Io.Mutex = .init,

    const Self = @This();

    pub fn init(allocator: std.mem.Allocator, options: Options) Server {
        return .{ .allocator = allocator, .options = options };
    }

    pub fn deinit(self: *Self, io: std.Io) void {
        self.stop(io);
    }

    /// Spawns the background accept loop. Requests are not executed until the
    /// host calls `pump` each frame.
    pub fn start(self: *Self, io: std.Io) !void {
        if (self.accept_thread != null) return error.AlreadyRunning;
        self.io = io;
        self.stop_flag.store(false, .release);
        self.accept_thread = try std.Thread.spawn(.{}, acceptLoop, .{self});
    }

    /// Legacy entry point: the provider is ignored (the pump takes the world
    /// directly), but the signature is kept so existing call sites compile.
    pub fn startWithProvider(self: *Self, io: std.Io, provider: WorldProvider) !void {
        _ = provider;
        return self.start(io);
    }

    /// Signals all threads to exit and joins them. Safe to call from the main
    /// thread (the same thread that calls `pump`).
    pub fn stop(self: *Self, io: std.Io) void {
        if (self.accept_thread == null) return;
        self.stop_flag.store(true, .release);

        // Wake a blocked `accept()` with a throwaway self-connection. On Linux,
        // closing/shutting down the listening socket from another thread does
        // NOT reliably unblock a thread already blocked in accept(); making a
        // local connection does — the accept returns, then the loop sees
        // stop_flag and exits.
        //
        // Skip this when the listener never came up (e.g. `listen()` failed):
        // the accept loop has already returned, so there's nothing to wake, and
        // the connect attempt would just fail — noisily so on some platforms
        // (Wine's ws2_32 rejects a socket option std sets and dumps a trace).
        self.listener_mutex.lockUncancelable(io);
        const has_listener = self.listener != null;
        self.listener_mutex.unlock(io);
        if (has_listener) {
            const addr: net.IpAddress = .{ .ip4 = net.Ip4Address.loopback(self.options.port) };
            if (net.IpAddress.connect(&addr, io, .{ .mode = .stream })) |s| {
                s.close(io);
            } else |_| {}
        }

        if (self.accept_thread) |t| {
            t.join();
            self.accept_thread = null;
        }

        // Accept loop has exited; release the listening socket.
        self.listener_mutex.lockUncancelable(io);
        if (self.listener) |*l| l.deinit(io);
        self.listener = null;
        self.listener_mutex.unlock(io);

        // Tear down every live connection.
        self.conns_mutex.lockUncancelable(io);
        for (&self.conns) |*slot| {
            if (slot.*) |conn| {
                self.shutdownConn(conn);
                slot.* = null;
            }
        }
        self.conns_mutex.unlock(io);

        // Drain any unprocessed inbound lines.
        self.inbound_mutex.lockUncancelable(io);
        for (self.inbound.items) |node| self.allocator.free(node.line);
        self.inbound.deinit(self.allocator);
        self.inbound = .empty;
        self.inbound_mutex.unlock(io);
    }

    pub fn isRunning(self: *const Self) bool {
        return self.accept_thread != null;
    }

    // ── Per-frame pump (main thread) ─────────────────────────────────────────

    /// Drains the inbound queue and executes each request against `world`.
    /// Mutating methods are routed through `applier` (if present); everything
    /// else is a read handled by `Handler.dispatch`. Call once per frame.
    pub fn pump(self: *Self, world: World, applier: ?MutationApplier) void {
        // Drain inbound under lock, then process without holding it.
        self.inbound_mutex.lockUncancelable(self.io);
        const batch = self.inbound.toOwnedSlice(self.allocator) catch {
            self.inbound_mutex.unlock(self.io);
            return;
        };
        self.inbound_mutex.unlock(self.io);
        defer self.allocator.free(batch);

        for (batch) |node| {
            self.process(node.conn, node.line, world, applier);
            self.allocator.free(node.line);
        }

        // Reap connections whose reader has finished.
        self.reapClosed();
    }

    /// Serialises a JSON-RPC notification and fans it out to every connection
    /// subscribed to `event` (by `introspect.Event` ordinal). `params_json` is
    /// a complete JSON value (object/array/scalar).
    pub fn emit(self: *Self, event: introspect.Event, params_json: []const u8) void {
        const bit = @as(u32, 1) << @intCast(@intFromEnum(event));
        var line: std.Io.Writer.Allocating = .init(self.allocator);
        defer line.deinit();
        Protocol.writeNotification(&line.writer, event.method(), params_json) catch return;

        self.conns_mutex.lockUncancelable(self.io);
        defer self.conns_mutex.unlock(self.io);
        for (self.conns) |slot| {
            const conn = slot orelse continue;
            if (conn.closing.load(.acquire)) continue;
            if (conn.subs & bit == 0) continue;
            const owned = self.allocator.dupe(u8, line.written()) catch continue;
            conn.pushOut(owned, true);
        }
    }

    // ── Request execution ────────────────────────────────────────────────────

    fn process(self: *Self, conn: *Conn, line: []u8, world: World, applier: ?MutationApplier) void {
        if (conn.closing.load(.acquire)) return;

        var out: std.Io.Writer.Allocating = .init(self.allocator);
        defer out.deinit();
        const w = &out.writer;

        const req = Protocol.Request.parse(line) catch |err| {
            const code = switch (err) {
                error.ParseError => Protocol.ErrorCode.PARSE_ERROR,
                error.InvalidRequest => Protocol.ErrorCode.INVALID_REQUEST,
            };
            const dummy = Protocol.Request{};
            Protocol.writeError(w, &dummy, code, @errorName(err)) catch {};
            self.sendOut(conn, out.written());
            return;
        };

        // Authentication gate.
        if (!conn.authenticated) {
            if (!std.mem.eql(u8, req.method(), "auth")) {
                Protocol.writeError(w, &req, Protocol.ErrorCode.INVALID_REQUEST, "Authentication required") catch {};
            } else {
                var tok_buf: [256]u8 = undefined;
                const tok = paramString(req.params(), "token", &tok_buf) orelse "";
                if (std.mem.eql(u8, tok, self.options.auth_token)) {
                    conn.authenticated = true;
                    Protocol.writeSuccess(w, &req, "\"ok\"") catch {};
                } else {
                    Protocol.writeError(w, &req, Protocol.ErrorCode.INVALID_REQUEST, "Bad token") catch {};
                }
            }
            self.sendOut(conn, out.written());
            return;
        }

        const m = req.method();

        // Rate limit (Phase 4).
        if (self.options.rate_limit_per_sec > 0 and !self.allowRequest(conn)) {
            Protocol.writeError(w, &req, Protocol.ErrorCode.RATE_LIMITED, "Rate limit exceeded") catch {};
            self.sendOut(conn, out.written());
            return;
        }

        // Session control: a client may drop its own write rights (Phase 4).
        if (std.mem.eql(u8, m, "session.readonly")) {
            conn.readonly = true;
            Protocol.writeSuccess(w, &req, "\"ok\"") catch {};
            self.sendOut(conn, out.written());
            return;
        }

        // Subscriptions (Phase 3).
        if (std.mem.eql(u8, m, "subscribe") or std.mem.eql(u8, m, "unsubscribe")) {
            self.handleSubscription(conn, &req, w);
            self.sendOut(conn, out.written());
            return;
        }

        // Mutating methods.
        if (Handler.isMutation(m)) {
            self.processMutation(conn, &req, w, applier);
            self.sendOut(conn, out.written());
            return;
        }

        // Reads.
        Handler.dispatch(self.allocator, &req, world, w);
        self.sendOut(conn, out.written());
    }

    fn processMutation(self: *Self, conn: *Conn, req: *const Protocol.Request, w: *std.Io.Writer, applier: ?MutationApplier) void {
        if (!self.options.allow_write or conn.readonly) {
            Protocol.writeError(w, req, Protocol.ErrorCode.READONLY, "Mutation requires a read-write session; start the server with --rw") catch {};
            return;
        }
        const ap = applier orelse {
            Protocol.writeError(w, req, Protocol.ErrorCode.INTERNAL_ERROR, "No mutation applier registered") catch {};
            return;
        };

        var arena = std.heap.ArenaAllocator.init(self.allocator);
        defer arena.deinit();
        const mut = Handler.buildMutation(arena.allocator(), req) catch {
            Protocol.writeError(w, req, Protocol.ErrorCode.INVALID_PARAMS, "Invalid mutation parameters") catch {};
            return;
        };

        const res = ap.apply(mut);
        if (res.ok) self.emitMutationEvent(mut);
        self.writeMutationResult(w, req, res);
    }

    /// Fans out the runtime event that corresponds to a just-applied mutation
    /// (entity create/destroy, asset reload). `mut`'s strings are still valid
    /// here (the caller frees the arena after this returns).
    fn emitMutationEvent(self: *Self, mut: Mutation) void {
        var buf: [Protocol.MAX_MESSAGE_BYTES]u8 = undefined;
        switch (mut) {
            .spawn => |sp| {
                const p = std.fmt.bufPrint(&buf, "{{\"name\":\"{s}\"}}", .{sp.name}) catch return;
                self.emit(.entity_created, p);
            },
            .destroy => |d| {
                const p = std.fmt.bufPrint(&buf, "{{\"entity\":\"{s}\"}}", .{d.entity}) catch return;
                self.emit(.entity_destroyed, p);
            },
            .reload_asset => |r| {
                const p = std.fmt.bufPrint(&buf, "{{\"guid\":\"{s}\"}}", .{r.guid}) catch return;
                self.emit(.resource_reloaded, p);
            },
            else => {},
        }
    }

    fn writeMutationResult(self: *Self, w: *std.Io.Writer, req: *const Protocol.Request, res: MutationResult) void {
        var buf: std.Io.Writer.Allocating = .init(self.allocator);
        defer buf.deinit();
        var jw = std.json.Stringify{ .writer = &buf.writer, .options = .{} };
        jw.beginObject() catch return;
        jw.objectField("ok") catch return;
        jw.write(res.ok) catch return;
        jw.objectField("message") catch return;
        jw.write(res.message) catch return;
        jw.endObject() catch return;
        if (res.ok)
            Protocol.writeSuccess(w, req, buf.written()) catch {}
        else
            Protocol.writeError(w, req, Protocol.ErrorCode.INTERNAL_ERROR, if (res.message.len > 0) res.message else "Mutation failed") catch {};
    }

    fn handleSubscription(self: *Self, conn: *Conn, req: *const Protocol.Request, w: *std.Io.Writer) void {
        _ = self;
        const subscribe = std.mem.eql(u8, req.method(), "subscribe");
        var name_buf: [64]u8 = undefined;
        const ev_name = paramString(req.params(), "event", &name_buf) orelse "";
        if (std.mem.eql(u8, ev_name, "*")) {
            // All events.
            conn.subs = if (subscribe) ~@as(u32, 0) else 0;
            Protocol.writeSuccess(w, req, "\"ok\"") catch {};
            return;
        }
        const ev = introspect.Event.fromMethod(ev_name) orelse {
            Protocol.writeError(w, req, Protocol.ErrorCode.INVALID_PARAMS, "Unknown event name") catch {};
            return;
        };
        const bit = @as(u32, 1) << @intCast(@intFromEnum(ev));
        if (subscribe) conn.subs |= bit else conn.subs &= ~bit;
        Protocol.writeSuccess(w, req, "\"ok\"") catch {};
    }

    /// Token-bucket check: refill based on elapsed monotonic time, consume one.
    fn allowRequest(self: *Self, conn: *Conn) bool {
        const rate: f64 = @floatFromInt(self.options.rate_limit_per_sec);
        const now: i128 = std.Io.Timestamp.now(self.io, .awake).nanoseconds;
        if (conn.last_refill_ns == 0) {
            conn.last_refill_ns = now;
            conn.tokens = rate;
        }
        const elapsed_s: f64 = @as(f64, @floatFromInt(now - conn.last_refill_ns)) / @as(f64, std.time.ns_per_s);
        conn.last_refill_ns = now;
        conn.tokens = @min(rate, conn.tokens + elapsed_s * rate);
        if (conn.tokens < 1.0) return false;
        conn.tokens -= 1.0;
        return true;
    }

    fn sendOut(self: *Self, conn: *Conn, bytes: []const u8) void {
        if (bytes.len == 0) return;
        const owned = self.allocator.dupe(u8, bytes) catch return;
        conn.pushOut(owned, false);
    }

    /// Enqueues a standalone JSON-RPC error (id = null) to a connection. Used for
    /// transport-level failures the request pump never sees — an oversized
    /// request line, or a full inbound queue.
    fn enqueueError(self: *Self, conn: *Conn, code: i32, msg: []const u8) void {
        var line: std.Io.Writer.Allocating = .init(self.allocator);
        defer line.deinit();
        const dummy = Protocol.Request{};
        Protocol.writeError(&line.writer, &dummy, code, msg) catch return;
        const owned = self.allocator.dupe(u8, line.written()) catch return;
        conn.pushOut(owned, false);
    }

    // ── Connection lifecycle ─────────────────────────────────────────────────

    fn pushInbound(self: *Self, conn: *Conn, line: []u8) void {
        self.inbound_mutex.lockUncancelable(self.io);
        const full = self.inbound.items.len >= self.options.max_inbound_queue;
        if (full) {
            self.inbound_mutex.unlock(self.io);
            self.allocator.free(line);
            // Reject loudly rather than silently dropping the request: the client
            // gets a clear "server busy" error it can back off on.
            self.enqueueError(conn, Protocol.ErrorCode.INTERNAL_ERROR, "Server busy: inbound queue full");
            return;
        }
        defer self.inbound_mutex.unlock(self.io);
        self.inbound.append(self.allocator, .{ .conn = conn, .line = line }) catch {
            self.allocator.free(line);
        };
    }

    fn reapClosed(self: *Self) void {
        self.conns_mutex.lockUncancelable(self.io);
        defer self.conns_mutex.unlock(self.io);
        for (&self.conns) |*slot| {
            if (slot.*) |conn| {
                if (conn.closing.load(.acquire)) {
                    self.shutdownConn(conn);
                    slot.* = null;
                }
            }
        }
    }

    /// Joins both threads, closes the socket, frees the connection.
    fn shutdownConn(self: *Self, conn: *Conn) void {
        conn.closing.store(true, .release);
        // Wake the writer in case it is waiting on an empty queue.
        conn.out_mutex.lockUncancelable(self.io);
        conn.out_cond.signal(self.io);
        conn.out_mutex.unlock(self.io);
        // Shutting down the socket unblocks a reader stuck in a blocking recv
        // (a plain close does not reliably wake it).
        conn.stream.shutdown(conn.io, .both) catch {};
        if (conn.reader_thread) |t| t.join();
        if (conn.writer_thread) |t| t.join();
        conn.stream.close(conn.io);
        conn.out_mutex.lockUncancelable(self.io);
        for (conn.out_queue.items) |item| self.allocator.free(item.line);
        conn.out_queue.deinit(self.allocator);
        conn.out_bytes = 0;
        conn.out_mutex.unlock(self.io);
        self.allocator.destroy(conn);
    }

    fn countConns(self: *Self) usize {
        var n: usize = 0;
        for (self.conns) |c| {
            if (c != null) n += 1;
        }
        return n;
    }

    fn onAccept(self: *Self, stream: net.Stream) void {
        self.conns_mutex.lockUncancelable(self.io);
        defer self.conns_mutex.unlock(self.io);

        const cap = @min(self.options.max_clients, MAX_CONNS);
        if (self.countConns() >= cap) {
            rejectStream(self.io, stream, "Too many clients");
            stream.close(self.io);
            return;
        }
        var slot_idx: ?usize = null;
        for (&self.conns, 0..) |*slot, i| {
            if (slot.* == null) {
                slot_idx = i;
                break;
            }
        }
        const idx = slot_idx orelse {
            rejectStream(self.io, stream, "Too many clients");
            stream.close(self.io);
            return;
        };

        const conn = self.allocator.create(Conn) catch {
            stream.close(self.io);
            return;
        };
        conn.* = .{
            .server = self,
            .io = self.io,
            .id = self.next_id.fetchAdd(1, .monotonic),
            .stream = stream,
            .authenticated = self.options.auth_token.len == 0,
        };
        conn.reader_thread = std.Thread.spawn(.{}, connReader, .{conn}) catch {
            stream.close(self.io);
            self.allocator.destroy(conn);
            return;
        };
        conn.writer_thread = std.Thread.spawn(.{}, connWriter, .{conn}) catch {
            // Reader already running; mark closing so it unwinds, then bail.
            conn.closing.store(true, .release);
            conn.stream.close(self.io);
            if (conn.reader_thread) |t| t.join();
            conn.out_queue.deinit(self.allocator);
            self.allocator.destroy(conn);
            return;
        };
        self.conns[idx] = conn;
    }
};

// ── Background threads ───────────────────────────────────────────────────────

fn acceptLoop(srv: *Server) void {
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

fn connReader(conn: *Conn) void {
    var reader = conn.stream.reader(conn.io, &conn.read_buf);
    const alloc = conn.server.allocator;
    while (!conn.closing.load(.acquire)) {
        // Heap-growing read: a big `component.set` value can exceed the 64 KiB
        // buffer. readLine owns the returned line, which pushInbound takes over.
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

fn connWriter(conn: *Conn) void {
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

fn rejectStream(io: std.Io, stream: net.Stream, msg: []const u8) void {
    var buf: [256]u8 = undefined;
    var writer = stream.writer(io, &buf);
    const dummy = Protocol.Request{};
    Protocol.writeError(&writer.interface, &dummy, Protocol.ErrorCode.INTERNAL_ERROR, msg) catch return;
    writer.interface.flush() catch {};
}

fn paramString(params_json: []const u8, key: []const u8, dst: []u8) ?[]u8 {
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

// ── Tests ────────────────────────────────────────────────────────────────────

const testing = std.testing;

fn sleepMs(io: std.Io, ms: i64) void {
    io.sleep(std.Io.Duration.fromMilliseconds(ms), .awake) catch {};
}

/// Background pump loop used by the socket integration tests.
const TestPumper = struct {
    srv: *Server,
    io: std.Io,
    world: World = .{},
    applier: ?MutationApplier = null,
    stop: std.atomic.Value(bool) = .{ .raw = false },

    fn run(self: *TestPumper) void {
        while (!self.stop.load(.acquire)) {
            self.srv.pump(self.world, self.applier);
            sleepMs(self.io, 2);
        }
    }
};

test "server: connect, write mutation, applier records it via pump" {
    const Threaded = std.Io.Threaded;
    var threaded: Threaded = .init(testing.allocator, .{});
    defer threaded.deinit();
    const io = threaded.io();

    var srv = Server.init(testing.allocator, .{ .port = 39117, .allow_write = true });
    try srv.start(io);
    defer srv.deinit(io);
    sleepMs(io, 50); // let the accept loop bind

    // The applier copies what it needs during the call: a Mutation's strings are
    // arena-owned and only valid for the duration of applyFn.
    const Recorded = struct {
        is_set_component: bool = false,
        entity_buf: [64]u8 = undefined,
        entity_len: usize = 0,
        fn entity(self: *const @This()) []const u8 {
            return self.entity_buf[0..self.entity_len];
        }
    };
    var recorded: Recorded = .{};
    const Recorder = struct {
        fn apply(ctx: ?*anyopaque, m: Mutation) MutationResult {
            const r: *Recorded = @ptrCast(@alignCast(ctx.?));
            if (m == .set_component) {
                r.is_set_component = true;
                const e = m.set_component.entity;
                const n = @min(e.len, r.entity_buf.len);
                @memcpy(r.entity_buf[0..n], e[0..n]);
                r.entity_len = n;
            }
            return .{ .ok = true, .message = "recorded" };
        }
    };
    const applier = MutationApplier{ .ctx = &recorded, .applyFn = Recorder.apply };

    var client = @import("Client.zig").Client.connect(io, "127.0.0.1", 39117) catch
        return error.SkipZigTest; // sockets unavailable (sandbox)
    defer client.close();

    var pumper = TestPumper{ .srv = &srv, .io = io, .applier = applier };
    const pump_thread = try std.Thread.spawn(.{}, TestPumper.run, .{&pumper});
    defer {
        pumper.stop.store(true, .release);
        pump_thread.join();
    }

    const resp = try client.call(testing.allocator, "component.set",
        \\{"entity":"Player","component":"Light","field":"intensity","value":2.5}
    );
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "\"ok\":true") != null);
    try testing.expect(recorded.is_set_component);
    try testing.expectEqualStrings("Player", recorded.entity());
}

test "server: read-only server rejects mutation" {
    const Threaded = std.Io.Threaded;
    var threaded: Threaded = .init(testing.allocator, .{});
    defer threaded.deinit();
    const io = threaded.io();

    var srv = Server.init(testing.allocator, .{ .port = 39118, .allow_write = false });
    try srv.start(io);
    defer srv.deinit(io);
    sleepMs(io, 50);

    var client = @import("Client.zig").Client.connect(io, "127.0.0.1", 39118) catch
        return error.SkipZigTest;
    defer client.close();

    var pumper = TestPumper{ .srv = &srv, .io = io };
    const pump_thread = try std.Thread.spawn(.{}, TestPumper.run, .{&pumper});
    defer {
        pumper.stop.store(true, .release);
        pump_thread.join();
    }

    const resp = try client.call(testing.allocator, "component.set",
        \\{"entity":"Player","component":"Light","field":"intensity","value":2.5}
    );
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "-32001") != null); // READONLY
}

test "server: subscribe then spawn delivers an entity.created notification" {
    const Threaded = std.Io.Threaded;
    var threaded: Threaded = .init(testing.allocator, .{});
    defer threaded.deinit();
    const io = threaded.io();

    var srv = Server.init(testing.allocator, .{ .port = 39119, .allow_write = true });
    try srv.start(io);
    defer srv.deinit(io);
    sleepMs(io, 50);

    const okApplier = struct {
        fn apply(_: ?*anyopaque, _: Mutation) MutationResult {
            return .{ .ok = true, .message = "ok" };
        }
    };
    const applier = MutationApplier{ .ctx = null, .applyFn = okApplier.apply };

    var client = @import("Client.zig").Client.connect(io, "127.0.0.1", 39119) catch
        return error.SkipZigTest;
    defer client.close();

    var pumper = TestPumper{ .srv = &srv, .io = io, .applier = applier };
    const pump_thread = try std.Thread.spawn(.{}, TestPumper.run, .{&pumper});
    defer {
        pumper.stop.store(true, .release);
        pump_thread.join();
    }

    // Subscribe, then spawn. The notification is enqueued before the response
    // (emit runs before the mutation reply), so the first line the client reads
    // after the spawn request is the `entity.created` notification. Reading a
    // single line keeps the test from blocking if no notification is delivered.
    const sub = try client.call(testing.allocator, "subscribe",
        \\{"event":"entity.created"}
    );
    testing.allocator.free(sub);

    const first = try client.call(testing.allocator, "entity.spawn",
        \\{"name":"Box"}
    );
    defer testing.allocator.free(first);
    try testing.expect(std.mem.indexOf(u8, first, "entity.created") != null);
}

test "server: rate limiter rejects rapid requests over the budget" {
    const Threaded = std.Io.Threaded;
    var threaded: Threaded = .init(testing.allocator, .{});
    defer threaded.deinit();
    const io = threaded.io();

    var srv = Server.init(testing.allocator, .{ .port = 39120, .rate_limit_per_sec = 1 });
    try srv.start(io);
    defer srv.deinit(io);
    sleepMs(io, 50);

    var client = @import("Client.zig").Client.connect(io, "127.0.0.1", 39120) catch
        return error.SkipZigTest;
    defer client.close();

    var pumper = TestPumper{ .srv = &srv, .io = io };
    const pump_thread = try std.Thread.spawn(.{}, TestPumper.run, .{&pumper});
    defer {
        pumper.stop.store(true, .release);
        pump_thread.join();
    }

    // With a 1 req/s budget, several back-to-back pings overflow the bucket.
    var limited = false;
    for (0..5) |_| {
        const resp = try client.call(testing.allocator, "ping", null);
        defer testing.allocator.free(resp);
        if (std.mem.indexOf(u8, resp, "-32002") != null) limited = true;
    }
    try testing.expect(limited);
}

test "server: a snapshot larger than 64 KiB round-trips to the client intact (H1)" {
    const Threaded = std.Io.Threaded;
    var threaded: Threaded = .init(testing.allocator, .{});
    defer threaded.deinit();
    const io = threaded.io();

    var srv = Server.init(testing.allocator, .{ .port = 39121 });
    try srv.start(io);
    defer srv.deinit(io);
    sleepMs(io, 50);

    // Build a synthetic scene whose `snapshot` serialises well past 64 KiB:
    // many entities, each with a long name, force a multi-buffer response.
    const count = 600;
    const nodes = try testing.allocator.alloc(engine.SceneNode, count);
    defer testing.allocator.free(nodes);
    for (nodes, 0..) |*n, i| {
        n.* = .{};
        var nbuf: [64]u8 = undefined;
        const name = std.fmt.bufPrint(&nbuf, "Entity_{d:0>5}_with_a_deliberately_long_descriptive_name", .{i}) catch "Entity";
        n.setName(name);
    }
    var view = [_]introspect.SceneView{.{ .name = "Big", .id = "big-scene", .active = true, .nodes = nodes }};
    const world: World = .{ .scenes = view[0..1] };

    var client = @import("Client.zig").Client.connect(io, "127.0.0.1", 39121) catch
        return error.SkipZigTest;
    defer client.close();

    var pumper = TestPumper{ .srv = &srv, .io = io, .world = world };
    const pump_thread = try std.Thread.spawn(.{}, TestPumper.run, .{&pumper});
    defer {
        pumper.stop.store(true, .release);
        pump_thread.join();
    }

    const resp = try client.call(testing.allocator, "snapshot", null);
    defer testing.allocator.free(resp);
    // The whole response must arrive — not be truncated at the 64 KiB buffer.
    try testing.expect(resp.len > Protocol.MAX_MESSAGE_BYTES);
    try testing.expect(std.mem.indexOf(u8, resp, "Entity_00000_") != null);
    try testing.expect(std.mem.indexOf(u8, resp, "Entity_00599_") != null);
    // And it must still be one well-formed JSON-RPC envelope.
    const parsed = try std.json.parseFromSlice(std.json.Value, testing.allocator, resp, .{});
    defer parsed.deinit();
    try testing.expect(parsed.value == .object);
    try testing.expect(parsed.value.object.get("result") != null);
}

test "server: flooding a non-reading subscriber stays bounded and responsive (H2)" {
    const Threaded = std.Io.Threaded;
    var threaded: Threaded = .init(testing.allocator, .{});
    defer threaded.deinit();
    const io = threaded.io();

    const cap = 16;
    var srv = Server.init(testing.allocator, .{
        .port = 39122,
        .max_outbound_queue = cap,
        .max_outbound_bytes = 4096,
    });
    try srv.start(io);
    defer srv.deinit(io);
    sleepMs(io, 50);

    var pumper = TestPumper{ .srv = &srv, .io = io };
    const pump_thread = try std.Thread.spawn(.{}, TestPumper.run, .{&pumper});
    defer {
        pumper.stop.store(true, .release);
        pump_thread.join();
    }

    // Client A subscribes to everything, then stops reading entirely.
    var slow = @import("Client.zig").Client.connect(io, "127.0.0.1", 39122) catch
        return error.SkipZigTest;
    defer slow.close();
    const sub = try slow.call(testing.allocator, "subscribe", "{\"event\":\"*\"}");
    testing.allocator.free(sub);

    // Fire far more notifications than the cap. The slow client never drains,
    // so its outbound queue must be bounded by the cap (oldest events dropped).
    for (0..5000) |i| {
        var b: [48]u8 = undefined;
        const p = std.fmt.bufPrint(&b, "{{\"fps\":{d}}}", .{i}) catch "{}";
        srv.emit(.fps_changed, p);
    }

    var max_seen: usize = 0;
    srv.conns_mutex.lockUncancelable(io);
    for (srv.conns) |slot| {
        const conn = slot orelse continue;
        conn.out_mutex.lockUncancelable(io);
        max_seen = @max(max_seen, conn.out_queue.items.len);
        conn.out_mutex.unlock(io);
    }
    srv.conns_mutex.unlock(io);
    try testing.expect(max_seen <= cap);

    // A second, healthy client is still served promptly.
    var fast = @import("Client.zig").Client.connect(io, "127.0.0.1", 39122) catch
        return error.SkipZigTest;
    defer fast.close();
    const resp = try fast.call(testing.allocator, "ping", null);
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "\"error\"") == null);
    try testing.expect(resp.len > 0);
}

test "server: exceeding max_clients delivers a clean error, not a silent close (H5)" {
    const Threaded = std.Io.Threaded;
    var threaded: Threaded = .init(testing.allocator, .{});
    defer threaded.deinit();
    const io = threaded.io();

    var srv = Server.init(testing.allocator, .{ .port = 39123, .max_clients = 1 });
    try srv.start(io);
    defer srv.deinit(io);
    sleepMs(io, 50);

    var pumper = TestPumper{ .srv = &srv, .io = io };
    const pump_thread = try std.Thread.spawn(.{}, TestPumper.run, .{&pumper});
    defer {
        pumper.stop.store(true, .release);
        pump_thread.join();
    }

    var first = @import("Client.zig").Client.connect(io, "127.0.0.1", 39123) catch
        return error.SkipZigTest;
    defer first.close();
    // Make sure the first client is fully accepted and occupying the only slot.
    const ok = try first.call(testing.allocator, "ping", null);
    testing.allocator.free(ok);

    // The second client is rejected — it must receive a JSON-RPC error line.
    var second = @import("Client.zig").Client.connect(io, "127.0.0.1", 39123) catch
        return error.SkipZigTest;
    defer second.close();
    const resp = try second.call(testing.allocator, "ping", null);
    defer testing.allocator.free(resp);
    try testing.expect(std.mem.indexOf(u8, resp, "Too many clients") != null);
}
