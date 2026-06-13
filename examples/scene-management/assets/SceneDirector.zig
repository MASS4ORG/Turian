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
/// The two levels are wired as `TypedAssetRef(.scene)` fields (issue #43): the
/// inspector shows a scene-asset picker for each, and the chosen GUID is
/// serialised into the scene JSON and hydrated back into these fields at
/// runtime. No hard-coded GUID constants.
pub const SceneDirector = struct {
    pub const is_component = true;

    /// First level, streamed in additively at startup and on key 1.
    level_a: engine.TypedAssetRef(.scene) = .{},
    /// Second level, used by keys 2 (single) / 3 (additive) / 4 (unload).
    level_b: engine.TypedAssetRef(.scene) = .{},

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
        mgr.requestLoad(self.level_a.guid(), .additive);
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
            mgr.requestLoad(self.level_a.guid(), .single);
            std.debug.print("[SceneDirector] Switch to Level A (single)\n", .{});
        }
        if (input.wasKeyPressed(.num_2)) {
            mgr.requestLoad(self.level_b.guid(), .single);
            std.debug.print("[SceneDirector] Switch to Level B (single)\n", .{});
        }
        if (input.wasKeyPressed(.num_3)) {
            mgr.requestLoad(self.level_b.guid(), .additive);
            std.debug.print("[SceneDirector] Add Level B (additive)\n", .{});
        }
        if (input.wasKeyPressed(.num_4)) {
            if (mgr.findById(self.level_b.guid())) |h| {
                mgr.requestUnload(h);
                std.debug.print("[SceneDirector] Unload Level B\n", .{});
            }
        }
    }
};
