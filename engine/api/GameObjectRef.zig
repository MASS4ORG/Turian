const FieldType = @import("FieldType.zig").FieldType;

/// Length of a stable GUID string (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).
pub const GUID_LEN = 36;
/// Max length of a reference buffer — holds a UUID string.
pub const MAX_REF_LEN = GUID_LEN;

/// Weak reference to a scene node by stable GUID string.
pub const GameObjectRef = struct {
    buf: [MAX_REF_LEN]u8 = .{0} ** MAX_REF_LEN,
    len: usize = 0,
    pub const _turian_ref_kind: FieldType = .game_object_ref;

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
