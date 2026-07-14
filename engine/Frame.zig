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
const Spawner = @import("scene/Spawner.zig").Spawner;
const Vector3 = @import("root.zig").Vector3;
const ui = @import("ui/root.zig");
const assets = @import("assets/root.zig");
const TypedAssetRef = @import("api/AssetRef.zig").TypedAssetRef;

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
    /// Runtime prefab spawner, or null in contexts without one (e.g. unit tests).
    /// Use `instantiate` / `destroy` rather than touching this directly.
    spawn: ?*Spawner = null,

    /// Convenience: fetch a registered service by type (null if unregistered).
    pub fn service(self: Frame, comptime T: type) ?*T {
        return self.services.get(T);
    }

    /// Spawn an instance of the prefab with asset GUID `prefab_guid` into the
    /// active scene (Unity's `Instantiate`). `position` / `rotation` override the
    /// prefab root's transform when given. Deferred: the instance appears after
    /// the current update completes. Pass a `TypedAssetRef(.scene)` field's
    /// `.slice()` as the guid.
    pub fn instantiate(self: Frame, prefab_guid: []const u8, position: ?Vector3, rotation: ?Vector3) void {
        if (self.spawn) |s| s.instantiate(prefab_guid, position, rotation);
    }

    /// Destroy `node` and its children (Unity's `Destroy`). Deferred to the end
    /// of the update. Safe to call on the script's own object.
    pub fn destroy(self: Frame, node: *const SceneNode) void {
        if (self.spawn) |s| s.destroy(node.guidSlice());
    }

    /// The live UI document instance owned by `node`'s `ui_document`
    /// component (C4), or null when the node has none / the runtime doesn't
    /// render UI. Hold a serialized `GameObjectRef` to the owning node and
    /// call `find()` once in `awake` — see `engine/ui/UiInstance.zig`.
    pub fn uiDocument(self: Frame, node: *const SceneNode) ?*ui.UiInstance {
        const rt = self.service(ui.UiRuntime) orelse return null;
        return rt.instanceFor(node.guidSlice());
    }

    /// The shared `GameEvent` channel referenced by `ref`, or null
    /// when the runtime doesn't publish the registry (unit tests) or the
    /// channel table is full. A publisher and every subscriber referencing
    /// the same asset GUID resolve to the SAME instance, decoupled from each
    /// other — see `engine.GameEventRegistry`.
    pub fn gameEvent(self: Frame, ref: TypedAssetRef(.game_event)) ?*assets.GameEvent {
        const reg = self.service(assets.GameEventRegistry) orelse return null;
        return reg.getOrCreate(ref.slice());
    }
};

// ---------------------------------------------------------------------------
// Tests — dependency-injection proof of concept.
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
