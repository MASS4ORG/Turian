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

    pub fn update(self: *@This(), frame: engine.Frame) void {
        const t = frame.transform;
        const input = frame.input;
        const dt = frame.time.delta;

        // Mouse-look while the right button is held.
        if (input.isPressed("look")) {
            const d = input.mouseDelta();
            t.rotation.y -= d.x * self.look_sensitivity; // yaw
            t.rotation.x -= d.y * self.look_sensitivity; // pitch
        }
        // Analog gamepad look (right stick), framerate-independent.
        const look = input.vector("look_axis");
        t.rotation.y -= look.x * self.look_stick_speed * dt; // yaw
        t.rotation.x -= look.y * self.look_stick_speed * dt; // pitch
        t.rotation.x = @max(-89.0, @min(89.0, t.rotation.x));

        // Camera basis from the same Euler convention the renderer uses
        // (engine/SoftwareRenderer.zig: forward = rotationEuler * +Z).
        const rm = engine.Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z);
        const fwd = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
        const right = normalize(cross(.{ .x = 0, .y = 1, .z = 0 }, fwd));

        const move = input.vector("move"); // x = right, y = forward
        var vy: f32 = 0;
        if (input.isPressed("ascend")) vy += 1;
        if (input.isPressed("descend")) vy -= 1;

        var speed = self.move_speed;
        if (input.isPressed("boost")) speed *= self.boost_multiplier;
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
