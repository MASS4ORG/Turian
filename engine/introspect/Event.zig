//! Runtime event catalog (issue #49 — event streaming / subscriptions).
//!
//! A small, stable enumeration of the runtime events external tools can
//! subscribe to over the Remote Debug Protocol. The wire name (`method`) is the
//! JSON-RPC notification method clients see; `fromMethod` maps it back. The
//! ordinal is used as a bit index in per-connection subscription sets, so keep
//! the count ≤ 32 and only ever append.

const std = @import("std");

pub const Event = enum(u5) {
    entity_created,
    entity_destroyed,
    scene_loaded,
    scene_unloaded,
    resource_reloaded,
    fps_changed,

    /// JSON-RPC notification method name for this event.
    pub fn method(self: Event) []const u8 {
        return switch (self) {
            .entity_created => "entity.created",
            .entity_destroyed => "entity.destroyed",
            .scene_loaded => "scene.loaded",
            .scene_unloaded => "scene.unloaded",
            .resource_reloaded => "resource.reloaded",
            .fps_changed => "fps.changed",
        };
    }

    /// Parse a wire method name back into an event, or null if unknown.
    pub fn fromMethod(name: []const u8) ?Event {
        inline for (std.meta.fields(Event)) |f| {
            const ev: Event = @enumFromInt(f.value);
            if (std.mem.eql(u8, ev.method(), name)) return ev;
        }
        return null;
    }
};

/// Writes the full event catalog as JSON: `[{ "name", "description" }, ...]`.
/// Consumed by `turian-cli docs export-ai-context` (issue #51).
pub fn writeCatalog(jw: *std.json.Stringify) !void {
    try jw.beginArray();
    inline for (std.meta.fields(Event)) |f| {
        const ev: Event = @enumFromInt(f.value);
        try jw.beginObject();
        try jw.objectField("name");
        try jw.write(ev.method());
        try jw.objectField("description");
        try jw.write(description(ev));
        try jw.endObject();
    }
    try jw.endArray();
}

fn description(ev: Event) []const u8 {
    return switch (ev) {
        .entity_created => "An entity was spawned in a scene.",
        .entity_destroyed => "An entity was removed from a scene.",
        .scene_loaded => "A scene became active / finished loading.",
        .scene_unloaded => "A scene was unloaded.",
        .resource_reloaded => "An asset was hot-reloaded.",
        .fps_changed => "The integer FPS bucket changed.",
    };
}

test "event round-trips through its wire method name" {
    try std.testing.expectEqual(Event.fps_changed, Event.fromMethod("fps.changed").?);
    try std.testing.expectEqual(@as(?Event, null), Event.fromMethod("nope"));
    try std.testing.expectEqualStrings("entity.created", Event.entity_created.method());
}
