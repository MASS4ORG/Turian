const std = @import("std");
const ScriptFieldValue = @import("ScriptFieldValue.zig").ScriptFieldValue;

/// Maximum length of a user component type name.
pub const MAX_COMPONENT_NAME = 128;
/// Maximum length of a user component source file path.
pub const MAX_COMPONENT_SOURCE = 256;
/// Maximum number of fields per user script component.
pub const MAX_SCRIPT_FIELDS = 16;

/// Reference to a user-defined script component on a game object.
pub const UserScriptRef = struct {
    type_name: [MAX_COMPONENT_NAME]u8 = std.mem.zeroes([MAX_COMPONENT_NAME]u8),
    type_name_len: usize = 0,
    source_file: [MAX_COMPONENT_SOURCE]u8 = std.mem.zeroes([MAX_COMPONENT_SOURCE]u8),
    source_file_len: usize = 0,
    field_values: [MAX_SCRIPT_FIELDS]ScriptFieldValue = std.mem.zeroes([MAX_SCRIPT_FIELDS]ScriptFieldValue),
    /// Number of populated field values.
    field_count: usize = 0,

    /// Returns the component type name as a slice.
    pub fn typeName(self: *const @This()) []const u8 {
        return self.type_name[0..self.type_name_len];
    }

    /// Returns the source file path as a slice.
    pub fn sourceFile(self: *const @This()) []const u8 {
        return self.source_file[0..self.source_file_len];
    }

    /// Sets the component type name, truncating if necessary.
    pub fn setTypeName(self: *@This(), n: []const u8) void {
        const len = @min(n.len, MAX_COMPONENT_NAME);
        @memcpy(self.type_name[0..len], n[0..len]);
        self.type_name_len = len;
    }

    /// Sets the source file path, truncating if necessary.
    pub fn setSourceFile(self: *@This(), p: []const u8) void {
        const len = @min(p.len, MAX_COMPONENT_SOURCE);
        @memcpy(self.source_file[0..len], p[0..len]);
        self.source_file_len = len;
    }

    /// Appends a field value. No-op if MAX_SCRIPT_FIELDS reached.
    pub fn addField(self: *@This(), fv: ScriptFieldValue) void {
        if (self.field_count >= MAX_SCRIPT_FIELDS) return;
        self.field_values[self.field_count] = fv;
        self.field_count += 1;
    }
};
