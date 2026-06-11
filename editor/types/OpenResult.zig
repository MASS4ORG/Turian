const engine = @import("engine");

/// Result of opening a project file.
pub const OpenResult = struct {
    /// The parsed project metadata.
    project: engine.Project,
};
