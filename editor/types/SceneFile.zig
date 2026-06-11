const SceneObject = @import("SceneObject.zig").SceneObject;

/// Top-level JSON scene file format.
pub const SceneFile = struct {
    /// Scene format version (currently 1).
    version: u32 = 1,
    /// Array of serialised scene objects.
    objects: []const SceneObject = &.{},
};
