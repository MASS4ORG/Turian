//! SceneManager — a formal scene-management API (issue #22).
//!
//! Where a Turian *scene* (like a Godot scene or a Unity prefab) can be
//! instantiated as a node tree, the SceneManager treats scenes the way Unity's
//! SceneManager does: named, addressable units that are **loaded** and
//! **unloaded** as a whole, can be **additive** (several active at once), can
//! carry **persistent** objects that survive transitions (DontDestroyOnLoad),
//! and that fire **lifecycle events** (loaded / unloaded / activated /
//! deactivated).
//!
//! ## Decoupling from scene parsing
//! The engine does not know how to turn a scene *asset id* into nodes — that
//! lives in the editor/runtime (JSON via `editor.scene_io`, bytes via the `.oap`
//! package). So the manager is handed a **`Loader`** callback (mirroring the
//! `software_renderer` source callbacks): given a scene id, fill a node buffer.
//! This keeps the manager pure engine logic and unit-testable with a fake
//! loader, while the generated game wires in the real package-backed loader.
//!
//! ## Storage
//! Each loaded scene owns an allocator-backed node array. Handles are
//! generational so a stale handle to an unloaded scene is detected rather than
//! aliasing a freshly loaded one.

const std = @import("std");
const SceneNode = @import("SceneNode.zig").SceneNode;
const MAX_OBJECTS = @import("SceneNode.zig").MAX_OBJECTS;

/// Maximum number of concurrently loaded scenes (including the persistent one).
pub const MAX_SCENES = 16;
/// Maximum number of event subscribers.
pub const MAX_SUBSCRIBERS = 16;
/// Maximum number of queued deferred scene requests per flush.
pub const MAX_REQUESTS = 32;
/// Length of a scene id (UUID string), matching `SceneNode` GUID storage.
pub const ID_LEN = 36;

/// How a `loadScene` call composes with already-loaded scenes.
pub const LoadMode = enum {
    /// Unload all non-persistent scenes first, then load. The classic
    /// "go to the next level" transition.
    single,
    /// Keep currently loaded scenes; add this one alongside them.
    additive,
};

/// Lifecycle state of a scene slot.
pub const LoadState = enum { unloaded, loading, loaded, failed };

/// Why a load failed. `none` while not failed.
pub const LoadError = enum {
    none,
    /// No loader is installed.
    no_loader,
    /// The loader reported the scene id could not be resolved or parsed.
    load_failed,
    /// No free scene slot was available.
    capacity,
};

/// Lifecycle events fired to subscribers.
pub const Event = enum { loaded, unloaded, activated, deactivated };

/// Errors returned by the synchronous load API.
pub const Error = error{ NoLoader, LoadFailed, OutOfMemory, TooManyScenes };

/// A generational reference to a loaded scene. Cheap to copy and compare.
pub const SceneHandle = struct {
    index: u16,
    generation: u16,

    pub fn eql(a: SceneHandle, b: SceneHandle) bool {
        return a.index == b.index and a.generation == b.generation;
    }
};

/// Resolve a scene `id` into nodes. Writes up to `out.len` nodes and sets
/// `out_count`. Returns false if the scene could not be found or parsed.
/// Runs on a worker thread for async loads, so it must not touch the manager.
pub const Loader = *const fn (ctx: ?*anyopaque, id: []const u8, out: []SceneNode, out_count: *usize) bool;

/// Notified when a scene's lifecycle changes. `ctx` is the pointer registered
/// with `subscribe`.
pub const EventCallback = *const fn (ctx: ?*anyopaque, mgr: *SceneManager, handle: SceneHandle, event: Event) void;

const Subscriber = struct {
    callback: EventCallback,
    ctx: ?*anyopaque,
};

/// A deferred scene operation requested by game code during a frame and applied
/// at a safe point via `flushRequests`. Deferring keeps scripts from mutating
/// scene storage while the host is mid-iteration over a scene's nodes.
const Request = union(enum) {
    load: struct { id_buf: [ID_LEN]u8, id_len: usize, mode: LoadMode },
    unload: SceneHandle,
};

/// One scene slot. Reused across loads; `generation` bumps on each reuse.
const Slot = struct {
    in_use: bool = false,
    generation: u16 = 0,
    id_buf: [ID_LEN]u8 = .{0} ** ID_LEN,
    id_len: usize = 0,
    state: LoadState = .unloaded,
    err: LoadError = .none,
    /// True for the persistent (DontDestroyOnLoad) scene; never auto-unloaded.
    persistent: bool = false,
    /// Owned node storage (capacity == MAX_OBJECTS while in use).
    nodes: []SceneNode = &.{},
    node_count: usize = 0,
    /// If this scene was pulled in as a dependency, the slot index of its parent.
    dep_of: ?u16 = null,
    /// Set true by the async worker when loading finishes (atomic hand-off).
    done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    /// Coarse load progress 0..1, updated by the worker.
    progress: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),

    fn idSlice(self: *const Slot) []const u8 {
        return self.id_buf[0..self.id_len];
    }

    fn setId(self: *Slot, id: []const u8) void {
        const n = @min(id.len, ID_LEN);
        @memcpy(self.id_buf[0..n], id[0..n]);
        self.id_len = n;
    }
};

pub const SceneManager = struct {
    allocator: std.mem.Allocator,
    loader: ?Loader = null,
    loader_ctx: ?*anyopaque = null,
    slots: [MAX_SCENES]Slot = [_]Slot{.{}} ** MAX_SCENES,
    /// Index of the active scene (where newly spawned objects conceptually live).
    active_index: ?u16 = null,
    /// Lazily-created persistent scene slot index.
    persistent_index: ?u16 = null,
    subscribers: [MAX_SUBSCRIBERS]Subscriber = undefined,
    subscriber_count: usize = 0,
    /// Per-slot futures for in-flight async loads.
    futures: [MAX_SCENES]std.Io.Future(void) = undefined,
    /// Deferred scene requests applied by `flushRequests`.
    requests: [MAX_REQUESTS]Request = undefined,
    request_count: usize = 0,

    // ── Lifecycle ───────────────────────────────────────────────────────────

    pub fn init(allocator: std.mem.Allocator) SceneManager {
        return .{ .allocator = allocator };
    }

    /// Install the scene loader used to resolve ids into nodes.
    pub fn setLoader(self: *SceneManager, loader: Loader, ctx: ?*anyopaque) void {
        self.loader = loader;
        self.loader_ctx = ctx;
    }

    pub fn deinit(self: *SceneManager) void {
        for (&self.slots) |*slot| {
            if (slot.nodes.len != 0) self.allocator.free(slot.nodes);
            slot.* = .{};
        }
    }

    // ── Subscriptions ───────────────────────────────────────────────────────

    /// Register a lifecycle-event callback. Silently ignored past capacity.
    pub fn subscribe(self: *SceneManager, callback: EventCallback, ctx: ?*anyopaque) void {
        if (self.subscriber_count >= MAX_SUBSCRIBERS) return;
        self.subscribers[self.subscriber_count] = .{ .callback = callback, .ctx = ctx };
        self.subscriber_count += 1;
    }

    fn fire(self: *SceneManager, handle: SceneHandle, event: Event) void {
        for (self.subscribers[0..self.subscriber_count]) |sub| {
            sub.callback(sub.ctx, self, handle, event);
        }
    }

    // ── Loading ─────────────────────────────────────────────────────────────

    /// Synchronously load a scene by `id`. Returns a handle on success or a
    /// typed error (with `getError(handle)` reporting the per-scene reason when
    /// a slot was allocated). In `.single` mode all non-persistent scenes are
    /// unloaded first.
    pub fn loadScene(self: *SceneManager, id: []const u8, mode: LoadMode) Error!SceneHandle {
        if (mode == .single) self.unloadAllNonPersistent();

        const handle = try self.acquireSlot(id);
        const slot = self.slotPtr(handle).?;
        slot.state = .loading;

        self.runLoad(handle);
        if (slot.state == .failed) {
            const e = slot.err;
            self.freeSlot(handle);
            return switch (e) {
                .no_loader => Error.NoLoader,
                .capacity => Error.TooManyScenes,
                else => Error.LoadFailed,
            };
        }

        self.fire(handle, .loaded);
        // The active scene is the most-recently single-loaded scene, or the
        // first scene loaded when none is active yet.
        if (mode == .single or self.active_index == null) self.setActiveInternal(handle);
        return handle;
    }

    /// Load `id` plus a set of dependency scene ids. Dependencies load additively
    /// first and are linked to the main scene, so unloading the main scene
    /// cascades to them (issue #22 — scene dependencies).
    pub fn loadSceneWithDeps(
        self: *SceneManager,
        id: []const u8,
        deps: []const []const u8,
        mode: LoadMode,
    ) Error!SceneHandle {
        if (mode == .single) self.unloadAllNonPersistent();

        const main = try self.loadScene(id, .additive);
        for (deps) |dep_id| {
            const dh = self.loadScene(dep_id, .additive) catch continue;
            if (self.slotPtr(dh)) |s| s.dep_of = main.index;
        }
        if (mode == .single or self.active_index == null) self.setActiveInternal(main);
        return main;
    }

    /// Begin loading a scene on a worker thread. Returns immediately with a
    /// handle in the `.loading` state. Poll `isReady(handle)` each frame, then
    /// call `finishAsync(io, handle)` to reap it (mirrors the studio task pump).
    /// Falls back to a synchronous load if concurrency is unavailable.
    pub fn loadSceneAsync(self: *SceneManager, io: std.Io, id: []const u8, mode: LoadMode) Error!SceneHandle {
        if (mode == .single) self.unloadAllNonPersistent();

        const handle = try self.acquireSlot(id);
        const slot = self.slotPtr(handle).?;
        slot.state = .loading;
        slot.done.store(false, .release);
        slot.progress.store(0, .release);

        self.futures[handle.index] = io.concurrent(asyncWorker, .{ self, handle }) catch {
            // No concurrency: load inline and mark done so finishAsync still works.
            self.runLoad(handle);
            slot.done.store(true, .release);
            return handle;
        };
        return handle;
    }

    /// True when an async load has finished (success or failure) and is ready to
    /// be reaped with `finishAsync`. Non-blocking.
    pub fn isReady(self: *SceneManager, handle: SceneHandle) bool {
        const slot = self.slotPtr(handle) orelse return true;
        return slot.done.load(.acquire);
    }

    /// Coarse async load progress in [0,1].
    pub fn loadProgress(self: *SceneManager, handle: SceneHandle) f32 {
        const slot = self.slotPtr(handle) orelse return 1;
        return @as(f32, @floatFromInt(slot.progress.load(.acquire))) / 1000.0;
    }

    /// Reap a finished async load: awaits the worker, fires events, and resolves
    /// the active scene. Returns the final state. Safe to call once `isReady`.
    pub fn finishAsync(self: *SceneManager, io: std.Io, handle: SceneHandle, mode: LoadMode) LoadState {
        const slot = self.slotPtr(handle) orelse return .unloaded;
        self.futures[handle.index].await(io);

        if (slot.state == .failed) {
            // Leave the failed slot in place so the caller can read getError;
            // they unload it explicitly.
            return .failed;
        }
        self.fire(handle, .loaded);
        if (mode == .single or self.active_index == null) self.setActiveInternal(handle);
        return slot.state;
    }

    /// Worker entry point. Runs the load and publishes completion atomically.
    fn asyncWorker(self: *SceneManager, handle: SceneHandle) void {
        self.runLoad(handle);
        if (self.slotPtr(handle)) |slot| {
            slot.progress.store(1000, .release);
            slot.done.store(true, .release);
        }
    }

    /// Core load step shared by sync and async paths. Fills the slot's nodes via
    /// the loader and sets state/err. Does not fire events or touch the active
    /// scene (callers do that on the owning thread).
    fn runLoad(self: *SceneManager, handle: SceneHandle) void {
        const slot = self.slotPtr(handle) orelse return;
        const loader = self.loader orelse {
            slot.state = .failed;
            slot.err = .no_loader;
            return;
        };
        var count: usize = 0;
        const ok = loader(self.loader_ctx, slot.idSlice(), slot.nodes[0..MAX_OBJECTS], &count);
        if (!ok) {
            slot.state = .failed;
            slot.err = .load_failed;
            slot.node_count = 0;
            return;
        }
        slot.node_count = @min(count, MAX_OBJECTS);
        slot.state = .loaded;
        slot.err = .none;
    }

    // ── Deferred requests ───────────────────────────────────────────────────

    /// Queue a scene load to be applied on the next `flushRequests`. Safe to call
    /// from game code mid-frame (e.g. an `update` hook); the actual load happens
    /// at a frame boundary so it never disturbs in-progress node iteration.
    pub fn requestLoad(self: *SceneManager, id: []const u8, mode: LoadMode) void {
        if (self.request_count >= MAX_REQUESTS) return;
        var req = Request{ .load = .{ .id_buf = undefined, .id_len = 0, .mode = mode } };
        const n = @min(id.len, ID_LEN);
        @memcpy(req.load.id_buf[0..n], id[0..n]);
        req.load.id_len = n;
        self.requests[self.request_count] = req;
        self.request_count += 1;
    }

    /// Queue a scene unload to be applied on the next `flushRequests`.
    pub fn requestUnload(self: *SceneManager, handle: SceneHandle) void {
        if (self.request_count >= MAX_REQUESTS) return;
        self.requests[self.request_count] = .{ .unload = handle };
        self.request_count += 1;
    }

    /// Apply all queued requests in order. Returns true if any request changed
    /// the set of loaded scenes (so the host can rebuild per-scene live state).
    pub fn flushRequests(self: *SceneManager) bool {
        if (self.request_count == 0) return false;
        var changed = false;
        for (self.requests[0..self.request_count]) |*req| {
            switch (req.*) {
                .load => |l| {
                    _ = self.loadScene(l.id_buf[0..l.id_len], l.mode) catch {};
                    changed = true;
                },
                .unload => |h| {
                    if (self.slotPtr(h) != null) {
                        self.unloadScene(h);
                        changed = true;
                    }
                },
            }
        }
        self.request_count = 0;
        return changed;
    }

    // ── Unloading ───────────────────────────────────────────────────────────

    /// Unload a scene and any scenes that were loaded as its dependencies.
    /// Fires `unloaded`. Persistent scenes are unloaded too if requested here.
    pub fn unloadScene(self: *SceneManager, handle: SceneHandle) void {
        const slot = self.slotPtr(handle) orelse return;

        // Cascade to dependencies first.
        var i: u16 = 0;
        while (i < MAX_SCENES) : (i += 1) {
            const s = &self.slots[i];
            if (s.in_use and s.dep_of != null and s.dep_of.? == handle.index) {
                self.unloadScene(.{ .index = i, .generation = s.generation });
            }
        }

        self.fire(handle, .unloaded);

        if (self.active_index != null and self.active_index.? == handle.index) {
            self.active_index = null;
            // Promote another loaded, non-persistent scene to active if any.
            var j: u16 = 0;
            while (j < MAX_SCENES) : (j += 1) {
                const s = &self.slots[j];
                if (s.in_use and j != handle.index and s.state == .loaded and !s.persistent) {
                    self.setActiveInternal(.{ .index = j, .generation = s.generation });
                    break;
                }
            }
        }
        _ = slot;
        self.freeSlot(handle);
    }

    /// Unload every loaded scene except the persistent one.
    pub fn unloadAllNonPersistent(self: *SceneManager) void {
        var i: u16 = 0;
        while (i < MAX_SCENES) : (i += 1) {
            const s = &self.slots[i];
            if (s.in_use and !s.persistent and s.dep_of == null) {
                self.unloadScene(.{ .index = i, .generation = s.generation });
            }
        }
        // Any remaining dependency scenes whose parents were unloaded above are
        // already gone via the cascade; unload stragglers (orphans) too.
        i = 0;
        while (i < MAX_SCENES) : (i += 1) {
            const s = &self.slots[i];
            if (s.in_use and !s.persistent) {
                self.unloadScene(.{ .index = i, .generation = s.generation });
            }
        }
    }

    // ── Active scene ────────────────────────────────────────────────────────

    /// Mark a loaded scene as persistent (or not). Persistent scenes survive
    /// `.single` loads and `unloadAllNonPersistent`, so a "bootstrap" scene that
    /// holds a long-lived controller/camera can outlive level transitions.
    pub fn setScenePersistent(self: *SceneManager, handle: SceneHandle, value: bool) void {
        if (self.slotPtr(handle)) |slot| slot.persistent = value;
    }

    /// Make `handle` the active scene, firing deactivated/activated events.
    pub fn setActiveScene(self: *SceneManager, handle: SceneHandle) void {
        if (self.slotPtr(handle) == null) return;
        self.setActiveInternal(handle);
    }

    fn setActiveInternal(self: *SceneManager, handle: SceneHandle) void {
        if (self.active_index) |old| {
            if (old == handle.index) return;
            const old_slot = &self.slots[old];
            if (old_slot.in_use) {
                self.fire(.{ .index = old, .generation = old_slot.generation }, .deactivated);
            }
        }
        self.active_index = handle.index;
        self.fire(handle, .activated);
    }

    /// The active scene, or null if none is loaded.
    pub fn getActiveScene(self: *SceneManager) ?SceneHandle {
        const idx = self.active_index orelse return null;
        const slot = &self.slots[idx];
        if (!slot.in_use) return null;
        return .{ .index = idx, .generation = slot.generation };
    }

    // ── Persistent objects (DontDestroyOnLoad) ──────────────────────────────

    /// Ensure the persistent scene exists and return its handle. The persistent
    /// scene survives `.single` loads and holds DontDestroyOnLoad objects.
    pub fn persistentScene(self: *SceneManager) Error!SceneHandle {
        if (self.persistent_index) |idx| {
            const slot = &self.slots[idx];
            return .{ .index = idx, .generation = slot.generation };
        }
        const handle = try self.acquireSlot("");
        const slot = self.slotPtr(handle).?;
        slot.persistent = true;
        slot.state = .loaded;
        slot.node_count = 0;
        self.persistent_index = handle.index;
        return handle;
    }

    /// Move node `node_index` of `from` into the persistent scene so it survives
    /// scene transitions (Unity's DontDestroyOnLoad). Returns false if the move
    /// could not be performed.
    pub fn markDontDestroyOnLoad(self: *SceneManager, from: SceneHandle, node_index: usize) bool {
        const src = self.slotPtr(from) orelse return false;
        if (node_index >= src.node_count) return false;

        const ph = self.persistentScene() catch return false;
        const dst = self.slotPtr(ph).?;
        if (dst.node_count >= MAX_OBJECTS) return false;

        dst.nodes[dst.node_count] = src.nodes[node_index];
        dst.node_count += 1;

        // Remove from the source scene (compact).
        for (node_index..src.node_count - 1) |k| src.nodes[k] = src.nodes[k + 1];
        src.node_count -= 1;
        return true;
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    /// Fill `out` with handles of all loaded scenes; returns the populated slice.
    pub fn getLoadedScenes(self: *SceneManager, out: []SceneHandle) []SceneHandle {
        var n: usize = 0;
        var i: u16 = 0;
        while (i < MAX_SCENES and n < out.len) : (i += 1) {
            const s = &self.slots[i];
            if (s.in_use and s.state == .loaded) {
                out[n] = .{ .index = i, .generation = s.generation };
                n += 1;
            }
        }
        return out[0..n];
    }

    /// Number of loaded scenes (including persistent).
    pub fn loadedCount(self: *SceneManager) usize {
        var n: usize = 0;
        for (&self.slots) |*s| {
            if (s.in_use and s.state == .loaded) n += 1;
        }
        return n;
    }

    pub fn isLoaded(self: *SceneManager, handle: SceneHandle) bool {
        const slot = self.slotPtr(handle) orelse return false;
        return slot.state == .loaded;
    }

    pub fn getState(self: *SceneManager, handle: SceneHandle) LoadState {
        const slot = self.slotPtr(handle) orelse return .unloaded;
        return slot.state;
    }

    pub fn getError(self: *SceneManager, handle: SceneHandle) LoadError {
        const slot = self.slotPtr(handle) orelse return .none;
        return slot.err;
    }

    /// The scene's node array (valid until the scene is unloaded). Empty if the
    /// handle is stale.
    pub fn nodes(self: *SceneManager, handle: SceneHandle) []SceneNode {
        const slot = self.slotPtr(handle) orelse return &.{};
        return slot.nodes[0..slot.node_count];
    }

    /// Full-capacity node buffer for a loaded scene (length == MAX_OBJECTS while
    /// loaded), for runtime spawning into the scene (issue #32). Empty if stale.
    pub fn nodeBuffer(self: *SceneManager, handle: SceneHandle) []SceneNode {
        const slot = self.slotPtr(handle) orelse return &.{};
        return slot.nodes;
    }

    /// Pointer to a loaded scene's live node count, so a runtime spawner can grow
    /// or shrink the scene. Null if the handle is stale.
    pub fn nodeCountPtr(self: *SceneManager, handle: SceneHandle) ?*usize {
        const slot = self.slotPtr(handle) orelse return null;
        return &slot.node_count;
    }

    /// Find a loaded scene by its asset id, or null.
    pub fn findById(self: *SceneManager, id: []const u8) ?SceneHandle {
        var i: u16 = 0;
        while (i < MAX_SCENES) : (i += 1) {
            const s = &self.slots[i];
            if (s.in_use and std.mem.eql(u8, s.idSlice(), id)) {
                return .{ .index = i, .generation = s.generation };
            }
        }
        return null;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    fn slotPtr(self: *SceneManager, handle: SceneHandle) ?*Slot {
        if (handle.index >= MAX_SCENES) return null;
        const slot = &self.slots[handle.index];
        if (!slot.in_use or slot.generation != handle.generation) return null;
        return slot;
    }

    fn acquireSlot(self: *SceneManager, id: []const u8) Error!SceneHandle {
        var i: u16 = 0;
        while (i < MAX_SCENES) : (i += 1) {
            const slot = &self.slots[i];
            if (slot.in_use) continue;
            if (slot.nodes.len == 0) {
                slot.nodes = self.allocator.alloc(SceneNode, MAX_OBJECTS) catch return Error.OutOfMemory;
            }
            slot.in_use = true;
            slot.state = .unloaded;
            slot.err = .none;
            slot.persistent = false;
            slot.node_count = 0;
            slot.dep_of = null;
            slot.done = std.atomic.Value(bool).init(false);
            slot.progress = std.atomic.Value(u32).init(0);
            slot.setId(id);
            return .{ .index = i, .generation = slot.generation };
        }
        return Error.TooManyScenes;
    }

    fn freeSlot(self: *SceneManager, handle: SceneHandle) void {
        const slot = self.slotPtr(handle) orelse return;
        // Keep the node allocation for reuse; just mark the slot free and bump
        // the generation so outstanding handles become stale.
        if (self.persistent_index != null and self.persistent_index.? == handle.index) {
            self.persistent_index = null;
        }
        slot.in_use = false;
        slot.state = .unloaded;
        slot.node_count = 0;
        slot.dep_of = null;
        slot.persistent = false;
        slot.generation +%= 1;
    }
};

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

const testing = std.testing;

/// A fake loader: produces `n` named nodes for any id starting with "ok:",
/// where the id encodes the count as "ok:<n>:<label>". Ids starting with
/// "bad" fail. Used to exercise the manager without the real asset pipeline.
fn fakeLoader(ctx: ?*anyopaque, id: []const u8, out: []SceneNode, out_count: *usize) bool {
    _ = ctx;
    if (std.mem.startsWith(u8, id, "bad")) return false;
    // Default 2 nodes; if "ok:N:..." parse N.
    var count: usize = 2;
    if (std.mem.startsWith(u8, id, "ok:")) {
        var it = std.mem.splitScalar(u8, id["ok:".len..], ':');
        if (it.next()) |ns| count = std.fmt.parseInt(usize, ns, 10) catch 2;
    }
    count = @min(count, out.len);
    for (0..count) |i| {
        out[i] = .{};
        var nbuf: [16]u8 = undefined;
        out[i].setName(std.fmt.bufPrint(&nbuf, "node{d}", .{i}) catch "node");
    }
    out_count.* = count;
    return true;
}

const EventLog = struct {
    counts: [4]u32 = .{ 0, 0, 0, 0 },
    fn cb(ctx: ?*anyopaque, mgr: *SceneManager, handle: SceneHandle, event: Event) void {
        _ = mgr;
        _ = handle;
        const self: *EventLog = @ptrCast(@alignCast(ctx.?));
        self.counts[@intFromEnum(event)] += 1;
    }
};

test "load single scene then query active and nodes" {
    var mgr = SceneManager.init(testing.allocator);
    defer mgr.deinit();
    mgr.setLoader(fakeLoader, null);

    const h = try mgr.loadScene("ok:3:level", .single);
    try testing.expect(mgr.isLoaded(h));
    try testing.expectEqual(@as(usize, 3), mgr.nodes(h).len);
    try testing.expect(mgr.getActiveScene().?.eql(h));
    try testing.expectEqual(@as(usize, 1), mgr.loadedCount());
}

test "single load unloads the previous scene; additive keeps it" {
    var mgr = SceneManager.init(testing.allocator);
    defer mgr.deinit();
    mgr.setLoader(fakeLoader, null);

    const a = try mgr.loadScene("ok:1:a", .single);
    const b = try mgr.loadScene("ok:1:b", .additive);
    try testing.expectEqual(@as(usize, 2), mgr.loadedCount());
    try testing.expect(mgr.isLoaded(a));
    try testing.expect(mgr.isLoaded(b));

    // Single-load c: both a and b are unloaded.
    const c = try mgr.loadScene("ok:1:c", .single);
    try testing.expectEqual(@as(usize, 1), mgr.loadedCount());
    try testing.expect(!mgr.isLoaded(a));
    try testing.expect(!mgr.isLoaded(b));
    try testing.expect(mgr.getActiveScene().?.eql(c));
}

test "failed loads report a clear error" {
    var mgr = SceneManager.init(testing.allocator);
    defer mgr.deinit();
    mgr.setLoader(fakeLoader, null);

    try testing.expectError(Error.LoadFailed, mgr.loadScene("bad-scene", .single));

    // No loader installed.
    var mgr2 = SceneManager.init(testing.allocator);
    defer mgr2.deinit();
    try testing.expectError(Error.NoLoader, mgr2.loadScene("ok:1:x", .single));
}

test "lifecycle events fire on load/unload/activate/deactivate" {
    var log = EventLog{};
    var mgr = SceneManager.init(testing.allocator);
    defer mgr.deinit();
    mgr.setLoader(fakeLoader, null);
    mgr.subscribe(EventLog.cb, &log);

    const a = try mgr.loadScene("ok:1:a", .single); // loaded + activated
    const b = try mgr.loadScene("ok:1:b", .single); // a deactivated, a unloaded, b loaded+activated
    _ = a;
    mgr.unloadScene(b); // unloaded

    try testing.expect(log.counts[@intFromEnum(Event.loaded)] >= 2);
    try testing.expect(log.counts[@intFromEnum(Event.unloaded)] >= 2);
    try testing.expect(log.counts[@intFromEnum(Event.activated)] >= 2);
    try testing.expect(log.counts[@intFromEnum(Event.deactivated)] >= 1);
}

test "DontDestroyOnLoad moves an object into the persistent scene" {
    var mgr = SceneManager.init(testing.allocator);
    defer mgr.deinit();
    mgr.setLoader(fakeLoader, null);

    const a = try mgr.loadScene("ok:3:a", .single);
    try testing.expect(mgr.markDontDestroyOnLoad(a, 0));
    try testing.expectEqual(@as(usize, 2), mgr.nodes(a).len); // one moved out

    const ph = try mgr.persistentScene();
    try testing.expectEqual(@as(usize, 1), mgr.nodes(ph).len);

    // Single-load a new scene: the persistent object survives.
    _ = try mgr.loadScene("ok:1:b", .single);
    try testing.expect(mgr.isLoaded(ph));
    try testing.expectEqual(@as(usize, 1), mgr.nodes(ph).len);
}

test "scene dependencies unload with their parent" {
    var mgr = SceneManager.init(testing.allocator);
    defer mgr.deinit();
    mgr.setLoader(fakeLoader, null);

    const main = try mgr.loadSceneWithDeps("ok:1:main", &.{ "ok:1:dep1", "ok:1:dep2" }, .single);
    try testing.expectEqual(@as(usize, 3), mgr.loadedCount());

    mgr.unloadScene(main);
    try testing.expectEqual(@as(usize, 0), mgr.loadedCount());
}

test "a persistent bootstrap scene survives single loads" {
    var mgr = SceneManager.init(testing.allocator);
    defer mgr.deinit();
    mgr.setLoader(fakeLoader, null);

    const boot = try mgr.loadScene("ok:1:boot", .additive);
    mgr.setScenePersistent(boot, true);

    const level_a = try mgr.loadScene("ok:1:a", .single); // boot survives
    try testing.expect(mgr.isLoaded(boot));
    try testing.expect(mgr.isLoaded(level_a));

    const level_b = try mgr.loadScene("ok:1:b", .single); // a gone, boot survives
    try testing.expect(mgr.isLoaded(boot));
    try testing.expect(!mgr.isLoaded(level_a));
    try testing.expect(mgr.isLoaded(level_b));
    try testing.expectEqual(@as(usize, 2), mgr.loadedCount());
}

test "deferred requests apply on flush" {
    var mgr = SceneManager.init(testing.allocator);
    defer mgr.deinit();
    mgr.setLoader(fakeLoader, null);

    mgr.requestLoad("ok:1:a", .single);
    try testing.expectEqual(@as(usize, 0), mgr.loadedCount()); // not applied yet
    try testing.expect(mgr.flushRequests());
    try testing.expectEqual(@as(usize, 1), mgr.loadedCount());

    const a = mgr.findById("ok:1:a").?;
    mgr.requestUnload(a);
    mgr.requestLoad("ok:1:b", .additive);
    try testing.expect(mgr.flushRequests());
    try testing.expect(!mgr.isLoaded(a));
    try testing.expect(mgr.findById("ok:1:b") != null);

    try testing.expect(!mgr.flushRequests()); // empty queue → no change
}

test "stale handle after unload is detected" {
    var mgr = SceneManager.init(testing.allocator);
    defer mgr.deinit();
    mgr.setLoader(fakeLoader, null);

    const a = try mgr.loadScene("ok:1:a", .additive);
    mgr.unloadScene(a);
    try testing.expect(!mgr.isLoaded(a));
    try testing.expectEqual(@as(usize, 0), mgr.nodes(a).len);
    try testing.expectEqual(LoadState.unloaded, mgr.getState(a));
}

test "async load completes and is reapable" {
    var mgr = SceneManager.init(testing.allocator);
    defer mgr.deinit();
    mgr.setLoader(fakeLoader, null);

    const io = testing.io;
    const h = try mgr.loadSceneAsync(io, "ok:4:async", .single);
    // Spin until the worker (or inline fallback) signals completion.
    while (!mgr.isReady(h)) std.atomic.spinLoopHint();
    const state = mgr.finishAsync(io, h, .single);
    try testing.expectEqual(LoadState.loaded, state);
    try testing.expectEqual(@as(usize, 4), mgr.nodes(h).len);
    try testing.expect(mgr.getActiveScene().?.eql(h));
}
