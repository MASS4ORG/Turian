const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const rdebug = @import("debug");
const EditorState = @import("EditorState.zig");
const Window = @import("Window.zig");
const ProjectOps = @import("ProjectOps.zig");
const GpuRenderer = @import("GpuRenderer.zig");
const PreviewSystem = @import("PreviewSystem.zig");
const AssetWatcher = @import("AssetWatcher.zig");
const Documents = @import("Documents.zig");
const EditorFrameTiming = @import("EditorFrameTiming.zig");
const build_options = @import("turian_build_options");

/// Route std.log through the engine diagnostic ring so the Remote Debug
/// Protocol's `errors` method / MCP `list_errors` can surface recent warnings
/// and errors. Still forwards to the default logger.
pub const std_options: std.Options = .{ .logFn = engine.DiagLog.logFn };

/// Default debug server port for the Studio.
/// Games use 7777; Studio uses 7778 so both can run simultaneously.
const STUDIO_DEBUG_PORT: u16 = 7778;

/// Builds a read-only snapshot of the currently open scene for the debug server.
/// `views` is a caller-owned buffer whose lifetime must span the `pump` call —
/// the returned `World.scenes` borrows it. Also refreshes live metrics.
fn studioWorld(views: *[1]engine.introspect.SceneView) engine.introspect.World {
    EditorState.refreshDebugMetrics();
    const assets = EditorState.refreshDebugAssets();
    if (!EditorState.scene_open) return .{ .metrics = &EditorState.debug_metrics, .assets = assets };
    views[0] = .{
        .name = if (EditorState.current_scene_path) |p| std.fs.path.basename(p) else "(unsaved)",
        .id = if (EditorState.current_scene_path) |p| p else "",
        .active = true,
        .nodes = EditorState.objects[0..EditorState.object_count],
    };
    return .{ .scenes = views[0..1], .metrics = &EditorState.debug_metrics, .assets = assets };
}

/// Applies a remote-debug mutation to the open scene. Runs on the main thread
/// inside `debug_srv.pump`, so it routes through the editor's undo stack — AI /
/// CLI edits are undoable and consistent with the UI.
fn studioMutationApplier(_: ?*anyopaque, m: rdebug.Mutation) rdebug.MutationResult {
    const now = gui.frameTimeNS();
    switch (m) {
        .set_component => |sc| {
            const idx = EditorState.findObjectByName(sc.entity) orelse
                return .{ .ok = false, .message = "entity not found" };
            if (EditorState.debugSetComponentField(now, idx, sc.component, sc.field, sc.value))
                return .{ .ok = true, .message = "component field updated" };
            return .{ .ok = false, .message = "unknown component/field or value type mismatch" };
        },
        .set_transform => |st| {
            const idx = EditorState.findObjectByName(st.entity) orelse
                return .{ .ok = false, .message = "entity not found" };
            if (EditorState.debugSetTransform(now, idx, st.channel, st.value))
                return .{ .ok = true, .message = "transform updated" };
            return .{ .ok = false, .message = "unknown transform channel (use position/rotation/scale)" };
        },
        .spawn => |sp| {
            if (!EditorState.scene_open) return .{ .ok = false, .message = "no scene open" };
            const idx = EditorState.addObjectWithUndo(now, gui.io, sp.name, -1);
            EditorState.clearSelectedObjects();
            EditorState.selectObject(idx);
            gui.refresh(null, @src(), null);
            return .{ .ok = true, .message = "entity spawned" };
        },
        .destroy => |d| {
            const idx = EditorState.findObjectByName(d.entity) orelse
                return .{ .ok = false, .message = "entity not found" };
            EditorState.deleteObject(now, idx);
            gui.refresh(null, @src(), null);
            return .{ .ok = true, .message = "entity destroyed" };
        },
        .reload_asset => |r| {
            const arena = gui.currentWindow().arena();
            if (EditorState.debugReloadAsset(gui.io, arena, r.guid))
                return .{ .ok = true, .message = "asset reloaded" };
            return .{ .ok = false, .message = "asset GUID not found" };
        },
    }
}

/// Emits a `scene.loaded` / `scene.unloaded` notification over the debug server
/// via the event catalog. `id` is the scene's project-relative path (or empty
/// for an unsaved scene); the name is its basename. Strings are JSON-escaped so
/// Windows paths and odd names stay well-formed.
fn emitSceneEvent(srv: *rdebug.Server, ev: engine.introspect.Event, id: []const u8) void {
    const name = if (id.len > 0) std.fs.path.basename(id) else "(unsaved)";
    var buf: [1400]u8 = undefined;
    var w = std.Io.Writer.fixed(&buf);
    // One Stringify document: mixing raw writes with multiple top-level
    // `jw.write` values trips its "document already complete" assert. Use the
    // object API so the whole payload is a single JSON value.
    var jw = std.json.Stringify{ .writer = &w, .options = .{} };
    jw.beginObject() catch return;
    jw.objectField("scene") catch return;
    jw.write(name) catch return;
    jw.objectField("id") catch return;
    jw.write(id) catch return;
    jw.endObject() catch return;
    srv.emit(ev, w.buffered());
}

/// GUI editor entry point. Initialises dvui, loads the optional project, and runs the event loop.
pub fn main(main_init: std.process.Init) !void {
    try run(main_init);
    // Hard-exit rather than returning normally. By this point our own state
    // (documents, GPU renderer, debug server, dvui window/backend) is already
    // torn down cleanly — see the log lines above this call. What's left is
    // the Zig runtime's post-main cleanup (io thread pool, debug allocator
    // leak scan) and then libc's exit(), which runs atexit handlers
    // registered by the statically-linked SDL3/Vulkan loader. That teardown
    // segfaults on this machine's Vulkan/RADV stack; `_exit` skips it
    // entirely (the OS reclaims everything anyway).
    std.c._exit(0);
}

fn run(main_init: std.process.Init) !void {
    if (@import("builtin").os.tag == .windows) {
        gui.Backend.Common.windowsAttachConsole() catch {};
        // Request Vulkan on Windows so the GPU renderer can use SPIRV shaders.
        // Falls back to D3D12 silently if Vulkan is unavailable (3D viewport
        // will show "unavailable" but the rest of the editor still works).
        _ = gui.backend.c.SDL_SetHint("SDL_GPU_DRIVER", "vulkan");
    }

    gui.backend.enableSDLLogging();

    // Give the engine profiler a monotonic clock source. It's
    // enabled per-frame only while Play mode runs (see studio/Window.zig).
    engine.Profiler.setIo(main_init.io);

    var backend = try gui.backend.initWindow(.{
        .io = main_init.io,
        .allocator = main_init.gpa,
        .size = .{ .w = 1280.0, .h = 720.0 },
        .min_size = .{ .w = 800.0, .h = 600.0 },
        .vsync = true,
        .title = "Turian Studio",
    });
    defer backend.deinit();

    var win = try gui.Window.init(@src(), main_init.gpa, backend.backend(), .{
        .theme = switch (backend.preferredColorScheme() orelse .dark) {
            .light => gui.Theme.builtin.adwaita_light,
            .dark => gui.Theme.builtin.adwaita_dark,
        },
    });
    defer win.deinit();

    GpuRenderer.init(&backend) catch |err|
        std.debug.print("[GpuRenderer] init failed: {any}\n", .{err});
    PreviewSystem.init();

    EditorState.clearScene();
    EditorState.initUndo(main_init.gpa);

    EditorState.gpa = main_init.gpa;
    EditorState.environ_map = main_init.environ_map;

    // Start the Studio debug server so LLM tools (MCP) can inspect the open
    // scene while the developer works. Runs on port 7778 to coexist with a
    // running game on the default 7777.
    var debug_srv = rdebug.Server.init(main_init.gpa, .{ .port = STUDIO_DEBUG_PORT, .allow_write = true });
    debug_srv.start(main_init.io) catch |err|
        std.debug.print("[studio] debug server failed to start: {s}\n", .{@errorName(err)});
    defer debug_srv.deinit(main_init.io);
    const studio_applier = rdebug.MutationApplier{ .ctx = null, .applyFn = studioMutationApplier };

    {
        const home = main_init.environ_map.get(
            if (@import("builtin").os.tag == .windows) "USERPROFILE" else "HOME",
        ) orelse ".";
        try EditorState.initSettings(main_init.io, main_init.gpa, home);
    }
    defer EditorState.deinitSettings(main_init.io);

    var cli_project_buf: [1024]u8 = undefined;
    var cli_project_path: ?[]const u8 = null;
    var cli_build = false;
    {
        var args = try std.process.Args.Iterator.initAllocator(main_init.minimal.args, main_init.gpa);
        defer args.deinit();
        _ = args.next();
        while (args.next()) |arg| {
            if (std.mem.eql(u8, arg, "--project")) {
                if (args.next()) |path| {
                    const len = @min(path.len, cli_project_buf.len);
                    @memcpy(cli_project_buf[0..len], path[0..len]);
                    cli_project_path = cli_project_buf[0..len];
                }
            } else if (std.mem.eql(u8, arg, "--build")) {
                cli_build = true;
            }
        }
    }

    var interrupted = false;
    var project_opened_from_arg = false;
    var last_fps_bucket: u32 = 0;
    // Lightweight per-frame FPS so `fps.changed` works outside Play mode, where
    // the engine profiler is off.
    var last_frame_ns: i128 = 0;
    // Last observed scene identity, to detect open/close transitions and emit
    // scene.loaded / scene.unloaded.
    var last_scene_open = false;
    var last_scene_id_buf: [1024]u8 = undefined;
    var last_scene_id_len: usize = 0;

    main_loop: while (true) {
        // Apply a pending vsync change here — between frames, before the
        // swapchain texture is acquired in win.begin.
        GpuRenderer.applyPendingVsync();

        const nstime = win.beginWait(interrupted);
        EditorFrameTiming.beginFrame(nstime);
        try win.begin(nstime);
        _ = try backend.addAllEvents(&win);
        EditorFrameTiming.markEventsEnd(backend.nanoTime());

        GpuRenderer.beginFrame(backend.cmd);
        PreviewSystem.beginFrame();

        // Whether this frame is the last one. We must still call win.end()
        // below so the frame's command buffer is submitted — breaking out
        // early would leave an unsubmitted command buffer (with an acquired
        // swapchain texture), which leaks GPU resources on shutdown.
        var quit = false;

        if (!project_opened_from_arg) {
            project_opened_from_arg = true;
            EditorState.refreshComponents(gui.io, gui.currentWindow().arena());
            if (cli_project_path) |p| {
                ProjectOps.openProject(p);
                if (cli_build) {
                    const baked = editor.GameBuild.BuildConfig{
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
                        .ui_render_root = build_options.ui_render_root_path,
                        .dvui_url = build_options.dvui_url,
                        .dvui_hash = build_options.dvui_hash,
                        .engine_version = build_options.version,
                    };
                    var cfg_arena = std.heap.ArenaAllocator.init(main_init.gpa);
                    defer cfg_arena.deinit();
                    const config = editor.sdk_layout.resolveBuildConfig(gui.io, cfg_arena.allocator(), main_init.environ_map, baked);
                    // openProject above kicked off the script-reflection
                    // compile in the background; this CLI path needs fully
                    // populated component fields before building, so wait
                    // for it here instead of racing it.
                    EditorState.waitForReflect(gui.io);
                    _ = editor.GameBuild.buildGame(
                        gui.io,
                        p,
                        &EditorState.discovered_components,
                        EditorState.discovered_count,
                        config,
                        editor.Progress.none,
                    );
                    quit = true;
                }
            }
        }

        // Auto-detect external asset changes (replaces the manual Refresh
        // button): poll the assets tree and hot-reload when it changes.
        if (AssetWatcher.poll(gui.io, nstime)) {
            EditorState.refreshComponents(gui.io, gui.currentWindow().arena());
            PreviewSystem.bumpGeneration();
            gui.refresh(null, @src(), null);
        }

        if (!quit and !Window.frame()) quit = true;

        // Execute any queued remote-debug requests on the main thread against
        // live editor state.
        var debug_views: [1]engine.introspect.SceneView = undefined;
        debug_srv.pump(studioWorld(&debug_views), studio_applier);

        // Compute a lightweight FPS from the wall-clock frame delta. The engine
        // profiler only runs in Play mode, so its FPS is 0 while editing; fall
        // back to this so `fps.changed` and the `metrics` tool stay meaningful
        // outside Play.
        if (last_frame_ns != 0 and EditorState.debug_metrics.fps == 0) {
            const dt_ns = nstime - last_frame_ns;
            if (dt_ns > 0) {
                EditorState.debug_metrics.fps = @floatCast(1_000_000_000.0 / @as(f64, @floatFromInt(dt_ns)));
                EditorState.debug_metrics.frame_time_ms = @floatCast(@as(f64, @floatFromInt(dt_ns)) / 1_000_000.0);
            }
        }
        last_frame_ns = nstime;

        // Emit fps.changed only when the integer FPS bucket changes.
        {
            const bucket: u32 = @intFromFloat(@round(EditorState.debug_metrics.fps));
            if (bucket != last_fps_bucket) {
                last_fps_bucket = bucket;
                var fbuf: [48]u8 = undefined;
                if (std.fmt.bufPrint(&fbuf, "{{\"fps\":{d}}}", .{bucket})) |p|
                    debug_srv.emit(.fps_changed, p)
                else |_| {}
            }
        }

        // Emit scene.loaded / scene.unloaded on scene open/close/switch so LLM
        // tools can track which scene is live. Polling the live
        // EditorState here captures every transition regardless of its source
        // (tab open, new scene, close), without threading the server into
        // Documents/ProjectOps.
        {
            const cur_open = EditorState.scene_open;
            const cur_id: []const u8 = if (EditorState.current_scene_path) |p| p else "";
            const last_id = last_scene_id_buf[0..last_scene_id_len];
            if (cur_open != last_scene_open or (cur_open and !std.mem.eql(u8, cur_id, last_id))) {
                if (last_scene_open) emitSceneEvent(&debug_srv, .scene_unloaded, last_id);
                if (cur_open) emitSceneEvent(&debug_srv, .scene_loaded, cur_id);
                last_scene_open = cur_open;
                last_scene_id_len = @min(cur_id.len, last_scene_id_buf.len);
                @memcpy(last_scene_id_buf[0..last_scene_id_len], cur_id[0..last_scene_id_len]);
            }
        }

        EditorFrameTiming.markBuildEnd(backend.nanoTime());
        win.endRendering(.{});
        const end_micros = try win.end(.{});
        EditorFrameTiming.endFrame(backend.nanoTime());
        if (quit) break :main_loop;

        const wait_event_micros = win.waitTime(end_micros);
        interrupted = try backend.waitEventTimeout(wait_event_micros);
    }

    // Persist the open document tabs so they restore on next launch,
    // then free the per-tab scene snapshots held on the heap.
    Documents.persist();
    Documents.closeAll();

    GpuRenderer.deinit();
    EditorState.clearUndoStack(); // free undo/redo snapshots held at exit
}
