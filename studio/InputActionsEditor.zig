//! Dedicated editor panel for `.inputactions` assets (issue #10).
//!
//! Edits the data-driven `engine.InputActions` binding asset: add/remove actions,
//! pick each action's kind (button/axis/vector), and edit the device bindings per
//! role. Save writes ZON back to the asset and re-cooks it. Mirrors the structure
//! of `MaterialEditor` / `DataAssetEditor` (module-level loaded state + a Save row).

const std = @import("std");
const dvui = @import("dvui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("EditorState.zig");

const InputActions = engine.InputActions;

const MAX_ACTIONS = 32;
const MAX_ROLE = 4; // engine.Input.MAX_BINDINGS
const NAME_CAP = 40;
const CODE_CAP = 24;

const RoleId = enum { pos, neg, up, down };

const EdSource = struct {
    device: InputActions.Device = .key,
    code: [CODE_CAP]u8 = .{0} ** CODE_CAP,
    axis_positive: bool = true,
};

const EdRole = struct {
    items: [MAX_ROLE]EdSource = [_]EdSource{.{}} ** MAX_ROLE,
    count: usize = 0,
};

const EdAction = struct {
    name: [NAME_CAP]u8 = .{0} ** NAME_CAP,
    kind: InputActions.Kind = .button,
    pos: EdRole = .{},
    neg: EdRole = .{},
    up: EdRole = .{},
    down: EdRole = .{},
};

var actions: [MAX_ACTIONS]EdAction = undefined;
var action_count: usize = 0;
var loaded_path_buf: [1024]u8 = undefined;
var loaded_path_len: usize = 0;
var dirty: bool = false;

// Mutations are recorded during the draw pass and applied after it, so the
// action/binding arrays are never modified while being iterated.
var cmd_remove_action: ?usize = null;
var cmd_add_binding: ?struct { action: usize, role: RoleId } = null;
var cmd_remove_binding: ?struct { action: usize, role: RoleId, idx: usize } = null;

fn loadedPath() []const u8 {
    return loaded_path_buf[0..loaded_path_len];
}

fn bufStr(b: []const u8) []const u8 {
    const end = std.mem.indexOfScalar(u8, b, 0) orelse b.len;
    return b[0..end];
}

fn setBuf(dst: []u8, s: []const u8) void {
    const n = @min(s.len, dst.len - 1);
    @memcpy(dst[0..n], s[0..n]);
    @memset(dst[n..], 0);
}

fn roleById(a: *EdAction, role: RoleId) *EdRole {
    return switch (role) {
        .pos => &a.pos,
        .neg => &a.neg,
        .up => &a.up,
        .down => &a.down,
    };
}

/// Draw the editor for the InputActions asset at `asset_path`. Loads (or reloads)
/// when the selected asset changes.
pub fn draw(asset_path: []const u8) void {
    if (!std.mem.eql(u8, asset_path, loadedPath())) load(asset_path);

    for (actions[0..action_count], 0..) |*act, ai| drawAction(act, ai);

    if (dvui.button(@src(), "+ Add Action", .{}, .{ .expand = .horizontal, .padding = .all(6) })) {
        if (action_count < MAX_ACTIONS) {
            actions[action_count] = .{};
            setBuf(&actions[action_count].name, "new_action");
            action_count += 1;
            dirty = true;
        }
    }

    _ = dvui.separator(@src(), .{ .expand = .horizontal, .id_extra = 9001 });

    {
        var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .all(6) });
        defer row.deinit();
        if (dirty)
            dvui.label(@src(), "Unsaved changes", .{}, .{ .gravity_y = 0.5, .expand = .horizontal })
        else
            dvui.label(@src(), "Saved", .{}, .{ .gravity_y = 0.5, .expand = .horizontal });
        if (dvui.button(@src(), "Save", .{}, .{ .gravity_y = 0.5, .style = if (dirty) .highlight else .control })) {
            save();
        }
    }

    applyCommands();
}

fn drawAction(act: *EdAction, ai: usize) void {
    var box = dvui.box(@src(), .{}, .{ .expand = .horizontal, .padding = .all(6), .id_extra = ai });
    defer box.deinit();

    {
        var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = ai });
        defer row.deinit();

        var te = dvui.textEntry(@src(), .{ .text = .{ .buffer = &act.name } }, .{
            .id_extra = ai,
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 150 },
        });
        const name_changed = te.text_changed;
        te.deinit();
        if (name_changed) dirty = true;

        if (dvui.dropdownEnum(@src(), InputActions.Kind, .{ .choice = &act.kind }, .{}, .{
            .id_extra = ai,
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 90 },
        })) dirty = true;

        if (dvui.button(@src(), "Remove", .{}, .{ .id_extra = ai, .gravity_y = 0.5 })) {
            cmd_remove_action = ai;
        }
    }

    switch (act.kind) {
        .button => drawRole(act, ai, .pos, "Buttons", 0),
        .axis => {
            drawRole(act, ai, .pos, "Positive", 0);
            drawRole(act, ai, .neg, "Negative", 1);
        },
        .vector => {
            drawRole(act, ai, .pos, "Right", 0);
            drawRole(act, ai, .neg, "Left", 1);
            drawRole(act, ai, .up, "Up", 2);
            drawRole(act, ai, .down, "Down", 3);
        },
    }
}

fn drawRole(act: *EdAction, ai: usize, role_id: RoleId, label: []const u8, rextra: usize) void {
    const role = roleById(act, role_id);
    const id = ai * 16 + rextra;

    var box = dvui.box(@src(), .{}, .{ .expand = .horizontal, .padding = .{ .x = 14, .y = 2 }, .id_extra = id });
    defer box.deinit();

    dvui.label(@src(), "{s}", .{label}, .{ .id_extra = id, .min_size_content = .{ .w = 80 } });

    for (role.items[0..role.count], 0..) |*src, bi| {
        const bid = id * 8 + bi;
        var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 10, .y = 1 }, .id_extra = bid });
        defer row.deinit();

        if (dvui.dropdownEnum(@src(), InputActions.Device, .{ .choice = &src.device }, .{}, .{
            .id_extra = bid,
            .gravity_y = 0.5,
            .min_size_content = .{ .w = 70 },
        })) dirty = true;

        var te = dvui.textEntry(@src(), .{ .text = .{ .buffer = &src.code } }, .{
            .id_extra = bid,
            .gravity_y = 0.5,
            .expand = .horizontal,
        });
        const code_changed = te.text_changed;
        te.deinit();
        if (code_changed) dirty = true;

        // Gamepad axes bind one direction (+/-) of the stick/trigger.
        if (src.device == .gamepad_axis) {
            const before = src.axis_positive;
            _ = dvui.checkbox(@src(), &src.axis_positive, "+", .{ .id_extra = bid, .gravity_y = 0.5 });
            if (src.axis_positive != before) dirty = true;
        }

        if (dvui.button(@src(), "x", .{}, .{ .id_extra = bid, .gravity_y = 0.5 })) {
            cmd_remove_binding = .{ .action = ai, .role = role_id, .idx = bi };
        }
    }

    if (role.count < MAX_ROLE) {
        if (dvui.button(@src(), "+ binding", .{}, .{ .id_extra = id, .padding = .{ .x = 6, .y = 2 } })) {
            cmd_add_binding = .{ .action = ai, .role = role_id };
        }
    }
}

fn applyCommands() void {
    if (cmd_remove_action) |ai| {
        if (ai < action_count) {
            for (ai..action_count - 1) |k| actions[k] = actions[k + 1];
            action_count -= 1;
            dirty = true;
        }
        cmd_remove_action = null;
    }
    if (cmd_add_binding) |c| {
        if (c.action < action_count) {
            const role = roleById(&actions[c.action], c.role);
            if (role.count < MAX_ROLE) {
                role.items[role.count] = .{};
                role.count += 1;
                dirty = true;
            }
        }
        cmd_add_binding = null;
    }
    if (cmd_remove_binding) |c| {
        if (c.action < action_count) {
            const role = roleById(&actions[c.action], c.role);
            if (c.idx < role.count) {
                for (c.idx..role.count - 1) |k| role.items[k] = role.items[k + 1];
                role.count -= 1;
                dirty = true;
            }
        }
        cmd_remove_binding = null;
    }
}

// ── Load / Save ────────────────────────────────────────────────────────────────

fn load(asset_path: []const u8) void {
    setBuf(loaded_path_buf[0..], asset_path);
    loaded_path_len = @min(asset_path.len, loaded_path_buf.len - 1);
    dirty = false;
    action_count = 0;
    cmd_remove_action = null;
    cmd_add_binding = null;
    cmd_remove_binding = null;

    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const bytes = std.Io.Dir.cwd().readFileAlloc(dvui.io, asset_path, arena, .unlimited) catch return;
    const ia = InputActions.loadFromBytes(arena, bytes) catch return;

    for (ia.actions) |a| {
        if (action_count >= MAX_ACTIONS) break;
        var ed = EdAction{ .kind = a.kind };
        setBuf(&ed.name, a.name);
        copyRole(&ed.pos, a.pos);
        copyRole(&ed.neg, a.neg);
        copyRole(&ed.up, a.up);
        copyRole(&ed.down, a.down);
        actions[action_count] = ed;
        action_count += 1;
    }
}

fn copyRole(dst: *EdRole, srcs: []const InputActions.Source) void {
    dst.count = 0;
    for (srcs) |s| {
        if (dst.count >= MAX_ROLE) break;
        dst.items[dst.count] = .{ .device = s.device, .axis_positive = s.axis_positive };
        setBuf(&dst.items[dst.count].code, s.code);
        dst.count += 1;
    }
}

fn save() void {
    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    const defs = arena.alloc(InputActions.ActionDef, action_count) catch return;
    for (actions[0..action_count], 0..) |*a, i| {
        defs[i] = .{
            .name = bufStr(&a.name),
            .kind = a.kind,
            .pos = buildSources(arena, &a.pos),
            .neg = buildSources(arena, &a.neg),
            .up = buildSources(arena, &a.up),
            .down = buildSources(arena, &a.down),
        };
    }

    const ia = InputActions{ .version = InputActions.CURRENT_VERSION, .actions = defs };
    ia.save(dvui.io, loadedPath()) catch return;
    dirty = false;

    // Keep the cached artifact in sync with the freshly written source.
    if (EditorState.project_path) |proj| {
        editor.asset_importer.importAssetForce(dvui.io, dvui.currentWindow().arena(), proj, loadedPath());
    }
}

fn buildSources(arena: std.mem.Allocator, role: *const EdRole) []const InputActions.Source {
    const out = arena.alloc(InputActions.Source, role.count) catch return &.{};
    for (role.items[0..role.count], 0..) |*s, i| {
        out[i] = .{ .device = s.device, .code = bufStr(&s.code), .axis_positive = s.axis_positive };
    }
    return out;
}
