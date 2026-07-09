const engine = @import("engine");

/// Freefly (no-clip) camera controller — issue #10 example.
///
/// Reads *actions*, never raw key codes, through the `engine.Frame` context
/// (ADR 0001). The bindings are **not** defined here in code — they live in the
/// data-driven `player-controls.inputactions` asset and are loaded project-wide
/// at startup, so they can be edited and reused without touching this script.
/// Works with keyboard+mouse AND a gamepad (the asset binds both):
///   - "move" (vector) ..... WASD / left stick
///   - "look_axis" (vector)  right stick (analog look)
///   - "ascend"/"descend" .. Space / LShift, gamepad A/B or triggers
///   - "boost" ............. LCtrl / left shoulder — speed multiplier
///   - "look" .............. hold Right Mouse to look with the mouse
pub const FreeflyCamera = struct {
    pub const is_component = true;

    /// Movement speed in world units per second.
    move_speed: f32 = 6.0,
    /// Degrees of rotation per pixel of mouse movement.
    look_sensitivity: f32 = 0.15,
    /// Degrees per second at full gamepad stick deflection.
    look_stick_speed: f32 = 140.0,
    /// Speed multiplier while the boost action is held.
    boost_multiplier: f32 = 3.0,

    // Typed action handles (#108): resolved once (lazily, below) instead of
    // string-compared every frame — same strings-at-rest/dense-handles port
    // as `engine.ui.UiEvents`.
    _look: engine.Input.ActionId = engine.Input.INVALID_ACTION_ID,
    _look_axis: engine.Input.ActionId = engine.Input.INVALID_ACTION_ID,
    _move: engine.Input.ActionId = engine.Input.INVALID_ACTION_ID,
    _ascend: engine.Input.ActionId = engine.Input.INVALID_ACTION_ID,
    _descend: engine.Input.ActionId = engine.Input.INVALID_ACTION_ID,
    _boost: engine.Input.ActionId = engine.Input.INVALID_ACTION_ID,
    _resolved: bool = false,

    /// Resolves every action name to its id on first use — deferred past
    /// `awake` because the `player-controls.inputactions` asset loads
    /// project-wide at startup, not before every component's `awake`.
    fn resolveActions(self: *@This(), input: *const engine.Input) void {
        if (self._resolved) return;
        self._look = input.resolveOrWarn("look") orelse return;
        self._look_axis = input.resolveOrWarn("look_axis") orelse return;
        self._move = input.resolveOrWarn("move") orelse return;
        self._ascend = input.resolveOrWarn("ascend") orelse return;
        self._descend = input.resolveOrWarn("descend") orelse return;
        self._boost = input.resolveOrWarn("boost") orelse return;
        self._resolved = true;
    }

    pub fn update(self: *@This(), frame: engine.Frame) void {
        const t = frame.transform;
        const input = frame.input;
        const dt = frame.time.delta;

        self.resolveActions(input);

        // Mouse-look while the right button is held.
        if (input.isPressedId(self._look)) {
            const d = input.mouseDelta();
            t.rotation.y -= d.x * self.look_sensitivity; // yaw
            t.rotation.x -= d.y * self.look_sensitivity; // pitch
        }
        // Analog gamepad look (right stick), framerate-independent.
        const look = input.vectorId(self._look_axis);
        t.rotation.y -= look.x * self.look_stick_speed * dt; // yaw
        t.rotation.x -= look.y * self.look_stick_speed * dt; // pitch
        t.rotation.x = @max(-89.0, @min(89.0, t.rotation.x));

        // Camera basis from the same Euler convention the renderer uses
        // (engine/SoftwareRenderer.zig: forward = rotationEuler * +Z).
        const rm = engine.Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z);
        const fwd = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
        const right = normalize(cross(.{ .x = 0, .y = 1, .z = 0 }, fwd));

        const move = input.vectorId(self._move); // x = right, y = forward
        var vy: f32 = 0;
        if (input.isPressedId(self._ascend)) vy += 1;
        if (input.isPressedId(self._descend)) vy -= 1;

        var speed = self.move_speed;
        if (input.isPressedId(self._boost)) speed *= self.boost_multiplier;
        const step = speed * dt;

        t.position.x += (right.x * move.x + fwd.x * move.y) * step;
        t.position.y += (right.y * move.x + fwd.y * move.y + vy) * step;
        t.position.z += (right.z * move.x + fwd.z * move.y) * step;
    }

    fn cross(a: engine.Vector3, b: engine.Vector3) engine.Vector3 {
        return .{
            .x = a.y * b.z - a.z * b.y,
            .y = a.z * b.x - a.x * b.z,
            .z = a.x * b.y - a.y * b.x,
        };
    }

    fn normalize(v: engine.Vector3) engine.Vector3 {
        const len = @sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        if (len <= 1e-6) return .{ .x = 1, .y = 0, .z = 0 };
        return .{ .x = v.x / len, .y = v.y / len, .z = v.z / len };
    }
};
