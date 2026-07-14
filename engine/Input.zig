//! Input core: a device-agnostic snapshot of the current input state plus a
//! semantic **action map** layered on top of it.
//!
//! Design: gameplay code reacts to *actions* (named, remappable),
//! never raw key codes. The host loop (game `main` or the studio viewport)
//! feeds raw device events into an `Input` each frame; scripts poll it through
//! `engine.Frame` (see ADR 0001). `Input` is plain data — no globals, no SDL
//! dependency — so unit tests construct one directly and substitute any state.

const std = @import("std");
const Vector2 = @import("math").Vector2;

/// Maximum number of named actions in a map.
pub const MAX_ACTIONS = 64;
/// Maximum bindings per role (e.g. how many keys can trigger one action).
pub const MAX_BINDINGS = 4;
/// Maximum length of an action name.
pub const NAME_MAX = 32;

/// Device-independent keyboard key. The host maps platform scancodes onto these.
pub const Key = enum(u16) {
    unknown = 0,
    // Letters
    a,
    b,
    c,
    d,
    e,
    f,
    g,
    h,
    i,
    j,
    k,
    l,
    m,
    n,
    o,
    p,
    q,
    r,
    s,
    t,
    u,
    v,
    w,
    x,
    y,
    z,
    // Number row
    num_0,
    num_1,
    num_2,
    num_3,
    num_4,
    num_5,
    num_6,
    num_7,
    num_8,
    num_9,
    // Editing / whitespace
    space,
    enter,
    escape,
    tab,
    backspace,
    // Arrows
    left,
    right,
    up,
    down,
    // Modifiers
    left_shift,
    right_shift,
    left_ctrl,
    right_ctrl,
    left_alt,
    right_alt,
};

/// Mouse button identifier.
pub const MouseButton = enum(u8) { left, right, middle, x1, x2 };

/// Gamepad button identifier. Values mirror SDL3's `SDL_GamepadButton` order so
/// the host can map SDL events with `@enumFromInt` (locked by a test below).
/// Names follow SDL/cross-platform face-button positions: `a` = south (Xbox A),
/// `b` = east (Xbox B), `x` = west (Xbox X), `y` = north (Xbox Y).
pub const GamepadButton = enum(u8) {
    a,
    b,
    x,
    y,
    back,
    guide,
    start,
    left_stick,
    right_stick,
    left_shoulder,
    right_shoulder,
    dpad_up,
    dpad_down,
    dpad_left,
    dpad_right,
};

/// Gamepad analog axis. Values mirror SDL3's `SDL_GamepadAxis` order.
pub const GamepadAxis = enum(u8) {
    left_x,
    left_y,
    right_x,
    right_y,
    left_trigger,
    right_trigger,
};

/// One half of a gamepad axis used as a directional source (e.g. left stick
/// pushed right is `left_x` positive).
pub const GamepadAxisDir = struct { axis: GamepadAxis, positive: bool = true };

/// A single physical source that can trigger an action.
pub const Binding = union(enum) {
    key: Key,
    mouse: MouseButton,
    gamepad_button: GamepadButton,
    gamepad_axis: GamepadAxisDir,
};

/// Stick/trigger magnitude below which input is treated as zero (drift guard).
pub const DEADZONE: f32 = 0.15;
/// Action value at/above which a button action counts as "pressed".
pub const PRESS_THRESHOLD: f32 = 0.5;
/// Number of gamepad analog axes.
pub const GAMEPAD_AXIS_COUNT = @typeInfo(GamepadAxis).@"enum".fields.len;

/// Semantic action category.
pub const ActionKind = enum {
    /// On/off (e.g. "jump", "fire").
    button,
    /// 1-D value in [-1, 1] (e.g. "move_forward" from W/S).
    axis,
    /// 2-D value, each component in [-1, 1] (e.g. "move" from WASD).
    vector,
};

/// A fixed-capacity list of bindings that all map to the same action role.
pub const BindingSet = struct {
    items: [MAX_BINDINGS]Binding = undefined,
    len: u8 = 0,

    pub fn fromSlice(bindings: []const Binding) BindingSet {
        var s: BindingSet = .{};
        for (bindings) |b| {
            if (s.len >= MAX_BINDINGS) break;
            s.items[s.len] = b;
            s.len += 1;
        }
        return s;
    }

    /// Magnitude in [0, 1]: the strongest contribution of any bound source this
    /// frame (a held button is 1; an analog stick/trigger is its deadzoned value).
    fn value(self: BindingSet, input: *const Input) f32 {
        var v: f32 = 0;
        for (self.items[0..self.len]) |b| v = @max(v, input.bindingValue(b, false));
        return v;
    }

    /// Same as `value` but evaluated against the previous frame's state.
    fn valuePrev(self: BindingSet, input: *const Input) f32 {
        var v: f32 = 0;
        for (self.items[0..self.len]) |b| v = @max(v, input.bindingValue(b, true));
        return v;
    }
};

/// A named action and the bindings that drive it. The four roles are reused per
/// kind: button uses `pos`; axis uses `pos`/`neg`; vector uses all four.
pub const Action = struct {
    name_buf: [NAME_MAX]u8 = undefined,
    name_len: u8 = 0,
    kind: ActionKind = .button,
    pos: BindingSet = .{}, // button / axis positive / vector +x (right)
    neg: BindingSet = .{}, //          axis negative / vector -x (left)
    up: BindingSet = .{}, //                            vector +y (up)
    down: BindingSet = .{}, //                          vector -y (down)

    pub fn name(self: *const Action) []const u8 {
        return self.name_buf[0..self.name_len];
    }
};

/// Snapshot of input plus the action map. Construct with `init`, configure
/// actions with `defineButton`/`defineAxis`/`defineVector`, then each frame:
/// `newFrame()` → feed raw events → scripts poll.
pub const Input = struct {
    keys_down: std.EnumSet(Key) = std.EnumSet(Key).initEmpty(),
    keys_prev: std.EnumSet(Key) = std.EnumSet(Key).initEmpty(),
    mouse_down: std.EnumSet(MouseButton) = std.EnumSet(MouseButton).initEmpty(),
    mouse_prev: std.EnumSet(MouseButton) = std.EnumSet(MouseButton).initEmpty(),
    mouse_x: f32 = 0,
    mouse_y: f32 = 0,
    mouse_dx: f32 = 0,
    mouse_dy: f32 = 0,
    wheel: f32 = 0,

    pad_buttons: std.EnumSet(GamepadButton) = std.EnumSet(GamepadButton).initEmpty(),
    pad_buttons_prev: std.EnumSet(GamepadButton) = std.EnumSet(GamepadButton).initEmpty(),
    pad_axes: [GAMEPAD_AXIS_COUNT]f32 = .{0} ** GAMEPAD_AXIS_COUNT,
    pad_axes_prev: [GAMEPAD_AXIS_COUNT]f32 = .{0} ** GAMEPAD_AXIS_COUNT,
    /// True while at least one gamepad is connected (host-maintained).
    gamepad_connected: bool = false,

    actions: [MAX_ACTIONS]Action = undefined,
    action_count: usize = 0,

    pub fn init() Input {
        return .{};
    }

    // --- Frame lifecycle (host loop) -------------------------------------

    /// Advance to a new frame: roll current state into the "previous" snapshot
    /// (so edge queries work) and clear per-frame deltas. Call BEFORE pumping
    /// the platform event queue.
    pub fn newFrame(self: *Input) void {
        self.keys_prev = self.keys_down;
        self.mouse_prev = self.mouse_down;
        self.pad_buttons_prev = self.pad_buttons;
        self.pad_axes_prev = self.pad_axes;
        self.mouse_dx = 0;
        self.mouse_dy = 0;
        self.wheel = 0;
    }

    // --- Raw event feed (host loop) --------------------------------------

    pub fn setKey(self: *Input, key: Key, down: bool) void {
        if (down) self.keys_down.insert(key) else self.keys_down.remove(key);
    }

    pub fn setMouseButton(self: *Input, button: MouseButton, down: bool) void {
        if (down) self.mouse_down.insert(button) else self.mouse_down.remove(button);
    }

    /// Absolute pointer position (window pixels).
    pub fn setMousePosition(self: *Input, x: f32, y: f32) void {
        self.mouse_x = x;
        self.mouse_y = y;
    }

    /// Relative pointer motion for this frame (accumulates).
    pub fn addMouseMotion(self: *Input, dx: f32, dy: f32) void {
        self.mouse_dx += dx;
        self.mouse_dy += dy;
    }

    /// Scroll-wheel motion for this frame (accumulates).
    pub fn addWheel(self: *Input, delta: f32) void {
        self.wheel += delta;
    }

    pub fn setGamepadButton(self: *Input, button: GamepadButton, down: bool) void {
        if (down) self.pad_buttons.insert(button) else self.pad_buttons.remove(button);
    }

    /// Set a gamepad analog axis to `v` in [-1, 1] (host normalizes the raw value).
    pub fn setGamepadAxis(self: *Input, ax: GamepadAxis, v: f32) void {
        self.pad_axes[@intFromEnum(ax)] = v;
    }

    // --- Raw polling -----------------------------------------------------

    pub fn isKeyDown(self: *const Input, key: Key) bool {
        return self.keys_down.contains(key);
    }
    pub fn wasKeyPressed(self: *const Input, key: Key) bool {
        return self.keys_down.contains(key) and !self.keys_prev.contains(key);
    }
    pub fn wasKeyReleased(self: *const Input, key: Key) bool {
        return !self.keys_down.contains(key) and self.keys_prev.contains(key);
    }
    pub fn isMouseDown(self: *const Input, button: MouseButton) bool {
        return self.mouse_down.contains(button);
    }
    pub fn wasMousePressed(self: *const Input, button: MouseButton) bool {
        return self.mouse_down.contains(button) and !self.mouse_prev.contains(button);
    }
    pub fn mousePosition(self: *const Input) Vector2 {
        return .{ .x = self.mouse_x, .y = self.mouse_y };
    }
    pub fn mouseDelta(self: *const Input) Vector2 {
        return .{ .x = self.mouse_dx, .y = self.mouse_dy };
    }
    pub fn wheelDelta(self: *const Input) f32 {
        return self.wheel;
    }
    pub fn isGamepadButtonDown(self: *const Input, button: GamepadButton) bool {
        return self.pad_buttons.contains(button);
    }
    /// Deadzoned analog axis value in [-1, 1].
    pub fn gamepadAxis(self: *const Input, ax: GamepadAxis) f32 {
        return applyDeadzone(self.pad_axes[@intFromEnum(ax)]);
    }

    /// Magnitude in [0, 1] of one binding source. `prev` selects the previous frame.
    fn bindingValue(self: *const Input, b: Binding, prev: bool) f32 {
        return switch (b) {
            .key => |k| boolVal((if (prev) self.keys_prev else self.keys_down).contains(k)),
            .mouse => |m| boolVal((if (prev) self.mouse_prev else self.mouse_down).contains(m)),
            .gamepad_button => |g| boolVal((if (prev) self.pad_buttons_prev else self.pad_buttons).contains(g)),
            .gamepad_axis => |ga| blk: {
                const raw = if (prev) self.pad_axes_prev[@intFromEnum(ga.axis)] else self.pad_axes[@intFromEnum(ga.axis)];
                const v = applyDeadzone(raw);
                break :blk if (ga.positive) @max(0, v) else @max(0, -v);
            },
        };
    }

    // --- Action configuration --------------------------------------------

    fn addAction(self: *Input, comptime_name: []const u8, kind: ActionKind) *Action {
        std.debug.assert(self.action_count < MAX_ACTIONS);
        const a = &self.actions[self.action_count];
        a.* = .{ .kind = kind };
        const n = @min(comptime_name.len, NAME_MAX);
        @memcpy(a.name_buf[0..n], comptime_name[0..n]);
        a.name_len = @intCast(n);
        self.action_count += 1;
        return a;
    }

    pub fn defineButton(self: *Input, action_name: []const u8, bindings: []const Binding) void {
        const a = self.addAction(action_name, .button);
        a.pos = BindingSet.fromSlice(bindings);
    }

    pub fn defineAxis(
        self: *Input,
        action_name: []const u8,
        positive: []const Binding,
        negative: []const Binding,
    ) void {
        const a = self.addAction(action_name, .axis);
        a.pos = BindingSet.fromSlice(positive);
        a.neg = BindingSet.fromSlice(negative);
    }

    pub fn defineVector(
        self: *Input,
        action_name: []const u8,
        right: []const Binding,
        left: []const Binding,
        up: []const Binding,
        down: []const Binding,
    ) void {
        const a = self.addAction(action_name, .vector);
        a.pos = BindingSet.fromSlice(right);
        a.neg = BindingSet.fromSlice(left);
        a.up = BindingSet.fromSlice(up);
        a.down = BindingSet.fromSlice(down);
    }

    pub fn findAction(self: *const Input, action_name: []const u8) ?*const Action {
        for (self.actions[0..self.action_count]) |*a| {
            if (std.mem.eql(u8, a.name(), action_name)) return a;
        }
        return null;
    }

    // --- Typed handles: strings at rest, dense handles at runtime,
    // porting `engine.ui.UiEvents`' resolution pattern. `ActionId` is just the
    // action's stable index into `actions` — indices never change after
    // `defineButton`/`defineAxis`/`defineVector` (only appended to, never
    // removed/reordered) — so resolving once (e.g. in a script's `awake`) and
    // polling by id every frame after is zero string work, same as `fireId`.

    pub const ActionId = u32;
    pub const INVALID_ACTION_ID: ActionId = std.math.maxInt(ActionId);

    /// Resolve `action_name` to its dense id, or null if no action with that
    /// name has been defined (yet).
    pub fn resolve(self: *const Input, action_name: []const u8) ?ActionId {
        for (self.actions[0..self.action_count], 0..) |*a, i| {
            if (std.mem.eql(u8, a.name(), action_name)) return @intCast(i);
        }
        return null;
    }

    /// Like `resolve`, but logs a warning listing every defined action name
    /// when `action_name` doesn't resolve — for load-time binding resolution
    /// (mirrors `UiEvents.resolveOrWarn`).
    pub fn resolveOrWarn(self: *const Input, action_name: []const u8) ?ActionId {
        if (self.resolve(action_name)) |id| return id;
        var buf: [1024]u8 = undefined;
        var w: std.Io.Writer = .fixed(&buf);
        w.print("unknown input action \"{s}\"; defined: ", .{action_name}) catch {};
        for (self.actions[0..self.action_count], 0..) |*a, i| {
            if (i != 0) w.writeAll(", ") catch {};
            w.writeAll(a.name()) catch {};
        }
        if (self.action_count == 0) w.writeAll("(none)") catch {};
        std.log.warn("{s}", .{w.buffered()});
        return null;
    }

    fn actionById(self: *const Input, id: ActionId) ?*const Action {
        if (id >= self.action_count) return null;
        return &self.actions[id];
    }

    /// Id-based counterpart of `isPressed` — zero string work.
    pub fn isPressedId(self: *const Input, id: ActionId) bool {
        const a = self.actionById(id) orelse return false;
        return a.pos.value(self) >= PRESS_THRESHOLD;
    }

    /// Id-based counterpart of `wasPressed` — zero string work.
    pub fn wasPressedId(self: *const Input, id: ActionId) bool {
        const a = self.actionById(id) orelse return false;
        return a.pos.value(self) >= PRESS_THRESHOLD and a.pos.valuePrev(self) < PRESS_THRESHOLD;
    }

    /// Id-based counterpart of `axis` — zero string work.
    pub fn axisId(self: *const Input, id: ActionId) f32 {
        const a = self.actionById(id) orelse return 0;
        return std.math.clamp(a.pos.value(self) - a.neg.value(self), -1, 1);
    }

    /// Id-based counterpart of `vector` — zero string work.
    pub fn vectorId(self: *const Input, id: ActionId) Vector2 {
        const a = self.actionById(id) orelse return .{ .x = 0, .y = 0 };
        return .{
            .x = std.math.clamp(a.pos.value(self) - a.neg.value(self), -1, 1),
            .y = std.math.clamp(a.up.value(self) - a.down.value(self), -1, 1),
        };
    }

    // --- Action polling --------------------------------------------------

    /// True while a button action's strongest source is past the press threshold
    /// (so an analog trigger counts once pressed far enough).
    pub fn isPressed(self: *const Input, action_name: []const u8) bool {
        const a = self.findAction(action_name) orelse return false;
        return a.pos.value(self) >= PRESS_THRESHOLD;
    }

    /// True only on the frame a button action crosses the press threshold up → down.
    pub fn wasPressed(self: *const Input, action_name: []const u8) bool {
        const a = self.findAction(action_name) orelse return false;
        return a.pos.value(self) >= PRESS_THRESHOLD and a.pos.valuePrev(self) < PRESS_THRESHOLD;
    }

    /// 1-D value in [-1, 1] for an axis action (analog-aware).
    pub fn axis(self: *const Input, action_name: []const u8) f32 {
        const a = self.findAction(action_name) orelse return 0;
        return std.math.clamp(a.pos.value(self) - a.neg.value(self), -1, 1);
    }

    /// 2-D value (each component in [-1, 1]) for a vector action (analog-aware).
    /// Not normalized; callers normalize if they need uniform diagonal speed.
    pub fn vector(self: *const Input, action_name: []const u8) Vector2 {
        const a = self.findAction(action_name) orelse return .{ .x = 0, .y = 0 };
        return .{
            .x = std.math.clamp(a.pos.value(self) - a.neg.value(self), -1, 1),
            .y = std.math.clamp(a.up.value(self) - a.down.value(self), -1, 1),
        };
    }

    // --- Runtime rebinding -----------------------------------------------

    /// Which binding role of an action to rebind (button uses `pos`; axis uses
    /// `pos`/`neg`; vector uses all four).
    pub const Role = enum { pos, neg, up, down };

    fn findActionMut(self: *Input, action_name: []const u8) ?*Action {
        for (self.actions[0..self.action_count]) |*a| {
            if (std.mem.eql(u8, a.name(), action_name)) return a;
        }
        return null;
    }

    /// Replace (or append) the binding at (action, role, index) — for a "press a
    /// key to rebind" settings UI. Pass an index past the end to append. Returns
    /// false if the action is unknown or the role is already full.
    pub fn rebind(self: *Input, action_name: []const u8, role: Role, index: usize, binding: Binding) bool {
        const a = self.findActionMut(action_name) orelse return false;
        const set: *BindingSet = switch (role) {
            .pos => &a.pos,
            .neg => &a.neg,
            .up => &a.up,
            .down => &a.down,
        };
        if (index < set.len) {
            set.items[index] = binding;
        } else {
            if (set.len >= MAX_BINDINGS) return false;
            set.items[set.len] = binding;
            set.len += 1;
        }
        return true;
    }

    /// Scan for the first input that became active *this frame*, as a re-bindable
    /// source: keys, mouse buttons, gamepad buttons, and gamepad axes/triggers
    /// crossing the press threshold. Returns null if nothing new fired. Call each
    /// frame while a rebind UI is "listening for input".
    pub fn captureBinding(self: *const Input) ?Binding {
        inline for (@typeInfo(Key).@"enum".fields) |f| {
            const k: Key = @enumFromInt(f.value);
            if (k != .unknown and self.wasKeyPressed(k)) return .{ .key = k };
        }
        inline for (@typeInfo(MouseButton).@"enum".fields) |f| {
            const m: MouseButton = @enumFromInt(f.value);
            if (self.wasMousePressed(m)) return .{ .mouse = m };
        }
        inline for (@typeInfo(GamepadButton).@"enum".fields) |f| {
            const g: GamepadButton = @enumFromInt(f.value);
            if (self.pad_buttons.contains(g) and !self.pad_buttons_prev.contains(g)) return .{ .gamepad_button = g };
        }
        inline for (@typeInfo(GamepadAxis).@"enum".fields) |f| {
            const ax: GamepadAxis = @enumFromInt(f.value);
            const v = applyDeadzone(self.pad_axes[f.value]);
            const pv = applyDeadzone(self.pad_axes_prev[f.value]);
            if (@abs(v) >= PRESS_THRESHOLD and @abs(pv) < PRESS_THRESHOLD)
                return .{ .gamepad_axis = .{ .axis = ax, .positive = v > 0 } };
        }
        return null;
    }
};

fn boolVal(b: bool) f32 {
    return if (b) 1 else 0;
}

/// Rescale an axis so values inside the deadzone are zero and the remaining range
/// still spans [0, 1] outward (no sudden jump at the deadzone edge).
fn applyDeadzone(v: f32) f32 {
    const mag = @abs(v);
    if (mag < DEADZONE) return 0;
    const scaled = (mag - DEADZONE) / (1.0 - DEADZONE);
    return if (v < 0) -scaled else scaled;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test "raw key state and edge detection" {
    var in = Input.init();
    try std.testing.expect(!in.isKeyDown(.w));

    in.newFrame();
    in.setKey(.w, true);
    try std.testing.expect(in.isKeyDown(.w));
    try std.testing.expect(in.wasKeyPressed(.w)); // edge: up -> down

    in.newFrame(); // held across frames
    try std.testing.expect(in.isKeyDown(.w));
    try std.testing.expect(!in.wasKeyPressed(.w)); // no longer an edge

    in.newFrame();
    in.setKey(.w, false);
    try std.testing.expect(!in.isKeyDown(.w));
    try std.testing.expect(in.wasKeyReleased(.w));
}

test "button action with multiple bindings" {
    var in = Input.init();
    in.defineButton("jump", &.{ .{ .key = .space }, .{ .mouse = .right } });

    try std.testing.expect(!in.isPressed("jump"));

    in.newFrame();
    in.setMouseButton(.right, true);
    try std.testing.expect(in.isPressed("jump"));
    try std.testing.expect(in.wasPressed("jump"));

    in.newFrame();
    try std.testing.expect(in.isPressed("jump"));
    try std.testing.expect(!in.wasPressed("jump")); // held, not a new press

    // Unknown action is inert, never crashes.
    try std.testing.expect(!in.isPressed("nope"));
}

test "resolve returns the same stable id as the action's define order" {
    var in = Input.init();
    in.defineButton("jump", &.{.{ .key = .space }});
    in.defineAxis("move_x", &.{.{ .key = .d }}, &.{.{ .key = .a }});

    const jump_id = in.resolve("jump").?;
    const move_id = in.resolve("move_x").?;
    try std.testing.expect(jump_id != move_id);
    try std.testing.expectEqual(@as(?Input.ActionId, null), in.resolve("nope"));

    // Resolving again returns the exact same id (stable across calls).
    try std.testing.expectEqual(jump_id, in.resolve("jump").?);
}

test "resolveOrWarn returns null and does not crash for an unknown name" {
    var in = Input.init();
    in.defineButton("jump", &.{.{ .key = .space }});
    try std.testing.expectEqual(@as(?Input.ActionId, null), in.resolveOrWarn("missing_action"));
    try std.testing.expect(in.resolveOrWarn("jump") != null);
}

test "id-based polling matches the string-based equivalent, zero string work" {
    var in = Input.init();
    in.defineButton("jump", &.{ .{ .key = .space }, .{ .mouse = .right } });
    const jump_id = in.resolve("jump").?;

    try std.testing.expectEqual(in.isPressed("jump"), in.isPressedId(jump_id));

    in.newFrame();
    in.setMouseButton(.right, true);
    try std.testing.expect(in.isPressedId(jump_id));
    try std.testing.expect(in.wasPressedId(jump_id));
    try std.testing.expectEqual(in.isPressed("jump"), in.isPressedId(jump_id));
    try std.testing.expectEqual(in.wasPressed("jump"), in.wasPressedId(jump_id));

    in.newFrame();
    try std.testing.expect(in.isPressedId(jump_id));
    try std.testing.expect(!in.wasPressedId(jump_id)); // held, not a new press

    // An invalid/unresolved id is inert, never a crash.
    try std.testing.expect(!in.isPressedId(Input.INVALID_ACTION_ID));
}

test "axisId/vectorId match the string-based equivalent" {
    var in = Input.init();
    in.defineAxis("move_x", &.{.{ .key = .d }}, &.{.{ .key = .a }});
    in.defineVector(
        "move",
        &.{.{ .key = .d }},
        &.{.{ .key = .a }},
        &.{.{ .key = .w }},
        &.{.{ .key = .s }},
    );
    const axis_id = in.resolve("move_x").?;
    const vec_id = in.resolve("move").?;

    in.setKey(.d, true);
    in.setKey(.w, true);
    try std.testing.expectEqual(in.axis("move_x"), in.axisId(axis_id));
    const v_str = in.vector("move");
    const v_id = in.vectorId(vec_id);
    try std.testing.expectEqual(v_str.x, v_id.x);
    try std.testing.expectEqual(v_str.y, v_id.y);

    // Unresolved id degrades to zero, never a crash.
    try std.testing.expectEqual(@as(f32, 0), in.axisId(Input.INVALID_ACTION_ID));
    const v_invalid = in.vectorId(Input.INVALID_ACTION_ID);
    try std.testing.expectEqual(@as(f32, 0), v_invalid.x);
    try std.testing.expectEqual(@as(f32, 0), v_invalid.y);
}

test "axis action resolves to [-1, 1]" {
    var in = Input.init();
    in.defineAxis("move_x", &.{.{ .key = .d }}, &.{.{ .key = .a }});

    try std.testing.expectEqual(@as(f32, 0), in.axis("move_x"));

    in.setKey(.d, true);
    try std.testing.expectEqual(@as(f32, 1), in.axis("move_x"));

    in.setKey(.a, true); // both held cancel out
    try std.testing.expectEqual(@as(f32, 0), in.axis("move_x"));

    in.setKey(.d, false);
    try std.testing.expectEqual(@as(f32, -1), in.axis("move_x"));
}

test "vector action from WASD" {
    var in = Input.init();
    in.defineVector(
        "move",
        &.{.{ .key = .d }}, // right
        &.{.{ .key = .a }}, // left
        &.{.{ .key = .w }}, // up
        &.{.{ .key = .s }}, // down
    );

    in.setKey(.w, true);
    in.setKey(.d, true);
    const v = in.vector("move");
    try std.testing.expectEqual(@as(f32, 1), v.x);
    try std.testing.expectEqual(@as(f32, 1), v.y);
}

test "Key enum letter/number ordering is contiguous" {
    // The generated game maps SDL scancodes to keys arithmetically, assuming
    // a..z and num_1..num_9 are contiguous. Lock that contract here.
    try std.testing.expectEqual(@as(u16, 25), @intFromEnum(Key.z) - @intFromEnum(Key.a));
    try std.testing.expectEqual(@as(u16, 8), @intFromEnum(Key.num_9) - @intFromEnum(Key.num_1));
}

test "mouse delta clears each frame" {
    var in = Input.init();
    in.newFrame();
    in.addMouseMotion(5, -3);
    try std.testing.expectEqual(@as(f32, 5), in.mouseDelta().x);
    try std.testing.expectEqual(@as(f32, -3), in.mouseDelta().y);

    in.newFrame(); // delta resets, accumulators cleared
    try std.testing.expectEqual(@as(f32, 0), in.mouseDelta().x);
}

test "gamepad enum order matches SDL (arithmetic mapping contract)" {
    // The host maps SDL_GamepadButton/SDL_GamepadAxis with @enumFromInt.
    try std.testing.expectEqual(@as(u8, 0), @intFromEnum(GamepadButton.a));
    try std.testing.expectEqual(@as(u8, 3), @intFromEnum(GamepadButton.y));
    try std.testing.expectEqual(@as(u8, 11), @intFromEnum(GamepadButton.dpad_up));
    try std.testing.expectEqual(@as(u8, 0), @intFromEnum(GamepadAxis.left_x));
    try std.testing.expectEqual(@as(u8, 5), @intFromEnum(GamepadAxis.right_trigger));
}

test "analog stick drives a vector action through the deadzone" {
    var in = Input.init();
    in.defineVector(
        "move",
        &.{ .{ .key = .d }, .{ .gamepad_axis = .{ .axis = .left_x, .positive = true } } },
        &.{ .{ .key = .a }, .{ .gamepad_axis = .{ .axis = .left_x, .positive = false } } },
        &.{.{ .gamepad_axis = .{ .axis = .left_y, .positive = false } }}, // up = stick up (-Y)
        &.{.{ .gamepad_axis = .{ .axis = .left_y, .positive = true } }},
    );

    // Inside deadzone -> zero.
    in.setGamepadAxis(.left_x, 0.1);
    try std.testing.expectEqual(@as(f32, 0), in.vector("move").x);

    // Full right stick -> ~1 (deadzone-rescaled).
    in.setGamepadAxis(.left_x, 1.0);
    try std.testing.expectApproxEqAbs(@as(f32, 1), in.vector("move").x, 1e-5);

    // Keyboard still works on the same action (max of sources).
    in.setGamepadAxis(.left_x, 0);
    in.setKey(.d, true);
    try std.testing.expectEqual(@as(f32, 1), in.vector("move").x);
}

test "gamepad button drives a button action; trigger crosses threshold" {
    var in = Input.init();
    in.defineButton("jump", &.{.{ .gamepad_button = .a }});
    in.defineButton("fire", &.{.{ .gamepad_axis = .{ .axis = .right_trigger, .positive = true } }});

    in.newFrame();
    in.setGamepadButton(.a, true);
    try std.testing.expect(in.isPressed("jump"));
    try std.testing.expect(in.wasPressed("jump"));

    try std.testing.expect(!in.isPressed("fire"));
    in.setGamepadAxis(.right_trigger, 0.8); // past PRESS_THRESHOLD
    try std.testing.expect(in.isPressed("fire"));
}

test "rebind replaces and appends bindings at runtime" {
    var in = Input.init();
    in.defineButton("jump", &.{.{ .key = .space }});

    try std.testing.expect(in.rebind("jump", .pos, 0, .{ .key = .enter }));
    in.setKey(.space, true);
    try std.testing.expect(!in.isPressed("jump")); // old binding replaced
    in.setKey(.enter, true);
    try std.testing.expect(in.isPressed("jump")); // new binding active

    // Append a gamepad binding to the same action.
    try std.testing.expect(in.rebind("jump", .pos, 99, .{ .gamepad_button = .a }));
    in.setKey(.enter, false);
    in.setGamepadButton(.a, true);
    try std.testing.expect(in.isPressed("jump"));

    try std.testing.expect(!in.rebind("nope", .pos, 0, .{ .key = .a })); // unknown action
}

test "captureBinding returns the first newly-pressed source" {
    var in = Input.init();
    in.newFrame();
    try std.testing.expect(in.captureBinding() == null);

    in.setKey(.j, true);
    const b = in.captureBinding() orelse return error.NothingCaptured;
    try std.testing.expectEqual(Key.j, b.key);

    in.newFrame(); // j now held, not new
    try std.testing.expect(in.captureBinding() == null);

    in.setGamepadButton(.start, true);
    const g = in.captureBinding() orelse return error.NothingCaptured;
    try std.testing.expectEqual(GamepadButton.start, g.gamepad_button);
}
