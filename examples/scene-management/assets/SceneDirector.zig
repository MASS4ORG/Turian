const std = @import("std");
const engine = @import("engine");

/// Drives the Scene Management demo (issue #22). Lives in the persistent
/// bootstrap scene (camera + light) and streams the two level scenes in and out
/// through the `engine.SceneManager` service:
///
///   key 1 — switch to Level A (single: replaces the current level)
///   key 2 — switch to Level B (single)
///   key 3 — add Level B alongside the current level (additive)
///   key 4 — unload Level B
///
/// Because the bootstrap scene is marked persistent, the camera/light/director
/// survive every `single` transition (the SceneManager equivalent of keeping a
/// DontDestroyOnLoad object across scene loads).
///
/// Note: scene ids are hard-coded GUID strings here. Component fields cannot yet
/// hold scene asset references (hydration of `TypedAssetRef` fields is a pending
/// editor feature), so a real game would expose these as inspector-edited refs.
pub const SceneDirector = struct {
    pub const is_component = true;

    // Stable GUIDs authored in the matching `*.json.meta` files.
    const LEVEL_A = "10000000-0000-4000-8000-00000000000a";
    const LEVEL_B = "10000000-0000-4000-8000-00000000000b";

    booted: bool = false,
    reported: bool = false,
    frames: u32 = 0,

    pub fn start(self: *SceneDirector, frame: engine.Frame) void {
        const mgr = frame.service(engine.SceneManager) orelse {
            std.debug.print("[SceneDirector] SceneManager service unavailable\n", .{});
            return;
        };
        // Keep this bootstrap scene (camera, light, director) alive across the
        // level transitions below.
        if (mgr.getActiveScene()) |boot| mgr.setScenePersistent(boot, true);
        // Stream the first level in additively so the bootstrap scene stays loaded.
        mgr.requestLoad(LEVEL_A, .additive);
        self.booted = true;
        std.debug.print("[SceneDirector] Bootstrap ready — loading Level A (additive)\n", .{});
    }

    pub fn update(self: *SceneDirector, frame: engine.Frame) void {
        const mgr = frame.service(engine.SceneManager) orelse return;
        const input = frame.input;

        // Deferred requests apply at frame boundaries (after this update), so
        // wait a couple of frames before reporting the loaded-scene count — by
        // then the additive Level A load has taken effect (boot + Level A = 2).
        self.frames += 1;
        if (self.booted and !self.reported and self.frames >= 2) {
            self.reported = true;
            var buf: [engine.SCENE_MANAGER_MAX_SCENES]engine.SceneHandle = undefined;
            const loaded = mgr.getLoadedScenes(&buf);
            std.debug.print("[SceneDirector] {d} scene(s) loaded; keys 1-4 switch/add/unload levels\n", .{loaded.len});
        }

        if (input.wasKeyPressed(.num_1)) {
            mgr.requestLoad(LEVEL_A, .single);
            std.debug.print("[SceneDirector] Switch to Level A (single)\n", .{});
        }
        if (input.wasKeyPressed(.num_2)) {
            mgr.requestLoad(LEVEL_B, .single);
            std.debug.print("[SceneDirector] Switch to Level B (single)\n", .{});
        }
        if (input.wasKeyPressed(.num_3)) {
            mgr.requestLoad(LEVEL_B, .additive);
            std.debug.print("[SceneDirector] Add Level B (additive)\n", .{});
        }
        if (input.wasKeyPressed(.num_4)) {
            if (mgr.findById(LEVEL_B)) |h| {
                mgr.requestUnload(h);
                std.debug.print("[SceneDirector] Unload Level B\n", .{});
            }
        }
    }
};
