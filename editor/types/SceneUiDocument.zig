/// Serialisable UI document component (GUID-based asset reference).
pub const SceneUiDocument = struct {
    /// `.uidoc` asset GUID string. Empty means no document assigned.
    document_guid: []const u8 = "",
};
