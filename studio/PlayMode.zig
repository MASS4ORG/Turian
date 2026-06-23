//! Play mode (issue #31) — runs the current scene's game simulation in-process,
//! inside the editor viewport, with clean enter/exit and exact state restore.
//!
//! ## State machine
//!   edit ──Play──▶ playing ──Pause──▶ paused ──Play──▶ playing
//!     ▲                │                  │
//!     └──────Stop──────┴────────Stop──────┘
//! `Step` advances exactly one update frame while paused.
//!
//! ## Execution model — in-process, hot-compiled shared library
//! User scripts are arbitrary `.zig` files not linked into the studio binary,
//! so they are compiled on Play into a *play library* (`editor.PlayBuild`) which
//! the studio dlopen()s and drives through a small C ABI. The library owns the
//! live scene nodes, the input snapshot and the script instances; the studio
//! owns the window, the renderer and the loop. See
//! `docs/decisions/0002-play-mode.md` for the in-process vs subprocess analysis.
//!
//! ## State restore
//! The edit-time scene is snapshotted on Play and restored verbatim on Stop, so
//! nothing the simulation does can leak into the saved scene. (Play edits live
//! only inside the library's own node copy, seeded from a serialized snapshot.)
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const GpuRenderer = @import("GpuRenderer.zig");
const build_options = @import("turian_build_options");

pub const State = enum { edit, playing, paused };

/// C-ABI entry points resolved from the play library after dlopen.
const Fns = struct {
    start: *const fn ([*]const engine.SceneNode, usize) callconv(.c) bool,
    update: *const fn (f32, f32, u64) callconv(.c) void,
    stop: *const fn () callconv(.c) void,
    nodes_ptr: *const fn () callconv(.c) [*]engine.SceneNode,
    nodes_count: *const fn () callconv(.c) usize,
    new_frame: *const fn () callconv(.c) void,
    set_key: *const fn (u16, bool) callconv(.c) void,
    set_mouse_button: *const fn (u8, bool) callconv(.c) void,
    set_mouse_pos: *const fn (f32, f32) callconv(.c) void,
    add_mouse_motion: *const fn (f32, f32) callconv(.c) void,
    add_wheel: *const fn (f32) callconv(.c) void,
    load_input_actions: *const fn ([*]const u8, usize) callconv(.c) void,
    register_prefab: *const fn ([*]const u8, usize, [*]const engine.SceneNode, usize) callconv(.c) void,
};

var g_state: State = .edit;

var g_lib: ?std.DynLib = null;
var g_fns: Fns = undefined;
/// Hash of the script sources the loaded library was built from. A change means
/// the user edited a script, so the library must be rebuilt before the next Play.
var g_lib_hash: u64 = 0;
var g_lib_valid: bool = false;

// Edit-time scene snapshot, restored on Stop.
var g_snapshot: [EditorState.objects.len]engine.SceneNode = undefined;
var g_snapshot_count: usize = 0;

// Timing.
var g_prev_ns: i128 = 0;
var g_elapsed: f32 = 0;
var g_frame: u64 = 0;
var g_step_once: bool = false;

// FPS (exponential moving average of instantaneous 1/dt).
var g_fps: f32 = 0;

pub fn state() State {
    return g_state;
}

pub fn isActive() bool {
    return g_state != .edit;
}

pub fn fps() f32 {
    return g_fps;
}

// ── Public controls ────────────────────────────────────────────────────────

/// Buffer for the "Play First Scene" nodes (loaded independently of the
/// currently-edited scene).
var g_first_scene_nodes: [EditorState.objects.len]engine.SceneNode = undefined;

/// Enter Play (from edit) or resume (from paused), using the currently-edited
/// scene.
pub fn play(io: std.Io) void {
    switch (g_state) {
        .playing => return,
        .paused => {
            g_state = .playing;
            g_prev_ns = gui.frameTimeNS();
            return;
        },
        .edit => {},
    }
    _ = startFromNodes(io, EditorState.objects[0..EditorState.object_count]);
}

/// Enter Play running the project's *first scene* (from ProjectSettings),
/// independent of whichever scene is currently open in the editor. Useful to
/// test the real game entry point without switching scenes. The editor's own
/// scene is untouched and shown again on Stop.
pub fn playFirstScene(io: std.Io) void {
    switch (g_state) {
        .playing, .paused => return,
        .edit => {},
    }

    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();

    const scene_path = EditorState.firstScenePath(io, arena.allocator()) orelse {
        gui.toast(@src(), .{ .message = "No first scene found. Set one in Project Settings." });
        return;
    };

    var count: usize = 0;
    if (!editor.scene_io.loadScene(io, arena.allocator(), scene_path, &g_first_scene_nodes, &count)) {
        gui.toast(@src(), .{ .message = "Failed to load the first scene." });
        return;
    }

    _ = startFromNodes(io, g_first_scene_nodes[0..count]);
}

/// Build (if needed), start the play library on `nodes`, and enter the playing
/// state. The edit-time editor scene is always snapshotted so Stop restores it
/// verbatim — so playing an off-screen scene (Play First Scene) never disturbs
/// what the editor is showing.
fn startFromNodes(io: std.Io, nodes: []const engine.SceneNode) bool {
    const project = EditorState.project_path orelse {
        gui.toast(@src(), .{ .message = "Open a project before entering Play mode." });
        return false;
    };

    // (Re)build the play library if the scripts changed since last build.
    const hash = sourceHash(io);
    if (!g_lib_valid or hash != g_lib_hash) {
        unloadLibrary();
        gui.toast(@src(), .{ .message = "Compiling play library..." });
        if (!loadLibrary(io, project)) {
            gui.toast(@src(), .{ .message = "Play build failed — see console." });
            return false;
        }
        g_lib_hash = hash;
    }

    // Snapshot the edit-time scene so Stop restores it exactly.
    g_snapshot_count = EditorState.object_count;
    @memcpy(g_snapshot[0..g_snapshot_count], EditorState.objects[0..g_snapshot_count]);

    // Hand the library a copy of the nodes directly (no JSON): studio and
    // library share the same SceneNode layout, and the library memcpy's its own
    // copy, so play-time mutations never touch EditorState.
    if (!g_fns.start(nodes.ptr, nodes.len)) {
        gui.toast(@src(), .{ .message = "Play start failed." });
        return false;
    }

    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    loadInputActions(io, arena.allocator());
    registerPrefabs(io);

    GpuRenderer.setRenderOverride(playNodes());
    g_state = .playing;
    g_prev_ns = gui.frameTimeNS();
    g_elapsed = 0;
    g_frame = 0;
    g_fps = 0;
    g_step_once = false;
    return true;
}

/// Pause a running simulation (no effect from edit/paused).
pub fn pause() void {
    if (g_state == .playing) g_state = .paused;
}

/// Advance exactly one update frame while paused.
pub fn step() void {
    if (g_state == .paused) g_step_once = true;
}

/// Stop the simulation and restore the edit-time scene exactly.
pub fn stop() void {
    if (g_state == .edit) return;
    if (g_lib_valid) g_fns.stop();
    GpuRenderer.setRenderOverride(null);

    // Restore the snapshot taken on Play (no play edit ever touched EditorState,
    // but restoring is the contract and guards against future edits-during-play).
    @memcpy(EditorState.objects[0..g_snapshot_count], g_snapshot[0..g_snapshot_count]);
    EditorState.object_count = g_snapshot_count;

    g_state = .edit;
}

/// Toggle between Play and Stop (used by the Ctrl+P shortcut).
pub fn toggle(io: std.Io) void {
    if (g_state == .edit) play(io) else stop();
}

// ── Per-frame pump ─────────────────────────────────────────────────────────

/// Step the simulation once per frame. Called from the main UI loop. While
/// active it keeps frames flowing (the viewport must animate continuously).
pub fn pump(io: std.Io) void {
    _ = io;
    if (g_state == .edit) return;

    const now = gui.frameTimeNS();
    var dt: f32 = @as(f32, @floatFromInt(now - g_prev_ns)) / 1_000_000_000.0;
    g_prev_ns = now;
    // Clamp pathological deltas (first frame after a long compile, breakpoints).
    if (dt < 0 or dt > 0.25) dt = 1.0 / 60.0;

    const advance = g_state == .playing or (g_state == .paused and g_step_once);
    if (advance) {
        g_fns.new_frame();
        feedInput();
        g_elapsed += dt;
        g_frame += 1;
        g_fns.update(dt, g_elapsed, g_frame);
        g_step_once = false;

        if (g_state == .playing and dt > 0) {
            const inst = 1.0 / dt;
            g_fps = if (g_fps == 0) inst else g_fps * 0.9 + inst * 0.1;
        }
    }

    // Keep the render override pointing at the (stable) live node storage and
    // request another frame so the simulation animates.
    GpuRenderer.setRenderOverride(playNodes());
    gui.refresh(null, @src(), null);
}

// ── Internals ──────────────────────────────────────────────────────────────

fn playNodes() []const engine.SceneNode {
    if (!g_lib_valid) return &.{};
    const ptr = g_fns.nodes_ptr();
    const count = g_fns.nodes_count();
    return ptr[0..count];
}

/// Forward this frame's dvui input events into the live simulation.
fn feedInput() void {
    for (gui.events()) |*e| {
        switch (e.evt) {
            .key => |ke| {
                if (ke.action == .repeat) continue;
                if (mapKey(ke.code)) |k| g_fns.set_key(k, ke.action == .down);
            },
            .mouse => |me| switch (me.action) {
                .press, .release => {
                    if (mapButton(me.button)) |b| g_fns.set_mouse_button(b, me.action == .press);
                },
                .motion => |delta| g_fns.add_mouse_motion(delta.x, delta.y),
                .position => g_fns.set_mouse_pos(me.p.x, me.p.y),
                .wheel_y => |amt| g_fns.add_wheel(amt),
                else => {},
            },
            else => {},
        }
    }
}

fn mapButton(b: gui.enums.Button) ?u8 {
    return switch (b) {
        .left => @intFromEnum(engine.MouseButton.left),
        .right => @intFromEnum(engine.MouseButton.right),
        .middle => @intFromEnum(engine.MouseButton.middle),
        .four => @intFromEnum(engine.MouseButton.x1),
        .five => @intFromEnum(engine.MouseButton.x2),
        else => null,
    };
}

fn mapKey(code: gui.enums.Key) ?u16 {
    const K = engine.Key;
    const ek: ?K = switch (code) {
        .a => .a,
        .b => .b,
        .c => .c,
        .d => .d,
        .e => .e,
        .f => .f,
        .g => .g,
        .h => .h,
        .i => .i,
        .j => .j,
        .k => .k,
        .l => .l,
        .m => .m,
        .n => .n,
        .o => .o,
        .p => .p,
        .q => .q,
        .r => .r,
        .s => .s,
        .t => .t,
        .u => .u,
        .v => .v,
        .w => .w,
        .x => .x,
        .y => .y,
        .z => .z,
        .zero => .num_0,
        .one => .num_1,
        .two => .num_2,
        .three => .num_3,
        .four => .num_4,
        .five => .num_5,
        .six => .num_6,
        .seven => .num_7,
        .eight => .num_8,
        .nine => .num_9,
        .space => .space,
        .enter, .kp_enter => .enter,
        .escape => .escape,
        .tab => .tab,
        .backspace => .backspace,
        .left => .left,
        .right => .right,
        .up => .up,
        .down => .down,
        .left_shift => .left_shift,
        .right_shift => .right_shift,
        .left_control => .left_ctrl,
        .right_control => .right_ctrl,
        .left_alt => .left_alt,
        .right_alt => .right_alt,
        else => null,
    };
    return if (ek) |k| @intFromEnum(k) else null;
}

/// Scratch buffer for parsing each prefab/scene asset before registering it.
var g_prefab_buf: [EditorState.objects.len]engine.SceneNode = undefined;

/// Register every scene/prefab asset's template nodes with the play library so
/// scripts can `Instantiate` them by GUID at runtime (issue #32).
fn registerPrefabs(io: std.Io) void {
    if (!EditorState.assetDbReady()) return;
    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    var it = EditorState.asset_db.enumerate(.scene);
    while (it.next()) |info| {
        var count: usize = 0;
        if (!editor.scene_io.loadScene(io, arena.allocator(), info.path, &g_prefab_buf, &count)) continue;
        var gbuf: [36]u8 = undefined;
        const guid = info.guid.toString(&gbuf);
        g_fns.register_prefab(guid.ptr, guid.len, &g_prefab_buf, count);
        _ = arena.reset(.retain_capacity);
    }
}

/// Feed every InputActions asset in the project into the live input map so
/// scripts that read actions by name work in Play (reuses the build data path).
fn loadInputActions(io: std.Io, a: std.mem.Allocator) void {
    if (!EditorState.assetDbReady()) return;
    var it = EditorState.asset_db.enumerate(.input_actions);
    while (it.next()) |info| {
        var file = std.Io.Dir.cwd().openFile(io, info.path, .{}) catch continue;
        defer file.close(io);
        var fbuf: [4096]u8 = undefined;
        var reader = file.reader(io, &fbuf);
        const bytes = reader.interface.allocRemaining(a, .unlimited) catch continue;
        g_fns.load_input_actions(bytes.ptr, bytes.len);
    }
}

/// Build + dlopen the play library and resolve its C-ABI symbols.
fn loadLibrary(io: std.Io, project: []const u8) bool {
    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    const a = arena.allocator();

    const lib_path = editor.PlayBuild.buildPlayLibrary(
        io,
        a,
        project,
        &EditorState.discovered_components,
        EditorState.discovered_count,
        buildConfig(a),
    ) orelse return false;

    var lib = std.DynLib.open(lib_path) catch |err| {
        std.debug.print("[Turian] dlopen play library failed: {any}\n", .{err});
        return false;
    };

    const S = editor.PlayBuild.symbols;
    g_fns = .{
        .start = lib.lookup(@TypeOf(g_fns.start), S.start) orelse return failLookup(&lib),
        .update = lib.lookup(@TypeOf(g_fns.update), S.update) orelse return failLookup(&lib),
        .stop = lib.lookup(@TypeOf(g_fns.stop), S.stop) orelse return failLookup(&lib),
        .nodes_ptr = lib.lookup(@TypeOf(g_fns.nodes_ptr), S.nodes_ptr) orelse return failLookup(&lib),
        .nodes_count = lib.lookup(@TypeOf(g_fns.nodes_count), S.nodes_count) orelse return failLookup(&lib),
        .new_frame = lib.lookup(@TypeOf(g_fns.new_frame), S.new_frame) orelse return failLookup(&lib),
        .set_key = lib.lookup(@TypeOf(g_fns.set_key), S.set_key) orelse return failLookup(&lib),
        .set_mouse_button = lib.lookup(@TypeOf(g_fns.set_mouse_button), S.set_mouse_button) orelse return failLookup(&lib),
        .set_mouse_pos = lib.lookup(@TypeOf(g_fns.set_mouse_pos), S.set_mouse_pos) orelse return failLookup(&lib),
        .add_mouse_motion = lib.lookup(@TypeOf(g_fns.add_mouse_motion), S.add_mouse_motion) orelse return failLookup(&lib),
        .add_wheel = lib.lookup(@TypeOf(g_fns.add_wheel), S.add_wheel) orelse return failLookup(&lib),
        .load_input_actions = lib.lookup(@TypeOf(g_fns.load_input_actions), S.load_input_actions) orelse return failLookup(&lib),
        .register_prefab = lib.lookup(@TypeOf(g_fns.register_prefab), S.register_prefab) orelse return failLookup(&lib),
    };
    g_lib = lib;
    g_lib_valid = true;
    return true;
}

fn failLookup(lib: *std.DynLib) bool {
    std.debug.print("[Turian] play library missing an expected symbol\n", .{});
    lib.close();
    return false;
}

fn unloadLibrary() void {
    if (g_lib) |*lib| lib.close();
    g_lib = null;
    g_lib_valid = false;
}

/// Hash the contents of every unique user-script source file so we can tell when
/// a recompile is needed.
fn sourceHash(io: std.Io) u64 {
    var h = std.hash.Wyhash.init(0);
    var seen: [64][]const u8 = undefined;
    var seen_count: usize = 0;
    for (EditorState.discovered_components[0..EditorState.discovered_count]) |*def| {
        if (def.is_builtin) continue;
        const src = def.sourceFile();
        if (src.len == 0) continue;
        const dup = for (seen[0..seen_count]) |s| {
            if (std.mem.eql(u8, s, src)) break true;
        } else false;
        if (dup or seen_count >= seen.len) continue;
        seen[seen_count] = src;
        seen_count += 1;

        h.update(src);
        var file = std.Io.Dir.cwd().openFile(io, src, .{}) catch continue;
        defer file.close(io);
        var buf: [4096]u8 = undefined;
        var reader = file.reader(io, &buf);
        var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
        defer arena.deinit();
        if (reader.interface.allocRemaining(arena.allocator(), .unlimited)) |bytes| {
            h.update(bytes);
        } else |_| {}
    }
    return h.final();
}

/// Resolve a BuildConfig (baked paths + SDK detection + env overrides), matching
/// `studio/Tasks.zig`'s game-build configuration.
fn buildConfig(a: std.mem.Allocator) editor.PlayBuild.BuildConfig {
    const baked = editor.PlayBuild.BuildConfig{
        .engine_root = build_options.engine_root_path,
        .editor_root = build_options.editor_root_path,
        .cgltf_wrap_c = build_options.cgltf_wrap_c_path,
        .vendor_include = build_options.vendor_include_path,
        .build_root = build_options.build_root_path,
        .sdl3_lib = build_options.sdl3_lib_path,
        .math_root = build_options.math_root_path,
        .guid_root = build_options.guid_root_path,
        .oap_root = build_options.oap_root_path,
        .serde_root = build_options.serde_root_path,
        .serde_compat_root = build_options.serde_compat_root_path,
        .ktx2_root = build_options.ktx2_root_path,
        .gpu_root = build_options.gpu_root_path,
        .gpu_sdl3_c = build_options.gpu_sdl3_c_path,
        .render_root = build_options.render_root_path,
        .sdl3_include = build_options.sdl3_include_path,
    };
    return editor.sdk_layout.resolveBuildConfig(gui.io, a, EditorState.environ_map, baked);
}
