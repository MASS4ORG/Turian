const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");

const State = @import("State.zig");
const EditorState = @import("EditorState.zig");

// ── Background asset import (project open) ──────────────────────────────────
// `asset_importer.importAll` reads and hashes every source asset's full bytes
// on every project open, and (re)cooks changed/version-bumped ones — for a
// large project this can take seconds. Running that inline used to block the
// very first frame from presenting (a black window until it returned). It now
// scans+imports into a private `AssetDatabase` on a worker via `io.concurrent`,
// swapped into `EditorState.asset_db` only once finished — avoids any other
// thread observing (or racing) a half-imported database.

pub const ImportJob = struct {
    io: std.Io,
    task_id: u64,
    /// `EditorState.import_generation` at launch time — lets a completing job
    /// detect that a newer project open has already superseded it.
    generation: usize,
    project_path: []u8,
    assets_path: []u8,
    db: editor.AssetDatabase,
    /// Runs once, on the main thread, after this job's database lands in
    /// `EditorState.asset_db` — e.g. to restore previously-open scene tabs
    /// only once their assets are actually resolvable.
    on_done: ?*const fn () void,
};

/// Scan and import `project_path` on a worker, then (on completion) swap the
/// result into `EditorState.asset_db` and run `on_done`.
pub fn launchImport(io: std.Io, project_path: []const u8, assets_path: []const u8, on_done: ?*const fn () void) void {
    EditorState.import_generation += 1;
    const job = std.heap.page_allocator.create(ImportJob) catch return;
    job.* = .{
        .io = io,
        .task_id = 0,
        .generation = EditorState.import_generation,
        .project_path = std.heap.page_allocator.dupe(u8, project_path) catch {
            std.heap.page_allocator.destroy(job);
            return;
        },
        .assets_path = std.heap.page_allocator.dupe(u8, assets_path) catch {
            std.heap.page_allocator.destroy(job);
            return;
        },
        .db = editor.AssetDatabase.init(std.heap.page_allocator),
        .on_done = on_done,
    };

    if (EditorState.import_job != null) {
        if (EditorState.import_pending) |old| {
            old.db.deinit();
            std.heap.page_allocator.free(old.project_path);
            std.heap.page_allocator.free(old.assets_path);
            std.heap.page_allocator.destroy(old);
        }
        EditorState.import_pending = job;
        return;
    }
    dispatchImport(job);
}

pub fn dispatchImport(job: *ImportJob) void {
    job.task_id = State.taskManager().begin(.import, "Import assets");
    EditorState.import_future = job.io.concurrent(runImportJob, .{job}) catch {
        // Concurrency unavailable: run synchronously (UI blocks, but the
        // import is still tracked and correct).
        runImportJob(job);
        finishImport(job);
        return;
    };
    EditorState.import_job = job;
}

pub fn runImportJob(job: *ImportJob) void {
    job.db.scan(job.io, job.assets_path);
    const progress = State.taskManager().progressFor(job.task_id);
    editor.asset_importer.importAll(job.io, std.heap.page_allocator, job.project_path, &job.db, progress);
    if (State.taskManager().isCancelRequested(job.task_id)) {
        State.taskManager().cancel(job.task_id);
    } else {
        State.taskManager().complete(job.task_id);
    }
}

/// Swap a finished job's database into `EditorState.asset_db` (unless a newer
/// project open already superseded it) and free the job.
pub fn finishImport(job: *ImportJob) void {
    if (job.generation == EditorState.import_generation) {
        if (EditorState.asset_db_initialized) EditorState.asset_db.deinit();
        EditorState.asset_db = job.db;
        EditorState.asset_db_initialized = true;
        if (job.on_done) |cb| cb();
    } else {
        job.db.deinit();
    }
    std.heap.page_allocator.free(job.project_path);
    std.heap.page_allocator.free(job.assets_path);
    std.heap.page_allocator.destroy(job);
}

/// Call once per frame (see `studio/main-window/Window.zig`) to reap a
/// finished background import job, keep the UI redrawing while one is in
/// flight, and launch any request that was queued behind it.
pub fn pumpImport(io: std.Io) void {
    const job = EditorState.import_job orelse return;
    const finished = if (State.taskManager().get(job.task_id)) |t| t.isFinished() else true;
    if (!finished) {
        gui.refresh(null, @src(), null);
        return;
    }
    EditorState.import_future.await(io);
    finishImport(job);
    EditorState.import_job = null;
    if (EditorState.import_pending) |pending| {
        EditorState.import_pending = null;
        dispatchImport(pending);
    }
    gui.refresh(null, @src(), null);
}

/// Block until any in-flight background import job (and anything queued
/// behind it) finishes, merging its results in. For synchronous callers (the
/// `--build` CLI path) that need a fully-cooked cache before proceeding.
pub fn waitForImport(io: std.Io) void {
    while (EditorState.import_job) |job| {
        EditorState.import_future.await(io);
        finishImport(job);
        EditorState.import_job = null;
        if (EditorState.import_pending) |pending| {
            EditorState.import_pending = null;
            dispatchImport(pending);
        }
    }
}
