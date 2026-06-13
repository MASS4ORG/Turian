const engine = @import("engine");

/// Result of opening a project file.
pub const OpenResult = struct {
    /// True when a valid project.json sentinel was found at the path.
    valid: bool = false,
    /// Project metadata hydrated from ProjectSettings (empty if not found).
    project: engine.Project = .{},
};
