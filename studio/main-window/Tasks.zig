//! Studio task runner — runs long-running operations (build, reimport) off the
//! UI thread. Jobs whose capability locks do not overlap run in parallel; the
//! rest queue behind whatever holds the lock they need.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");
const EditorState = @import("editor").EditorState;
const build_options = @import("turian_build_options");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

const ComponentDef = EditorState.ComponentDef;
const Future = std.Io.Future(void);

/// Jobs allowed to run at once. Two covers the useful overlap (a build while
/// assets reimport is impossible anyway — they share the asset lock) without
/// letting a mistaken burst of clicks spawn a compiler farm.
pub const MAX_PARALLEL = 2;

/// The single editor-wide task registry, actually owned by `EditorState`
/// (whose own background script-reflection job needs it too, and can't
/// import this file without a cycle — every other studio file already
/// depends on `EditorState`, never the reverse). Read by the task bar each
/// frame.
pub fn tm() *editor.TaskManager {
    return EditorState.taskManager();
}

/// True while any studio-launched job is running.
pub fn isBusy() bool {
    return running_count > 0;
}

/// Capabilities currently held by *any* background task, including the
/// project-open import and script compile owned by `EditorState`. Callers
/// disable the affected UI action rather than the whole editor.
pub fn locks() editor.TaskLocks {
    return tm().activeLocks();
}

/// Whether an action needing `wanted` must be disabled right now.
pub fn isLocked(wanted: editor.TaskLocks) bool {
    return tm().activeLocks().overlaps(wanted);
}

/// Label of the task holding `wanted`, for explaining a disabled control.
pub fn lockOwnerLabel(wanted: editor.TaskLocks, buf: []u8) []const u8 {
    const owner = tm().lockOwner(wanted) orelse return "";
    const n = @min(owner.label().len, buf.len);
    @memcpy(buf[0..n], owner.label()[0..n]);
    return buf[0..n];
}

// ── Remote-debug / MCP exposure ───────────────────────────────────────────────

const introspect = @import("engine").introspect;

/// Backing storage for `debugViews`. Static because the borrowed strings must
/// outlive `studioWorld`'s stack frame, and the debug server consumes them
/// later in the same main-thread frame.
var debug_tasks: [editor.TaskManager.MAX_TASKS]editor.Task = undefined;
var debug_views: [editor.TaskManager.MAX_TASKS]introspect.TaskView = undefined;
var debug_locks: [editor.TaskManager.MAX_TASKS][40]u8 = undefined;

/// Snapshot every task as debug-protocol views, so `tasks.list` (and the
/// `list_tasks` MCP tool) can tell a machine driver whether the operation it
/// triggered has actually landed.
pub fn debugViews() []const introspect.TaskView {
    const now_ms = nowMs();
    const tasks = debug_tasks[0..tm().snapshot(&debug_tasks)];
    for (tasks, 0..) |*t, i| {
        const roll = editor.task_tree.rollup(tasks, t.*);
        debug_views[i] = .{
            .id = t.id,
            .parent_id = t.parent_id,
            .kind = @tagName(t.kind),
            .status = @tagName(roll.status),
            .label = t.label(),
            .note = t.note(),
            .progress = roll.progress,
            .units_done = t.units_done,
            .units_total = t.units_total,
            .elapsed_ms = t.elapsedMs(now_ms),
            .locks = formatLocks(t.locks, &debug_locks[i]),
        };
    }
    return debug_views[0..tasks.len];
}

fn formatLocks(held: editor.TaskLocks, buf: []u8) []const u8 {
    var w: usize = 0;
    inline for (.{ "assets", "scripts", "scene", "project" }) |name| {
        if (@field(held, name)) {
            if (w > 0 and w < buf.len) {
                buf[w] = ',';
                w += 1;
            }
            const n = @min(name.len, buf.len - w);
            @memcpy(buf[w..][0..n], name[0..n]);
            w += n;
        }
    }
    return buf[0..w];
}

const JobKind = enum { build, reimport };

/// A self-contained unit of background work.
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

    // Reimport: private database swapped in once finished (avoids racing the UI thread).
    db: editor.AssetDatabase = undefined,
    /// True once `db` has been handed to `EditorState`, which then owns it.
    db_published: bool = false,
    /// Owned in `arena`.
    assets_path: []const u8 = "",
};

/// One occupied worker slot.
const Slot = struct {
    job: *Job,
    future: Future,
    /// False while the job's task is still queued or blocked, so `pump` knows
    /// there is no future to await yet.
    spawned: bool = false,
};

var slots: [MAX_PARALLEL]?Slot = @splat(null);
var running_count: usize = 0;

// ── Public launch API ─────────────────────────────────────────────────────────

/// Launch a background game build for the open project.
pub fn launchBuild(io: std.Io) void {
    const project = EditorState.project_path orelse {
        noProjectDialog();
        return;
    };

    const job = newJob(io, .build, project) orelse return;

    // Asset import and script reflection may still be running (see
    // `EditorState.launchImport`/`launchReflect`). Declaring them as
    // dependencies lets the editor stay responsive while they finish, instead
    // of blocking the UI thread, and still guarantees the build never
    // snapshots a half-cooked cache or zeroed component field defaults.
    var deps: [2]u64 = undefined;
    var dep_count: usize = 0;
    if (EditorState.import_job) |j| {
        deps[dep_count] = j.task_id;
        dep_count += 1;
    }
    if (EditorState.reflect_job) |j| {
        deps[dep_count] = j.task_id;
        dep_count += 1;
    }

    // A build packages the assets, reads the compiled scripts and writes the
    // output folder, so it holds everything. Repeat clicks coalesce into a
    // single rerun rather than queueing one full build per press.
    submit(job, .{
        .kind = .build,
        .label = tr("Build game"),
        .key = "project:build",
        .policy = .coalesce,
        .status = .queued,
        .locks = .{ .assets = true, .scripts = true, .project = true },
        .deps = deps[0..dep_count],
    });
}

/// Launch a background reimport of all assets for the open project.
pub fn launchReimport(io: std.Io) void {
    const project = EditorState.project_path orelse {
        noProjectDialog();
        return;
    };
    if (!EditorState.assetDbReady()) return;

    const job = newJob(io, .reimport, project) orelse return;
    const a = job.arena.allocator();
    job.db = editor.AssetDatabase.init(std.heap.page_allocator);
    job.assets_path = std.fmt.allocPrint(a, "{s}/assets", .{project}) catch "";

    submit(job, .{
        .kind = .import,
        .label = tr("Reimport assets"),
        .key = "assets:reimport",
        .policy = .coalesce,
        .status = .queued,
        .locks = .{ .assets = true },
    });
}

// ── Per-frame pump ────────────────────────────────────────────────────────────

/// Monotonic milliseconds since boot — the clock the task registry stamps
/// start/finish times with, and the one the task bar renders elapsed from.
pub fn nowMs() i64 {
    const ns: i128 = std.Io.Clock.boot.now(gui.io).nanoseconds;
    return @intCast(@divTrunc(ns, std.time.ns_per_ms));
}

/// Call once per frame. Advances registry bookkeeping, starts queued jobs whose
/// locks are free, reaps finished ones, and keeps frames flowing while any job
/// is active so progress bars animate.
pub fn pump(io: std.Io) void {
    tm().tick(nowMs());

    var busy = false;
    for (&slots) |*maybe| {
        const slot = &(maybe.* orelse continue);
        if (!slot.spawned) startIfReady(slot);

        const finished = if (tm().get(slot.job.task_id)) |t| t.isFinished() else true;
        if (!finished) {
            busy = true;
            continue;
        }
        // An unspawned slot either ran inline (no concurrency available) or was
        // cancelled with its dependency — neither leaves a future to await.
        if (slot.spawned) slot.future.await(io);
        const job = slot.job;
        const task_id = job.task_id;
        const kind = job.kind;
        const completed = if (tm().get(task_id)) |t| t.status == .completed else false;
        // Only a successful run publishes its results: a cancelled reimport
        // would otherwise swap its empty database over the live one.
        if (completed) publishJob(job);
        discardJob(job);
        maybe.* = null;
        running_count -= 1;
        // A `.coalesce` duplicate arrived mid-run: honour it exactly once.
        if (tm().takeRerunRequest(task_id)) {
            switch (kind) {
                .build => launchBuild(io),
                .reimport => launchReimport(io),
            }
        }
        busy = true;
    }
    if (busy) gui.refresh(null, @src(), null);
}

// ── Internals ─────────────────────────────────────────────────────────────────

fn newJob(io: std.Io, kind: JobKind, project: []const u8) ?*Job {
    const job = std.heap.page_allocator.create(Job) catch return null;
    job.* = .{
        .arena = std.heap.ArenaAllocator.init(std.heap.page_allocator),
        .io = io,
        .kind = kind,
        .task_id = 0,
        .project_path = "",
    };
    job.project_path = job.arena.allocator().dupe(u8, project) catch project;
    return job;
}

/// Register the job's task and take a worker slot. A coalesced duplicate or a
/// full slot table releases the job without running it.
fn submit(job: *Job, spec: editor.TaskSpec) void {
    const id = tm().submit(spec);
    // Coalesced into an in-flight task, or the registry is full: nothing to run.
    if (id == 0 or ownerOf(id) != null) {
        discardJob(job);
        return;
    }
    job.task_id = id;

    for (&slots) |*maybe| {
        if (maybe.* != null) continue;
        maybe.* = .{ .job = job, .future = undefined };
        running_count += 1;
        startIfReady(&(maybe.*.?));
        return;
    }
    // Every slot is occupied: cancel the registration rather than leave a task
    // nobody will ever run.
    tm().cancel(id);
    discardJob(job);
}

/// Whether some slot already owns the given task id.
fn ownerOf(task_id: u64) ?*Slot {
    for (&slots) |*maybe| {
        const slot = &(maybe.* orelse continue);
        if (slot.job.task_id == task_id) return slot;
    }
    return null;
}

/// Spawn a queued job's worker once its dependencies have landed and nothing
/// else holds a lock it needs.
fn startIfReady(slot: *Slot) void {
    if (slot.spawned) return;
    const task = tm().get(slot.job.task_id) orelse return;
    if (task.status != .queued) return;
    if (heldByOthers(slot.job.task_id).overlaps(task.locks)) return;

    // Snapshot build inputs at start rather than at submit: a reflect job this
    // build waited on will have rewritten the component definitions since.
    if (slot.job.kind == .build) snapshotBuildInputs(slot.job);

    tm().start(slot.job.task_id);
    slot.future = slot.job.io.concurrent(runJob, .{slot.job}) catch {
        // Concurrency unavailable: run synchronously. The UI blocks for the
        // duration, but the task is still tracked and reported.
        runJob(slot.job);
        return;
    };
    slot.spawned = true;
}

/// Copy the component list and resolve the build config from live editor
/// state. Runs on the UI thread, immediately before the worker starts.
fn snapshotBuildInputs(job: *Job) void {
    job.component_count = EditorState.discovered_count;
    @memcpy(
        job.components[0..EditorState.discovered_count],
        EditorState.discovered_components[0..EditorState.discovered_count],
    );
    job.config = buildConfig(job.arena.allocator());
}

/// Locks held by every active task except `except_id`.
fn heldByOthers(except_id: u64) editor.TaskLocks {
    var buf: [editor.TaskManager.MAX_TASKS]editor.Task = undefined;
    const tasks = buf[0..tm().snapshot(&buf)];
    var held: editor.TaskLocks = .none;
    for (tasks) |*t| {
        if (t.id == except_id or !t.isActive() or t.status == .queued or t.status == .blocked) continue;
        held = held.merge(t.locks);
    }
    return held;
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
            // Scan into the job's own private database to avoid racing the UI thread.
            job.db.scan(job.io, job.assets_path);
            editor.asset_importer.reimportAll(
                job.io,
                std.heap.page_allocator,
                job.project_path,
                &job.db,
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

/// Hand a successful job's results to the editor.
fn publishJob(job: *Job) void {
    if (job.kind != .reimport) return;
    // Swap the reimported private database in now that the worker is done.
    if (EditorState.asset_db_initialized) EditorState.asset_db.deinit();
    EditorState.asset_db = job.db;
    EditorState.asset_db_initialized = true;
    job.db_published = true;
}

/// Free a job and anything it still owns.
fn discardJob(job: *Job) void {
    if (job.kind == .reimport and !job.db_published) job.db.deinit();
    job.arena.deinit();
    std.heap.page_allocator.destroy(job);
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
    return editor.sdk_layout.resolveBuildConfig(gui.io, alloc, EditorState.environ_map, baked);
}
