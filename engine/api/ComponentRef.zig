const FieldType = @import("FieldType.zig").FieldType;
const MAX_REF_LEN = @import("GameObjectRef.zig").MAX_REF_LEN;

/// Weak reference to a component by name/path.
pub const ComponentRef = struct {
    buf: [MAX_REF_LEN]u8 = .{0} ** MAX_REF_LEN,
    len: usize = 0,
    pub const _turian_ref_kind: FieldType = .component_ref;

    /// Returns the reference string as a slice.
    pub fn slice(self: *const @This()) []const u8 {
        return self.buf[0..self.len];
    }
    /// Sets the reference string, truncating if necessary.
    pub fn set(self: *@This(), s: []const u8) void {
        const l = @min(s.len, MAX_REF_LEN);
        @memcpy(self.buf[0..l], s[0..l]);
        @memset(self.buf[l..], 0);
        self.len = l;
    }
};
