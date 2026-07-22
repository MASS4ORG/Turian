const std = @import("std");
const engine = @import("engine");
const editor = @import("editor");

const State = @import("State.zig");
const EditorState = @import("EditorState.zig");
const UndoRedo = @import("UndoRedo.zig");
const Selection = @import("Selection.zig");
const AssetResolution = @import("AssetResolution.zig");

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

// ── Prefabs ────────────────────────────────────────────────────

/// Walk up from `idx` to the enclosing prefab-instance root, or null if `idx`
/// is not part of a prefab instance.
pub fn prefabInstanceRoot(idx: usize) ?usize {
    if (idx >= EditorState.object_count) return null;
    var cur: i32 = @intCast(idx);
    while (cur >= 0) {
        const c: usize = @intCast(cur);
        if (EditorState.objects[c].isPrefabInstanceRoot()) return c;
        cur = EditorState.objects[c].parent;
    }
    return null;
}

/// Collect `root` plus all of its descendants (transitively) into `out`.
fn collectInstanceIndices(root: usize, out: []usize) usize {
    var n: usize = 0;
    out[n] = root;
    n += 1;
    var i: usize = 0;
    while (i < EditorState.object_count) : (i += 1) {
        if (EditorState.objects[i].parent < 0) continue;
        const p: usize = @intCast(EditorState.objects[i].parent);
        var has_p = false;
        var has_i = false;
        for (out[0..n]) |c| {
            if (c == p) has_p = true;
            if (c == i) has_i = true;
        }
        if (has_p and !has_i and n < out.len) {
            out[n] = i;
            n += 1;
        }
    }
    return n;
}

/// Parse `bytes` into a freshly `arena`-allocated template buffer, growing
/// (from `MAX_OBJECTS`, doubling) until the whole template fits — a Bistro-
/// scale FBX hierarchy prefab can carry thousands of nodes, well past the old
/// fixed `MAX_OBJECTS` template cap. Relies on `editor.scene_io.loadSceneFromBytes`
/// (which `editor.prefab.parse` wraps) always reporting the template's *true*
/// node count, even when the buffer it was given was too small.
fn parseTemplateGrown(arena: std.mem.Allocator, bytes: []const u8) ?[]SceneNode {
    var cap: usize = MAX_OBJECTS;
    var buf = arena.alloc(SceneNode, cap) catch return null;
    var tn: usize = 0;
    while (true) {
        tn = editor.prefab.parse(arena, bytes, buf) orelse return null;
        if (tn <= cap or cap >= engine.scene.GROWTH_CEILING) break;
        cap = @min(tn, engine.scene.GROWTH_CEILING);
        buf = arena.alloc(SceneNode, cap) catch return null;
    }
    if (tn > buf.len) return null; // hit the growth ceiling
    return buf[0..tn];
}

/// Read the prefab template referenced by `guid_str`, returning its nodes
/// (arena-owned) or null. Uses `arena` for the file bytes + parse.
fn readPrefabTemplate(io: std.Io, arena: std.mem.Allocator, guid_str: []const u8) ?[]SceneNode {
    const path = AssetResolution.resolveAssetGuid(guid_str) orelse return null;
    const bytes = readFileArena(io, arena, path) orelse return null;
    return parseTemplateGrown(arena, bytes);
}

fn readFileArena(io: std.Io, arena: std.mem.Allocator, path: []const u8) ?[]u8 {
    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return null;
    defer file.close(io);
    var buf: [4096]u8 = undefined;
    var reader = file.reader(io, &buf);
    return reader.interface.allocRemaining(arena, .unlimited) catch null;
}

/// Build a non-colliding `<dir>/<base>.prefab` path, written into `buf`.
/// Returns null on overflow / 100 collisions.
fn uniquePrefabPath(io: std.Io, dir: []const u8, base: []const u8, buf: []u8) ?[]const u8 {
    var n: usize = 0;
    while (n < 100) : (n += 1) {
        const path = if (n == 0)
            std.fmt.bufPrint(buf, "{s}/{s}.prefab", .{ dir, base }) catch return null
        else
            std.fmt.bufPrint(buf, "{s}/{s}_{d}.prefab", .{ dir, base, n }) catch return null;
        const exists = blk: {
            _ = std.Io.Dir.cwd().openFile(io, path, .{}) catch break :blk false;
            break :blk true;
        };
        if (!exists) return path;
    }
    return null;
}

/// Sanitise an object name into a filename stem (alnum/_/- kept, others → '_').
fn sanitizeStem(name: []const u8, buf: []u8) []const u8 {
    var n: usize = 0;
    for (name) |c| {
        if (n >= buf.len) break;
        buf[n] = if (std.ascii.isAlphanumeric(c) or c == '_' or c == '-') c else '_';
        n += 1;
    }
    if (n == 0) {
        const fallback = "prefab";
        @memcpy(buf[0..fallback.len], fallback);
        return buf[0..fallback.len];
    }
    return buf[0..n];
}

/// Create a `.prefab` asset from the subtree rooted at `idx` and turn that
/// subtree into the first instance of it. Returns true on success.
pub fn createPrefabFromObject(now: i128, io: std.Io, idx: usize) bool {
    if (idx >= EditorState.object_count) return false;

    var arena_state = std.heap.ArenaAllocator.init(EditorState.gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const bytes = editor.prefab.serializeSubtree(arena, EditorState.objects, EditorState.object_count, idx) orelse return false;

    var stem_buf: [NAME_MAX]u8 = undefined;
    const stem = sanitizeStem(EditorState.objects[idx].nameSlice(), &stem_buf);
    var dir_buf: [1024]u8 = undefined;
    const dir = State.activeBrowseDir(&dir_buf);
    if (dir.len == 0) return false;
    var path_buf: [1024]u8 = undefined;
    const path = uniquePrefabPath(io, dir, stem, &path_buf) orelse return false;
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = bytes }) catch return false;

    const meta = editor.asset_meta.ensureMeta(io, arena, path);
    var guid_buf: [36]u8 = undefined;
    const guid_str = meta.guid.toString(&guid_buf);

    const before = UndoRedo.captureSnapshot();

    // Link the selected subtree as the first instance. Sized to
    // `object_count`, not `MAX_OBJECTS` — `collectInstanceIndices` bounds
    // itself to `out.len`, so a fixed 128-cap here would silently drop nodes
    // of a larger subtree (e.g. from a Bistro-scale FBX hierarchy).
    const indices = arena.alloc(usize, EditorState.object_count) catch return false;
    const n = collectInstanceIndices(idx, indices);
    for (indices[0..n]) |i| {
        EditorState.objects[i].setPrefabNode(EditorState.objects[i].guidSlice());
        EditorState.objects[i].clearOverrides();
    }
    EditorState.objects[idx].setPrefabSource(guid_str);

    EditorState.scene_dirty = true;
    const after = UndoRedo.captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .prefab_op = .{ .before = before, .after = after } });

    // Surface the new asset in the browser / asset database.
    AssetResolution.refreshComponents(io, arena);
    return true;
}

/// Instantiate the prefab at `prefab_path` into the current scene, parented
/// under the current selection (or scene root). Selects the new root. Returns
/// the new root index, or null.
pub fn instantiatePrefab(now: i128, io: std.Io, prefab_path: []const u8) ?usize {
    if (!State.assetDbReady()) return null;
    const info = EditorState.asset_db.findByPath(prefab_path) orelse return null;

    var arena_state = std.heap.ArenaAllocator.init(EditorState.gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const bytes = readFileArena(io, arena, prefab_path) orelse return null;
    var guid_buf: [36]u8 = undefined;
    const guid_str = info.guid.toString(&guid_buf);

    const parent: i32 = if (EditorState.selected_object) |s| @intCast(s) else -1;

    // Probe the template's true node count (a zero-length output buffer just
    // asks `loadSceneFromBytes` to report the count, per its doc comment)
    // before growing the destination scene — `instantiate` itself only
    // checks capacity, it doesn't grow the caller's storage.
    var probe_count: usize = 0;
    _ = editor.scene_io.loadSceneFromBytes(arena, bytes, &.{}, &probe_count);
    EditorState.ensureObjectCapacity(EditorState.object_count + probe_count);

    const before = UndoRedo.captureSnapshot();
    const root = editor.prefab.instantiate(arena, io, bytes, guid_str, EditorState.objects, &EditorState.object_count, parent) orelse return null;

    AssetResolution.syncSceneWithDefinitions();
    Selection.clearSelectedObjects();
    EditorState.selected_object = root;
    Selection.selectObject(root);
    State.clearSelectedAsset();
    EditorState.scene_dirty = true;

    const after = UndoRedo.captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .prefab_op = .{ .before = before, .after = after } });
    return root;
}

/// Recompute the override record for every node of the prefab instance rooted
/// at `root`, diffing against the source template. Call after editing a node
/// that belongs to a prefab instance.
pub fn recomputePrefabOverrides(io: std.Io, root: usize) void {
    if (root >= EditorState.object_count or !EditorState.objects[root].isPrefabInstanceRoot()) return;

    var arena_state = std.heap.ArenaAllocator.init(EditorState.gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const tmpl = readPrefabTemplate(io, arena, EditorState.objects[root].prefabSourceSlice()) orelse return;

    const indices = arena.alloc(usize, EditorState.object_count) catch return;
    const n = collectInstanceIndices(root, indices);
    for (indices[0..n]) |i| {
        if (editor.prefab.findTemplate(&EditorState.objects[i], tmpl)) |t| {
            editor.prefab.recomputeOverrides(&EditorState.objects[i], t);
        }
    }
}

/// Re-apply every prefab instance's source template (respecting overrides) for
/// the whole scene. Called after loading a scene so edits made to a source
/// prefab since the scene was saved flow into its instances. Does not mark the
/// scene dirty or push undo — it reconciles loaded state with sources.
pub fn resyncPrefabInstances(io: std.Io) void {
    var arena_state = std.heap.ArenaAllocator.init(EditorState.gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    var r: usize = 0;
    while (r < EditorState.object_count) : (r += 1) {
        if (!EditorState.objects[r].isPrefabInstanceRoot()) continue;

        const tmpl = readPrefabTemplate(io, arena, EditorState.objects[r].prefabSourceSlice()) orelse continue;

        const indices = arena.alloc(usize, EditorState.object_count) catch continue;
        const n = collectInstanceIndices(r, indices);
        for (indices[0..n]) |i| {
            if (editor.prefab.findTemplate(&EditorState.objects[i], tmpl)) |t| {
                editor.prefab.applyTemplate(&EditorState.objects[i], t, true);
            }
        }
        _ = arena_state.reset(.retain_capacity);
    }
}

/// Revert the prefab instance rooted at `root` to its source template,
/// discarding all per-instance overrides.
pub fn revertPrefabInstance(now: i128, io: std.Io, root: usize) bool {
    if (root >= EditorState.object_count or !EditorState.objects[root].isPrefabInstanceRoot()) return false;

    var arena_state = std.heap.ArenaAllocator.init(EditorState.gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const tmpl = readPrefabTemplate(io, arena, EditorState.objects[root].prefabSourceSlice()) orelse return false;

    const before = UndoRedo.captureSnapshot();
    const indices = arena.alloc(usize, EditorState.object_count) catch return false;
    const n = collectInstanceIndices(root, indices);
    for (indices[0..n]) |i| {
        if (editor.prefab.findTemplate(&EditorState.objects[i], tmpl)) |t| {
            editor.prefab.applyTemplate(&EditorState.objects[i], t, false);
            EditorState.objects[i].clearOverrides();
        }
    }
    EditorState.scene_dirty = true;
    const after = UndoRedo.captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .prefab_op = .{ .before = before, .after = after } });
    return true;
}

/// Apply the prefab instance rooted at `root` back to its source asset, then
/// propagate the change to every other instance of that prefab in the scene
/// (preserving their overrides). Returns true on success.
pub fn applyPrefabInstance(now: i128, io: std.Io, root: usize) bool {
    if (root >= EditorState.object_count or !EditorState.objects[root].isPrefabInstanceRoot()) return false;
    const src_guid = EditorState.objects[root].prefabSourceSlice();
    const path = AssetResolution.resolveAssetGuid(src_guid) orelse return false;

    var arena_state = std.heap.ArenaAllocator.init(EditorState.gpa);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    // Rewrite the source template from this instance (preserving identities).
    const bytes = editor.prefab.serializeInstanceAsTemplate(arena, EditorState.objects, EditorState.object_count, root) orelse return false;
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = bytes }) catch return false;

    const tmpl = parseTemplateGrown(arena, bytes) orelse return false;

    const before = UndoRedo.captureSnapshot();

    // Propagate to every instance of this prefab (including resetting this one's
    // now-applied overrides).
    var r: usize = 0;
    while (r < EditorState.object_count) : (r += 1) {
        if (!EditorState.objects[r].isPrefabInstanceRoot()) continue;
        if (!std.mem.eql(u8, EditorState.objects[r].prefabSourceSlice(), src_guid)) continue;

        const indices = arena.alloc(usize, EditorState.object_count) catch continue;
        const n = collectInstanceIndices(r, indices);
        const is_source = (r == root);
        for (indices[0..n]) |i| {
            if (editor.prefab.findTemplate(&EditorState.objects[i], tmpl)) |t| {
                if (is_source) {
                    EditorState.objects[i].clearOverrides();
                } else {
                    editor.prefab.applyTemplate(&EditorState.objects[i], t, true);
                }
            }
        }
    }
    EditorState.scene_dirty = true;
    const after = UndoRedo.captureSnapshot();
    UndoRedo.pushCommand(now, &.{ .prefab_op = .{ .before = before, .after = after } });
    return true;
}
