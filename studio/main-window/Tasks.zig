//! Studio task runner — owns the editor `TaskManager` and runs long-running
//! operations (game build, asset reimport) off the UI thread so the bottom task
//! bar can animate progress and offer cancellation while the editor stays
//! responsive.
//!
//! Work is dispatched via `io.concurrent`, which runs the operation on one of
//! the Io implementation's worker threads. The `TaskManager` mutex makes the
//! UI thread's per-frame reads safe against the worker's writes. If the Io
//! implementation does not support concurrency, the job runs synchronously
//! (the UI blocks for its duration but the task system still works).
//!
//! At most one background job runs at a time; build is an exclusive operation
//! and serialising reimport with it keeps the asset database access simple.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("../services/EditorState.zig");
const build_options = @import("turian_build_options");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

const ComponentDef = EditorState.ComponentDef;
const Future = std.Io.Future(void);

/// The single editor-wide task registry, actually owned by `EditorState`
/// (whose own background script-reflection job needs it too, and can't
/// import this file without a cycle — every other studio file already
/// depends on `EditorState`, never the reverse). Read by the task bar each
/// frame.
pub fn tm() *editor.TaskManager {
    return EditorState.taskManager();
}

/// True while a background job is running (build/reimport are exclusive).
pub fn isBusy() bool {
    return active_job != null;
}

const JobKind = enum { build, reimport };

/// A self-contained unit of background work. All inputs the worker needs are
/// copied in (or, for the asset DB, accessed read-only) so the worker never
/// races the UI thread's mutable state.
const Job = struct {
    arena: std.heap.ArenaAllocator,
    io: std.Io,
    kind: JobKind,
    task_id: u64,
    /// Owned in `arena`.
    project_path: []const u8,

    // Build inputs (snapshotted from EditorState at launch).
    components: [EditorState.MAX_DISCOVERED]ComponentDef = undefined,
    component_count: usize = 0,
    config: editor.GameBuild.BuildConfig = undefined,
};

var active_job: ?*Job = null;
var active_future: Future = undefined;

// ── Public launch API ─────────────────────────────────────────────────────────

/// Launch a background game build for the open project.
pub fn launchBuild(io: std.Io) void {
    if (rejectIfBusy()) return;
    const project = EditorState.project_path orelse {
        noProjectDialog();
        return;
    };

    // Script reflection may still be compiling in the background (see
    // `EditorState.launchReflect`); block until it lands so the build doesn't
    // snapshot components with stale/zeroed field defaults.
    EditorState.waitForReflect(io);

    const job = std.heap.page_allocator.create(Job) catch return;
    job.* = .{
        .arena = std.heap.ArenaAllocator.init(std.heap.page_allocator),
        .io = io,
        .kind = .build,
        .task_id = 0,
        .project_path = "",
    };
    const a = job.arena.allocator();
    job.project_path = a.dupe(u8, project) catch project;
    job.component_count = EditorState.discovered_count;
    @memcpy(
        job.components[0..EditorState.discovered_count],
        EditorState.discovered_components[0..EditorState.discovered_count],
    );
    job.config = buildConfig(a);
    job.task_id = tm().begin(.build, tr("Build game"));

    dispatch(io, job);
}

/// Launch a background reimport of all assets for the open project.
pub fn launchReimport(io: std.Io) void {
    if (rejectIfBusy()) return;
    const project = EditorState.project_path orelse {
        noProjectDialog();
        return;
    };
    if (!EditorState.assetDbReady()) return;

    const job = std.heap.page_allocator.create(Job) catch return;
    job.* = .{
        .arena = std.heap.ArenaAllocator.init(std.heap.page_allocator),
        .io = io,
        .kind = .reimport,
        .task_id = 0,
        .project_path = "",
    };
    job.project_path = job.arena.allocator().dupe(u8, project) catch project;
    job.task_id = tm().begin(.import, tr("Reimport assets"));

    dispatch(io, job);
}

// ── Per-frame pump ────────────────────────────────────────────────────────────

/// Call once per frame. Reaps a finished background job and keeps frames
/// flowing while one is active so the progress bar animates.
pub fn pump(io: std.Io) void {
    const job = active_job orelse return;

    const finished = if (tm().get(job.task_id)) |t| t.isFinished() else true;
    if (finished) {
        active_future.await(io);
        finishJob(job);
        active_job = null;
        // One more frame so the bar reflects the final state immediately.
        gui.refresh(null, @src(), null);
    } else {
        // Worker thread is updating progress; schedule the next frame.
        gui.refresh(null, @src(), null);
    }
}

// ── Internals ─────────────────────────────────────────────────────────────────

fn dispatch(io: std.Io, job: *Job) void {
    active_future = io.concurrent(runJob, .{job}) catch {
        // Concurrency unavailable: run synchronously. The UI blocks for the
        // duration, but the task is still tracked and reported.
        runJob(job);
        finishJob(job);
        return;
    };
    active_job = job;
}

/// Worker entry point — runs on a background thread (or inline on fallback).
fn runJob(job: *Job) void {
    const progress = tm().progressFor(job.task_id);
    switch (job.kind) {
        .build => {
            const ok = editor.GameBuild.buildGame(
                job.io,
                job.project_path,
                &job.components,
                job.component_count,
                job.config,
                progress,
            );
            // Not translated: runs on a worker thread, where `gui.currentWindow()`
            // (and therefore `StudioLocale.tr`) is unsafe to call.
            finalize(job.task_id, ok, "Build failed");
        },
        .reimport => {
            // The asset database is iterated read-only here and on the UI
            // thread, so concurrent access is safe (no structural mutation).
            editor.asset_importer.reimportAll(
                job.io,
                job.arena.allocator(),
                job.project_path,
                &EditorState.asset_db,
                progress,
            );
            finalize(job.task_id, true, "");
        },
    }
}

/// Move a task to its terminal state, preferring a cancel observed mid-flight.
fn finalize(task_id: u64, ok: bool, fail_msg: []const u8) void {
    if (tm().isCancelRequested(task_id)) {
        tm().cancel(task_id);
    } else if (ok) {
        tm().complete(task_id);
    } else {
        tm().fail(task_id, fail_msg);
    }
}

fn finishJob(job: *Job) void {
    job.arena.deinit();
    std.heap.page_allocator.destroy(job);
}

fn rejectIfBusy() bool {
    if (active_job == null) return false;
    gui.dialog(@src(), .{}, .{
        .title = tr("Task Running"),
        .message = tr("A task is already running. Wait for it to finish."),
    });
    return true;
}

fn noProjectDialog() void {
    gui.dialog(@src(), .{}, .{
        .title = tr("No Project"),
        .message = tr("Open a project first."),
    });
}

/// Resolve a BuildConfig into `alloc` (baked paths + env-var overrides).
fn buildConfig(alloc: std.mem.Allocator) editor.GameBuild.BuildConfig {
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
    return editor.sdk_layout.resolveBuildConfig(gui.io, alloc, EditorState.environ_map, baked);
}
