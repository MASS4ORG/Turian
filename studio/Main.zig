const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");
const Window = @import("Window.zig");
const ProjectOps = @import("ProjectOps.zig");
const GpuRenderer = @import("GpuRenderer.zig");
const AssetWatcher = @import("AssetWatcher.zig");
const Documents = @import("Documents.zig");
const build_options = @import("turian_build_options");

/// GUI editor entry point. Initialises dvui, loads the optional project, and runs the event loop.
pub fn main(main_init: std.process.Init) !void {
    if (@import("builtin").os.tag == .windows) {
        gui.Backend.Common.windowsAttachConsole() catch {};
        // Request Vulkan on Windows so the GPU renderer can use SPIRV shaders.
        // Falls back to D3D12 silently if Vulkan is unavailable (3D viewport
        // will show "unavailable" but the rest of the editor still works).
        _ = gui.backend.c.SDL_SetHint("SDL_GPU_DRIVER", "vulkan");
    }

    gui.backend.enableSDLLogging();

    // Give the engine profiler a monotonic clock source (issue #35). It's
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

    EditorState.clearScene();
    EditorState.initUndo(main_init.gpa);

    EditorState.gpa = main_init.gpa;
    EditorState.environ_map = main_init.environ_map;

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

    main_loop: while (true) {
        // Apply a pending vsync change here — between frames, before the
        // swapchain texture is acquired in win.begin (issue #35).
        GpuRenderer.applyPendingVsync();

        const nstime = win.beginWait(interrupted);
        try win.begin(nstime);
        _ = try backend.addAllEvents(&win);

        GpuRenderer.beginFrame(backend.cmd);

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
                    };
                    var cfg_arena = std.heap.ArenaAllocator.init(main_init.gpa);
                    defer cfg_arena.deinit();
                    const config = editor.sdk_layout.resolveBuildConfig(gui.io, cfg_arena.allocator(), main_init.environ_map, baked);
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
            gui.refresh(null, @src(), null);
        }

        if (!quit and !Window.frame()) quit = true;

        const end_micros = try win.end(.{});
        if (quit) break :main_loop;

        const wait_event_micros = win.waitTime(end_micros);
        interrupted = try backend.waitEventTimeout(wait_event_micros);
    }

    // Persist the open document tabs so they restore on next launch (issue #1),
    // then free the per-tab scene snapshots held on the heap.
    Documents.persist();
    Documents.closeAll();

    GpuRenderer.deinit();
    EditorState.clearUndoStack(); // free undo/redo snapshots held at exit
}
