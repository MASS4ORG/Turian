const SceneObject = @import("SceneObject.zig").SceneObject;

/// Current scene format version. v2 stores `material_guids` keyed by material
/// slot; v1 stored them per-submesh (auto-migrated on load — see `SceneIo`).
pub const CURRENT_VERSION: u32 = 2;

/// Top-level JSON scene file format.
pub const SceneFile = struct {
    /// Scene format version (see `CURRENT_VERSION`).
    version: u32 = 1,
    /// Array of serialised scene objects.
    objects: []const SceneObject = &.{},
};
