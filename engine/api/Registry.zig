const ComponentInfo = @import("ComponentInfo.zig").ComponentInfo;

/// C-ABI registry of component type information returned by
/// the getRegistry() entry point in user script shared libraries.
pub const Registry = extern struct {
    /// Pointer to the component info array.
    components: [*]const ComponentInfo,
    /// Number of entries.
    count: usize,
};
