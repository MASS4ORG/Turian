const FieldType = @import("FieldType.zig").FieldType;
const MAX_REF_LEN = @import("GameObjectRef.zig").MAX_REF_LEN;

/// Asset type category filter for typed asset references.
pub const AssetFilter = enum { any, mesh, texture, audio, material };

/// Weak reference to an asset by stable GUID string.
pub const AssetRef = struct {
    buf: [MAX_REF_LEN]u8 = .{0} ** MAX_REF_LEN,
    len: usize = 0,
    pub const _turian_ref_kind: FieldType = .asset_ref;
    pub const _turian_asset_filter: AssetFilter = .any;

    /// Returns the GUID string, or empty if unset.
    pub fn slice(self: *const @This()) []const u8 {
        return self.buf[0..self.len];
    }
    /// Sets the GUID string, truncating if necessary.
    pub fn set(self: *@This(), s: []const u8) void {
        const l = @min(s.len, MAX_REF_LEN);
        @memcpy(self.buf[0..l], s[0..l]);
        @memset(self.buf[l..], 0);
        self.len = l;
    }
};

/// Returns a type like AssetRef but annotated with a compile-time asset filter.
/// The inspector uses _turian_asset_filter to restrict drag-drop to matching assets.
pub fn TypedAssetRef(comptime filter: AssetFilter) type {
    return struct {
        buf: [MAX_REF_LEN]u8 = .{0} ** MAX_REF_LEN,
        len: usize = 0,
        pub const _turian_ref_kind: FieldType = .asset_ref;
        pub const _turian_asset_filter: AssetFilter = filter;

        /// Returns the GUID string, or empty if unset.
        pub fn slice(self: *const @This()) []const u8 {
            return self.buf[0..self.len];
        }
        /// Sets the GUID string, truncating if necessary.
        pub fn set(self: *@This(), s: []const u8) void {
            const l = @min(s.len, MAX_REF_LEN);
            @memcpy(self.buf[0..l], s[0..l]);
            @memset(self.buf[l..], 0);
            self.len = l;
        }
    };
}
