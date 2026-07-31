const std = @import("std");
const engine = @import("engine");
const editor = @import("../root.zig");
const build_options = @import("turian_build_options");

const State = @import("State.zig");
const EditorState = @import("EditorState.zig");

/// Called after a rescan's results land in `EditorState.discovered_components`,
/// to re-sync a host panel registry with what was just discovered. A callback
/// rather than a direct import keeps this module GUI-free — set once at
/// startup by the host (Studio's main-window code).
pub var onRescan: ?*const fn () void = null;

pub const Vector3 = engine.Vector3;
pub const Transform = engine.Transform;
pub const Component = engine.Component;
pub const UserScriptRef = engine.UserScriptRef;
pub const SceneNode = engine.SceneNode;
pub const Project = engine.Project;
pub const MAX_OBJECTS = engine.scene.MAX_OBJECTS;
pub const NAME_MAX = engine.scene.NAME_MAX;
pub const ComponentDef = editor.ComponentDef;

pub const MAX_DISCOVERED = editor.scanner.MAX_COMPONENTS;

// ── Background component reflection (script compile) ────────────────────────
// Compiling user script reflection spawns `zig build` per source file. Runs on
// a worker via `io.concurrent`, tracked in `task_manager` for progress.

pub const ReflectJob = struct {
    arena: std.heap.ArenaAllocator,
    io: std.Io,
    task_id: u64,
    /// `EditorState.reflect_generation` at launch time — lets a completing job detect
    /// that a newer scan has already superseded it.
    generation: usize,
    components: [MAX_DISCOVERED]ComponentDef = undefined,
    count: usize = 0,
    config: editor.user_reflection.ReflectionConfig = undefined,
};

/// Snapshot `discovered_components` and the resolved reflection config into a
/// job, then dispatch it (or queue it if one is already running).
pub fn launchReflect(io: std.Io) void {
    EditorState.reflect_generation += 1;
    const job = std.heap.page_allocator.create(ReflectJob) catch return;
    job.* = .{
        .arena = std.heap.ArenaAllocator.init(std.heap.page_allocator),
        .io = io,
        .task_id = 0,
        .generation = EditorState.reflect_generation,
    };
    const a = job.arena.allocator();
    job.count = EditorState.discovered_count;
    @memcpy(job.components[0..EditorState.discovered_count], EditorState.discovered_components[0..EditorState.discovered_count]);

    const baked_ref_cfg = editor.GameBuild.BuildConfig{
        .engine_root = build_options.engine_root_path,
        .editor_root = build_options.editor_root_path,
        .cgltf_wrap_c = build_options.cgltf_wrap_c_path,
        .fbx_wrap_c = build_options.fbx_wrap_c_path,
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
    // Resolved from the job's own arena (not the caller's per-frame arena),
    // so the config's strings stay valid for the worker's lifetime.
    job.config = editor.sdk_layout.resolveReflectionConfig(io, a, EditorState.environ_map, baked_ref_cfg);

    if (EditorState.reflect_job != null) {
        if (EditorState.reflect_pending) |old| {
            old.arena.deinit();
            std.heap.page_allocator.destroy(old);
        }
        EditorState.reflect_pending = job;
        return;
    }
    dispatchReflect(job);
}

/// Register the job's task and start it as soon as its dependencies allow.
/// Compiling user scripts reads the project's imported assets, so a reflect
/// launched while an import is still in flight is declared blocked on it and
/// spawned later by `pumpReflect`.
pub fn dispatchReflect(job: *ReflectJob) void {
    var deps: [1]u64 = undefined;
    var dep_count: usize = 0;
    if (EditorState.import_job) |imp| {
        deps[0] = imp.task_id;
        dep_count = 1;
    }

    job.task_id = State.taskManager().submit(.{
        .kind = .compile,
        .label = "Compile scripts",
        .key = "scripts:compile",
        .status = .queued,
        .locks = .{ .scripts = true },
        .deps = deps[0..dep_count],
    });
    EditorState.reflect_job = job;
    EditorState.reflect_spawned = false;
    startReflectIfReady(job);
}

/// Spawn the worker once the task's dependencies have cleared. Called on
/// dispatch and again every frame from `pumpReflect` while the job waits.
fn startReflectIfReady(job: *ReflectJob) void {
    if (EditorState.reflect_spawned) return;
    const task = State.taskManager().get(job.task_id) orelse return;
    switch (task.status) {
        .queued => {},
        // The dependency failed and took this task with it; drop the job.
        .cancelled, .failed => {
            finishReflect(job);
            EditorState.reflect_job = null;
            return;
        },
        else => return,
    }

    State.taskManager().start(job.task_id);
    EditorState.reflect_future = job.io.concurrent(runReflectJob, .{job}) catch {
        // Concurrency unavailable: run synchronously (UI blocks, but the
        // scan is still tracked and correct).
        runReflectJob(job);
        finishReflect(job);
        EditorState.reflect_job = null;
        return;
    };
    EditorState.reflect_spawned = true;
}

pub fn runReflectJob(job: *ReflectJob) void {
    const progress = State.taskManager().progressFor(job.task_id);
    editor.user_reflection.loadFieldInfo(job.io, job.components[0..job.count], job.count, job.config, progress);
    if (State.taskManager().isCancelRequested(job.task_id)) {
        State.taskManager().cancel(job.task_id);
    } else {
        State.taskManager().complete(job.task_id);
    }
}

/// Merge a finished job's compiled field data back into the live component
/// list (unless a newer scan already superseded it) and free the job.
pub fn finishReflect(job: *ReflectJob) void {
    if (job.generation == EditorState.reflect_generation) {
        @memcpy(EditorState.discovered_components[0..job.count], job.components[0..job.count]);
        if (EditorState.object_count > 0) {
            const AssetResolution = @import("AssetResolution.zig");
            AssetResolution.syncSceneWithDefinitions();
        }
        if (onRescan) |cb| cb();
    }
    job.arena.deinit();
    std.heap.page_allocator.destroy(job);
}

/// Call once per frame (see `studio/Window.zig`) to reap a finished background
/// reflect job, keep the UI redrawing while one is in flight, and launch any
/// request that was queued behind it.
pub fn pumpReflect(io: std.Io) void {
    const job = EditorState.reflect_job orelse return;
    if (!EditorState.reflect_spawned) {
        startReflectIfReady(job);
        EditorState.requestRedraw();
        return;
    }
    const finished = if (State.taskManager().get(job.task_id)) |t| t.isFinished() else true;
    if (!finished) {
        EditorState.requestRedraw();
        return;
    }
    EditorState.reflect_future.await(io);
    EditorState.reflect_spawned = false;
    finishReflect(job);
    EditorState.reflect_job = null;
    if (EditorState.reflect_pending) |pending| {
        EditorState.reflect_pending = null;
        dispatchReflect(pending);
    }
    EditorState.requestRedraw();
}

/// Block until any in-flight background reflect job (and anything queued
/// behind it) finishes, merging its results in. For synchronous callers (the
/// `--build` CLI path) that need fully-populated field data before proceeding.
pub fn waitForReflect(io: std.Io) void {
    while (EditorState.reflect_job) |job| {
        // A job still blocked on its import dependency has no worker to await;
        // resolving the registry releases it so it can be spawned and drained.
        if (!EditorState.reflect_spawned) {
            State.taskManager().resolveDependencies();
            startReflectIfReady(job);
            // Still not runnable: the dependency has not landed, so there is
            // nothing here to wait on.
            if (!EditorState.reflect_spawned and EditorState.reflect_job != null) return;
            continue;
        }
        EditorState.reflect_future.await(io);
        EditorState.reflect_spawned = false;
        finishReflect(job);
        EditorState.reflect_job = null;
        if (EditorState.reflect_pending) |pending| {
            EditorState.reflect_pending = null;
            dispatchReflect(pending);
        }
    }
}
