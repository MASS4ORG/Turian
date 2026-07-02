/// com.example.spawner — sample source+plugin package.
///
/// Demonstrates the source package (#61) and plugin runtime registration (#64)
/// workflow:
///   1. The build system emits a `b.addModule("spawner", ...)` declaration for
///      this file so user scripts can `@import("spawner")`.
///   2. The generated main.zig calls `@import("spawner").register(&g_services)`
///      at startup so the `SpawnerConfig` service is available from the first
///      frame's `engine.Frame.services`.
///
/// Usage from a user script:
///   const spawner = @import("spawner");
///   pub fn awake(self: *@This(), frame: engine.Frame) void {
///       const cfg = frame.services.get(spawner.SpawnerConfig) orelse return;
///       _ = cfg;
///   }
const engine = @import("engine");

/// A trivially simple service registered by this plugin so user scripts can
/// read shared spawn configuration without hard-coding values.
pub const SpawnerConfig = struct {
    /// Maximum entities this spawner may create before it stops.
    max_entities: u32 = 100,
    /// Tag written to the debug log when a spawn occurs.
    tag: []const u8 = "spawner",
};

/// Singleton config value owned by this module; registered into Services on
/// startup so it outlives any individual frame.
var g_config: SpawnerConfig = .{};

/// Plugin entry point called by the generated main.zig at startup, before the
/// boot scene loads (issue #64). Registers `SpawnerConfig` into the engine
/// service registry so user scripts can retrieve it via `frame.services.get`.
pub fn register(services: *engine.Services) void {
    services.register(SpawnerConfig, &g_config);
}
