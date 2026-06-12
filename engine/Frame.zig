//! Per-update context object (ADR 0001 — dependency injection).
//!
//! Gameplay code receives a single `Frame` bundling the dependencies the engine
//! offers, instead of reaching for globals. The host loop (game `main` / studio)
//! is the single composition root that constructs the services and threads them in.
//!
//! Two tiers, by design (answering "what about a fixed list of services?"):
//!   - **Built-in, high-traffic services are direct fields** (`input`, `time`) —
//!     ergonomic and zero lookup cost.
//!   - **Everything else lives in `services`**, a type-keyed registry: engine
//!     subsystems added later, and any **user-defined service** (translation,
//!     networking, save system, …). Fetch with `frame.service(MyService)`.
//!
//! `Frame` is passed to *every* script lifecycle hook that can meaningfully use it
//! (`awake`/`enable`/`start`/`update`/`disable`/`destroy`), not just `update`, so a
//! provider component can `frame.services.register(...)` in `awake` and consumers can
//! resolve it from `start`/`update`.

const Time = @import("core/Time.zig").Time;
const Input = @import("Input.zig").Input;
const Services = @import("Services.zig").Services;
const Transform = @import("scene/Transform.zig").Transform;
const SceneNode = @import("scene/SceneNode.zig").SceneNode;

/// Services made available to a script's lifecycle hooks.
pub const Frame = struct {
    /// Frame timing (delta / elapsed / frame count).
    time: Time,
    /// Polled input state. Read-only to scripts.
    input: *const Input,
    /// Transform of the object owning the script being updated.
    transform: *Transform,
    /// All scene objects (read/write).
    objects: []SceneNode,
    /// Type-keyed registry for all other engine and user-defined services.
    services: *Services,

    /// Convenience: fetch a registered service by type (null if unregistered).
    pub fn service(self: Frame, comptime T: type) ?*T {
        return self.services.get(T);
    }
};

// ---------------------------------------------------------------------------
// Tests — dependency-injection proof of concept (issue #12).
//
// A "system" depends only on what arrives through `Frame`. The test constructs
// fake services (an `Input` with scripted state, a fresh `Transform`) and
// substitutes them — no globals, no service container. This is the mockability
// the ADR requires.
// ---------------------------------------------------------------------------

const std = @import("std");

/// Representative system under test: move the owning transform from the "move"
/// vector action. Note it reaches for nothing global — every dependency is in `frame`.
fn moveSystem(frame: Frame, speed: f32) void {
    const v = frame.input.vector("move");
    frame.transform.position.x += v.x * speed * frame.time.delta;
    frame.transform.position.z -= v.y * speed * frame.time.delta; // forward = -Z
}

test "Frame threads substituted services into a system" {
    var input = Input.init();
    input.defineVector(
        "move",
        &.{.{ .key = .d }},
        &.{.{ .key = .a }},
        &.{.{ .key = .w }},
        &.{.{ .key = .s }},
    );
    input.setKey(.w, true); // request forward

    var tf: Transform = .{};
    var objs = [_]SceneNode{};
    var services = Services.init();

    const frame = Frame{
        .time = .{ .delta = 1.0, .elapsed = 0, .frame = 0 },
        .input = &input,
        .transform = &tf,
        .objects = &objs,
        .services = &services,
    };

    moveSystem(frame, 2.0);

    // "w" -> vector.y = 1 -> position.z -= 1 * 2 * 1 = -2
    try std.testing.expectEqual(@as(f32, 0), tf.position.x);
    try std.testing.expectEqual(@as(f32, -2), tf.position.z);
}

test "Frame resolves a user-defined service via the registry" {
    // A user service the engine knows nothing about.
    const SaveSystem = struct {
        slot: u32 = 0,
        fn write(self: *@This(), v: u32) void {
            self.slot = v;
        }
    };

    var input = Input.init();
    var tf: Transform = .{};
    var objs = [_]SceneNode{};
    var services = Services.init();
    var save = SaveSystem{};
    services.register(SaveSystem, &save);

    const frame = Frame{
        .time = .{ .delta = 0, .elapsed = 0, .frame = 0 },
        .input = &input,
        .transform = &tf,
        .objects = &objs,
        .services = &services,
    };

    // A system pulls a substitutable, user-defined dependency out of the context.
    frame.service(SaveSystem).?.write(42);
    try std.testing.expectEqual(@as(u32, 42), save.slot);
}
